using System;
using System.Collections.Generic;
using System.Linq;

namespace CinecorePlayer2025.Utilities;

internal readonly record struct KeychainRead(int Status, string? Value);
internal interface IKeychainBackend
{
    KeychainRead Read(string account, bool interactive);
    int Write(string account, string value);
    int Delete(string account);
}

/// <summary>Una sola lettura per voce/sessione, inclusi rifiuti e assenze. Nessuna richiesta automatica di consenso.</summary>
internal sealed class KeychainSession(IKeychainBackend backend)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, KeychainRead> _cache = new(StringComparer.Ordinal);
    private bool _authorizing;
    private static bool Blocked(int status) => status is -25308 or -25293 or -128 or -25294;
    public bool AccessRequired { get { lock (_sync) return _cache.Values.Any(r => Blocked(r.Status)); } }
    public bool Authorizing { get { lock (_sync) return _authorizing; } }

    public string? Get(string account)
    {
        lock (_sync)
        {
            if (!_cache.TryGetValue(account, out var result))
                _cache[account] = result = _authorizing ? new(-25308, null) : backend.Read(account, false);
            return result.Status == 0 ? result.Value : null;
        }
    }

    public bool Set(string account, string value)
    {
        lock (_sync)
        {
            int status = _authorizing ? -25308 : backend.Write(account, value);
            // Non sostituire un valore funzionante se il salvataggio fallisce.
            if (status == 0) _cache[account] = new(0, value);
            else if (!_cache.TryGetValue(account, out var previous) || previous.Status != 0) _cache[account] = new(status, null);
            return status == 0;
        }
    }

    public void Delete(string account)
    {
        lock (_sync)
        {
            int status = _authorizing ? -25308 : backend.Delete(account);
            if (status == 0 || status == -25300) _cache[account] = new(-25300, null);
        }
    }

    /// <summary>Solo da un comando esplicito. Un annullamento interrompe l'intera coda.</summary>
    public bool AuthorizePending()
    {
        string[] accounts;
        lock (_sync)
        {
            if (_authorizing) return false;
            _authorizing = true;
            accounts = _cache.Where(p => Blocked(p.Value.Status)).Select(p => p.Key).ToArray();
        }
        try
        {
            foreach (string account in accounts)
            {
                var result = backend.Read(account, true);
                lock (_sync) _cache[account] = result;
                if (Blocked(result.Status)) break;
            }
            return !AccessRequired;
        }
        finally { lock (_sync) _authorizing = false; }
    }
}
