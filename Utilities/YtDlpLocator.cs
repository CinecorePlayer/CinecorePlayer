#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace CinecorePlayer2025.Utilities
{
    internal static class YtDlpLocator
    {
        public static string? Find()
        {
            try
            {
                // Copia aggiornata dal player (ComponentUpdates), nella cartella dell'utente: vale se
                // e' piu' nuova di quella arrivata con il programma.
                string updated = ComponentUpdates.UserYtDlpPath;
                bool hasUpdated = File.Exists(updated);
                foreach (var candidate in EnumerateLocalCandidatePaths())
                {
                    if (!File.Exists(candidate)) continue;
                    if (hasUpdated && ComponentUpdates.Compare(ComponentUpdates.YtDlpVersion(updated), ComponentUpdates.YtDlpVersion(candidate)) >= 0)
                        return updated;
                    return candidate;
                }
                if (hasUpdated) return updated;

                foreach (var found in FindOnPath("yt-dlp.cmd")
                    .Concat(FindOnPath("yt-dlp.bat"))
                    .Concat(FindOnPath("yt-dlp.exe"))
                    .Concat(FindOnPath("yt-dlp")))
                {
                    return found;
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Un URL (o una ricerca) arriva da fuori: casella di testo, appunti, telecomando. Finisce
        /// tra virgolette nella riga di comando di yt-dlp: una virgoletta o un carattere di
        /// controllo al suo interno chiuderebbe l'argomento e permetterebbe di aggiungere opzioni
        /// (yt-dlp ha --exec, che esegue programmi) o, con yt-dlp.cmd, comandi per cmd.exe.
        /// </summary>
        public static bool IsSafeArgumentValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            foreach (char c in value)
                if (c == '"' || c < 0x20 || c == 0x7F) return false;
            return true;
        }

        public static ProcessStartInfo CreateProcessStartInfo(string toolPath, string args)
        {
            var psi = new ProcessStartInfo
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = ResolveWorkingDirectory()
            };

            if (toolPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                toolPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            {
                psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                psi.Arguments = "/d /s /c \"\"" + toolPath + "\" " + args + "\"";
            }
            else
            {
                psi.FileName = toolPath;
                psi.Arguments = args;
            }

            return psi;
        }

        private static IEnumerable<string> EnumerateLocalCandidatePaths()
        {
            string[] names = { "yt-dlp.exe", "yt-dlp.cmd", "yt-dlp.bat", "yt-dlp" };
            foreach (var fileName in names)
            {
                foreach (var candidate in EnumerateCandidatePaths(fileName))
                    yield return candidate;
            }
        }

        private static IEnumerable<string> EnumerateCandidatePaths(string fileName)
        {
            foreach (var root in EnumerateSearchRoots())
            {
                yield return Path.Combine(root, "third-parties", "yt-dlp", fileName);
                yield return Path.Combine(root, "tools", fileName);
                yield return Path.Combine(root, "bin", fileName);
                yield return Path.Combine(root, fileName);
                yield return Path.Combine(root, "third-parties", "madVR09217", "Documents", "YoutubeConverter4K", fileName);
                yield return Path.Combine(root, "madVR09217", "Documents", "YoutubeConverter4K", fileName);
            }
        }

        private static IEnumerable<string> EnumerateSearchRoots()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddRoot(string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return;

                try
                {
                    var full = Path.GetFullPath(path);
                    if (Directory.Exists(full))
                        seen.Add(full);
                }
                catch { }
            }

            AddRoot(AppContext.BaseDirectory);
            AddRoot(AppDomain.CurrentDomain.BaseDirectory);
            AddRoot(Directory.GetCurrentDirectory());

            foreach (var seed in seen.ToArray())
            {
                try
                {
                    var dir = new DirectoryInfo(seed);
                    for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
                        AddRoot(dir.FullName);
                }
                catch { }
            }

            foreach (var root in seen)
                yield return root;
        }

        private static IEnumerable<string> FindOnPath(string fileName)
        {
            string path;
            try { path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty; }
            catch { yield break; }

            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir))
                    continue;

                string candidate;
                try { candidate = Path.Combine(dir.Trim(), fileName); }
                catch { continue; }

                if (File.Exists(candidate))
                    yield return candidate;
            }
        }

        private static string ResolveWorkingDirectory()
        {
            try
            {
                if (Directory.Exists(AppContext.BaseDirectory))
                    return AppContext.BaseDirectory;
            }
            catch { }

            try { return Directory.GetCurrentDirectory(); }
            catch { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
        }
    }
}
