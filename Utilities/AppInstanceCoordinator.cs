#nullable enable
using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities
{
    internal static class AppInstanceCoordinator
    {
        private const string MutexName = @"Local\CinecorePlayer2025.SingleInstance.v2";
        private const string PipeName = "CinecorePlayer2025.OpenFiles.v2";

        private static readonly object Sync = new();
        private static Mutex? _mutex;
        private static CancellationTokenSource? _serverCts;

        public static bool TryBecomePrimary()
        {
            lock (Sync)
            {
                try
                {
                    _mutex?.Dispose();
                    _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
                    return createdNew;
                }
                catch (Exception ex)
                {
                    Dbg.Warn("Single-instance mutex unavailable: " + ex.Message);
                    return true;
                }
            }
        }

        public static bool TryTakeOverAfterForwardFailure()
        {
            lock (Sync)
            {
                try
                {
                    _mutex?.Dispose();
                    _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
                    return createdNew;
                }
                catch (Exception ex)
                {
                    Dbg.Warn("Single-instance takeover failed: " + ex.Message);
                    return false;
                }
            }
        }

        public static bool TryForwardToPrimary(string[]? args)
        {
            if (args == null || args.Length == 0)
                return true;

            Exception? lastError = null;
            DateTime deadline = DateTime.UtcNow.AddSeconds(4);

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                    int timeout = Math.Clamp((int)(deadline - DateTime.UtcNow).TotalMilliseconds, 80, 500);
                    client.Connect(timeout);

                    using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
                    writer.Write(JsonSerializer.Serialize(args));
                    Dbg.Log($"Forwarded {args.Length} external open argument(s) to the primary instance.");
                    return true;
                }
                catch (Exception ex) when (ex is TimeoutException or IOException)
                {
                    lastError = ex;
                    Thread.Sleep(100);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    break;
                }
            }

            Dbg.Warn("Could not forward external open request: " + lastError?.Message);
            return false;
        }

        public static void StartServer(Action<string[]> onArgsReceived)
        {
            if (onArgsReceived == null)
                throw new ArgumentNullException(nameof(onArgsReceived));

            lock (Sync)
            {
                _serverCts?.Cancel();
                _serverCts?.Dispose();
                _serverCts = new CancellationTokenSource();
            }

            CancellationToken token = _serverCts.Token;
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(
                            PipeName,
                            PipeDirection.In,
                            NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous);

                        await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                        using var reader = new StreamReader(server, Encoding.UTF8);
                        string payload = await reader.ReadToEndAsync(token).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(payload))
                            continue;

                        string[] forwardedArgs = JsonSerializer.Deserialize<string[]>(payload) ?? Array.Empty<string>();
                        if (forwardedArgs.Length > 0)
                            onArgsReceived(forwardedArgs);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Dbg.Warn("Open-files pipe server error: " + ex.Message);
                        try { await Task.Delay(120, token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                    }
                }
            }, token);
        }

        public static void Shutdown()
        {
            lock (Sync)
            {
                try { _serverCts?.Cancel(); } catch { }
                try { _serverCts?.Dispose(); } catch { }
                _serverCts = null;

                try { _mutex?.ReleaseMutex(); } catch { }
                try { _mutex?.Dispose(); } catch { }
                _mutex = null;
            }
        }
    }
}
