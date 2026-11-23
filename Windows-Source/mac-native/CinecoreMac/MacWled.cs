using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CinecoreMac;

/// <summary>
/// Luci WLED come su Windows (PlayerForm.ConfigurationAndLighting.cs): quando parte il film si spengono con una
/// dissolvenza lunga (4,7 s), in pausa tornano com'erano dopo 2 s, alla fine tornano com'erano. Lo stato di partenza
/// (acceso/spento e luminosita') si legge da /json/state prima del primo comando e non si inventa mai: se il WLED non
/// risponde non si manda nulla di "ricostruito". Impostazioni in settings.json: wledEnabled, wledUrl.
/// </summary>
internal static class MacWled
{
    private const int FadeMs = 900, FadeOutMs = 4700, PauseRestoreDelayMs = 2000, DefaultBri = 255, MinBri = 1;
    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };

    public readonly record struct Probe(bool Reachable, bool On, int Brightness);

    public static bool Enabled
    {
        get => MacSettings.Get("wledEnabled", false);
        set => MacSettings.Set("wledEnabled", value);
    }

    public static string Address
    {
        get => MacSettings.Get("wledUrl", "");
        set { MacSettings.Set("wledUrl", value.Trim()); _captured = false; _lastSentOn = null; }
    }

    private static string? BaseUrl => Normalize(Address);

    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim();
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = "http://" + s;
        return Uri.TryCreate(s, UriKind.Absolute, out _) ? s.TrimEnd('/') : null;
    }

    // ----- stato -----
    private static bool _captured, _initialOn;
    private static int _initialBri = DefaultBri, _lastBri;
    private static bool? _lastSentOn;
    private static CancellationTokenSource? _transition, _pauseRestore;
    private enum Phase { Idle, Playing, Paused }
    private static Phase _phase = Phase.Idle;

    /// <summary>Chiamato spesso dal player: agisce solo ai cambi (film che suona, in pausa, nessun film).</summary>
    public static void Update(bool hasFilm, bool playing)
    {
        var phase = !hasFilm ? Phase.Idle : playing ? Phase.Playing : Phase.Paused;
        if (phase == _phase) return;
        var previous = _phase;
        _phase = phase;
        if (!Enabled || BaseUrl == null) return;
        Cancel(ref _pauseRestore);
        switch (phase)
        {
            case Phase.Playing:
                Run(ct => FadeAsync(false, FadeOutMs, ct));
                break;
            case Phase.Paused:
            {
                var cts = _pauseRestore = new CancellationTokenSource();
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(PauseRestoreDelayMs, cts.Token); Run(c => RestoreAsync(FadeMs, c)); }
                    catch (OperationCanceledException) { }
                });
                break;
            }
            case Phase.Idle when previous != Phase.Idle:
                Run(ct => RestoreAsync(FadeMs, ct));
                break;
        }
    }

    /// <summary>Spento dalle impostazioni a film in corso: le luci tornano com'erano.</summary>
    public static void Disabled()
    {
        Cancel(ref _pauseRestore);
        if (BaseUrl != null && _captured) Run(ct => RestoreAsync(FadeMs, ct));
    }

    /// <summary>All'uscita dall'app: le luci tornano com'erano (al massimo 1,5 s di attesa).</summary>
    public static void RestoreOnExit()
    {
        if (!Enabled || BaseUrl == null || _phase == Phase.Idle) return;
        try { RestoreAsync(250, new CancellationTokenSource(1500).Token).Wait(1600); } catch { }
    }

    private static void Run(Func<CancellationToken, Task> work)
    {
        var cts = new CancellationTokenSource();
        var old = Interlocked.Exchange(ref _transition, cts);
        try { old?.Cancel(); } catch { }
        _ = Task.Run(async () =>
        {
            try { await work(cts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.Error.WriteLine("[wled] " + ex.GetType().Name + ": " + ex.Message); }
        });
    }

    private static void Cancel(ref CancellationTokenSource? cts)
    {
        var old = Interlocked.Exchange(ref cts, null);
        try { old?.Cancel(); } catch { }
    }

    public static async Task<Probe> ProbeAsync(string address, CancellationToken ct)
    {
        string? url = Normalize(address);
        if (url == null) return default;
        try
        {
            using var response = await Http.GetAsync(url + "/json/state", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return default;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            // Non basta un 200: lo stato di un WLED vero ha almeno on e bri.
            if (!root.TryGetProperty("on", out var on) || on.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !root.TryGetProperty("bri", out var bri) || bri.ValueKind != JsonValueKind.Number) return default;
            return new Probe(true, on.GetBoolean(), Math.Clamp(bri.GetInt32(), MinBri, DefaultBri));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return default; }
    }

    private static async Task EnsureCapturedAsync(CancellationToken ct)
    {
        if (_captured || BaseUrl is not { } url) return;
        var state = await ProbeAsync(url, ct).ConfigureAwait(false);
        if (!state.Reachable) return;
        _initialOn = state.On;
        _initialBri = state.Brightness;
        _lastBri = state.On ? state.Brightness : _initialBri;
        _lastSentOn = state.On;
        _captured = true;
    }

    private static async Task FadeAsync(bool on, int fadeMs, CancellationToken ct)
    {
        await EnsureCapturedAsync(ct).ConfigureAwait(false);
        int fadeDs = Math.Max(1, (int)Math.Round(fadeMs / 100.0));
        if (on)
        {
            if (_lastSentOn != true && !await SendAsync(true, MinBri, 0, ct).ConfigureAwait(false)) return;
            await SendAsync(true, _captured ? _initialBri : _lastBri > 0 ? _lastBri : DefaultBri, fadeDs, ct).ConfigureAwait(false);
            return;
        }
        if (_lastSentOn == false) return;
        // Stato non noto: niente accensioni per costruire una dissolvenza, solo lo spegnimento.
        if (_lastSentOn != true) { await SendAsync(false, null, fadeDs, ct).ConfigureAwait(false); return; }
        if (!await SendAsync(true, MinBri, fadeDs, ct).ConfigureAwait(false)) return;
        await Task.Delay(Math.Max(fadeMs + 150, 300), ct).ConfigureAwait(false);
        await SendAsync(false, null, 0, ct).ConfigureAwait(false);
    }

    private static async Task RestoreAsync(int fadeMs, CancellationToken ct)
    {
        await EnsureCapturedAsync(ct).ConfigureAwait(false);
        int fadeDs = Math.Max(0, (int)Math.Round(fadeMs / 100.0));
        if (!_captured) { await SendAsync(true, null, fadeDs, ct).ConfigureAwait(false); return; }
        // Mai bri=0 insieme a on=false: WLED ricorda da solo l'ultima luminosita'.
        if (_initialOn) await SendAsync(true, _initialBri, fadeDs, ct).ConfigureAwait(false);
        else await SendAsync(false, null, fadeDs, ct).ConfigureAwait(false);
    }

    private static async Task<bool> SendAsync(bool on, int? bri, int transitionDs, CancellationToken ct)
    {
        if (BaseUrl is not { } url) return false;
        var state = new Dictionary<string, object> { ["on"] = on, ["tt"] = transitionDs };
        if (bri is int b) state["bri"] = Math.Clamp(b, 0, 255);
        string payload = JsonSerializer.Serialize(state);
        bool ok = await PostAsync(url + "/json/state", payload, ct).ConfigureAwait(false)
               || await PostAsync(url + "/json", payload, ct).ConfigureAwait(false);
        if (!ok)
        {
            // Firmware vecchi: l'API /win.
            var legacy = new StringBuilder(url).Append("/win&T=").Append(on ? '1' : '0');
            if (bri is int lb) legacy.Append("&A=").Append(Math.Clamp(lb, 0, 255));
            legacy.Append("&TT=").Append(transitionDs);
            try { using var r = await Http.GetAsync(legacy.ToString(), ct).ConfigureAwait(false); ok = r.IsSuccessStatusCode; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { ok = false; }
        }
        if (ok) { _lastSentOn = on; if (on && bri is int sent) _lastBri = sent; }
        return ok;
    }

    private static async Task<bool> PostAsync(string url, string payload, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }
}
