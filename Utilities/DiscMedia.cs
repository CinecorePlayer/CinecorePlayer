#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace CinecorePlayer2025.Utilities
{
    internal enum DiscKind { None, Bluray, Dvd }

    /// <summary>
    /// Dischi video: cartella con BDMV o VIDEO_TS, unita' ottica, immagine ISO. Si aprono con
    /// libbluray / dvdnav dentro libmpv ("bd://", "dvd://"), che scelgono il titolo principale.
    /// </summary>
    internal static class DiscMedia
    {
        /// <param name="Device">Cartella che contiene BDMV / VIDEO_TS, radice dell'unita' o file ISO.</param>
        /// <param name="Protected">true/false quando si vede la cartella AACS; null per una ISO (non si apre per saperlo).</param>
        public readonly record struct Source(DiscKind Kind, string Device, bool? Protected)
        {
            public string Url => Kind == DiscKind.Dvd ? "dvd://" : "bd://";
            public string DeviceOption => Kind == DiscKind.Dvd ? "dvd-device" : "bluray-device";
            public bool IsImage => File.Exists(Device);
            /// <summary>Percorso per LAV Splitter (madVR / MPC VR / EVR): solo Blu-ray visibile come cartella.</summary>
            public string? DirectShowPath => Kind == DiscKind.Bluray && !IsImage ? Path.Combine(Device, "BDMV", "index.bdmv") : null;
            /// <summary>
            /// Indirizzi per ffmpeg (analisi del contenuto), in ordine: il disco intero (titolo principale,
            /// ma ffmpeg scarta i titoli sotto i tre minuti) e poi il file video piu' grande.
            /// </summary>
            public IEnumerable<string> ProbeUrls()
            {
                if (Kind != DiscKind.Bluray || IsImage) yield break;
                yield return "bluray:" + Device;
                string? largest = null;
                try
                {
                    largest = new DirectoryInfo(Path.Combine(Device, "BDMV", "STREAM")).EnumerateFiles("*.m2ts")
                        .OrderByDescending(file => file.Length).FirstOrDefault()?.FullName;
                }
                catch { }
                if (largest != null) yield return largest;
            }
        }

        // ISO montate da Windows per questa riproduzione: percorso dell'immagine -> radice dell'unita'.
        private static readonly Dictionary<string, string> Mounts = new(StringComparer.OrdinalIgnoreCase);

        internal static void SetMount(string isoPath, string? root)
        {
            lock (Mounts)
            {
                if (root == null) Mounts.Remove(isoPath);
                else Mounts[isoPath] = root;
            }
        }

        /// <summary>
        /// Un Blu-ray senza una playlist con almeno un brano non e' riproducibile, e libmpv va in
        /// errore di memoria provando a elencarne i titoli: meglio dirlo prima.
        /// </summary>
        public static bool HasPlayableTitle(Source source)
        {
            if (source.Kind != DiscKind.Bluray || source.IsImage) return true;
            try
            {
                string folder = Path.Combine(source.Device, "BDMV", "PLAYLIST");
                if (!Directory.Exists(folder)) return false;
                foreach (string file in Directory.EnumerateFiles(folder, "*.mpls"))
                {
                    byte[] data = File.ReadAllBytes(file);
                    if (data.Length < 20 || data[0] != 'M' || data[1] != 'P' || data[2] != 'L' || data[3] != 'S') continue;
                    int start = (data[8] << 24) | (data[9] << 16) | (data[10] << 8) | data[11];
                    if (start > 0 && start + 8 <= data.Length && ((data[start + 6] << 8) | data[start + 7]) > 0) return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Dbg.Warn("[DISC] playlist check: " + ex.Message);
                return true;
            }
        }

        public static bool TryResolve(string? path, out Source source)
        {
            source = default;
            if (string.IsNullOrWhiteSpace(path) || path.Contains("://", StringComparison.Ordinal)) return false;
            try
            {
                if (Directory.Exists(path))
                    return FromFolder(path, out source);
                if (!File.Exists(path)) return false;

                string name = Path.GetFileName(path);
                string? folder = Path.GetDirectoryName(path);
                if (folder != null && (name.Equals("index.bdmv", StringComparison.OrdinalIgnoreCase) || name.Equals("MovieObject.bdmv", StringComparison.OrdinalIgnoreCase)))
                    return FromFolder(folder, out source);
                if (folder != null && name.Equals("VIDEO_TS.IFO", StringComparison.OrdinalIgnoreCase))
                    return FromFolder(folder, out source);
                if (name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                {
                    string? mounted;
                    lock (Mounts) Mounts.TryGetValue(Path.GetFullPath(path), out mounted);
                    if (mounted != null && Directory.Exists(mounted) && FromFolder(mounted, out source)) return true;
                    DiscKind kind = IsoKind(path);
                    if (kind == DiscKind.None) return false;
                    source = new Source(kind, path, null);
                    return true;
                }
            }
            catch (Exception ex) { Dbg.Warn("[DISC] resolve: " + ex.Message); }
            return false;
        }

        private static bool FromFolder(string folder, out Source source)
        {
            source = default;
            string full = Path.GetFullPath(folder);
            string leaf = Path.GetFileName(full.TrimEnd('\\', '/'));
            // Si puo' indicare anche la cartella BDMV / VIDEO_TS stessa.
            string root = (leaf.Equals("BDMV", StringComparison.OrdinalIgnoreCase) || leaf.Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase))
                && Path.GetDirectoryName(full.TrimEnd('\\', '/')) is string parent ? parent : full;

            if (File.Exists(Path.Combine(root, "BDMV", "index.bdmv")))
            {
                source = new Source(DiscKind.Bluray, root, Directory.Exists(Path.Combine(root, "AACS")) || Directory.Exists(Path.Combine(root, "BDSVM")));
                return true;
            }
            if (File.Exists(Path.Combine(root, "VIDEO_TS", "VIDEO_TS.IFO")))
            {
                source = new Source(DiscKind.Dvd, root, null);
                return true;
            }
            return false;
        }

        /// <summary>
        /// La sequenza di riconoscimento del volume (dal settore 16) dice il tipo senza leggere il
        /// file system: UDF 2.50+ ("NSR03") e' Blu-ray, UDF 1.02 ("NSR02") con ISO 9660 e' DVD-Video.
        /// </summary>
        private static DiscKind IsoKind(string path)
        {
            const int Sector = 2048;
            byte[] buffer = new byte[Sector * 16];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            if (stream.Length < Sector * 32L) return DiscKind.None;
            stream.Position = Sector * 16L;
            int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            bool nsr2 = false, nsr3 = false;
            for (int offset = 0; offset + 6 <= read; offset += Sector)
            {
                string id = Encoding.ASCII.GetString(buffer, offset + 1, 5);
                if (id == "NSR03") nsr3 = true;
                else if (id == "NSR02") nsr2 = true;
            }
            return nsr3 ? DiscKind.Bluray : nsr2 ? DiscKind.Dvd : DiscKind.None;
        }

        /// <summary>Unita' ottiche con un disco video inserito.</summary>
        public static List<(string Label, string Path)> InsertedDiscs()
        {
            var found = new List<(string, string)>();
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (drive.DriveType != DriveType.CDRom || !drive.IsReady) continue;
                    if (AudioCd.IsAudioCdPath(drive.RootDirectory.FullName, out _, out _))
                    {
                        found.Add(((AppLanguage.English ? "Audio CD (" : "CD audio (") + drive.Name.TrimEnd('\\') + ")", drive.RootDirectory.FullName));
                        continue;
                    }
                    if (!TryResolve(drive.RootDirectory.FullName, out var source)) continue;
                    string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name : drive.VolumeLabel + " (" + drive.Name.TrimEnd('\\') + ")";
                    found.Add(((source.Kind == DiscKind.Dvd ? "DVD · " : "Blu-ray · ") + label, drive.RootDirectory.FullName));
                }
            }
            catch (Exception ex) { Dbg.Warn("[DISC] drives: " + ex.Message); }
            return found;
        }

        public static string DisplayName(string path)
        {
            try
            {
                if (!TryResolve(path, out var source)) return Path.GetFileName(path);
                if (File.Exists(source.Device)) return Path.GetFileNameWithoutExtension(source.Device);
                string root = Path.GetPathRoot(source.Device) ?? "";
                if (string.Equals(root.TrimEnd('\\'), source.Device.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    string label = new DriveInfo(root).VolumeLabel;
                    return string.IsNullOrWhiteSpace(label) ? root : label;
                }
                return Path.GetFileName(source.Device.TrimEnd('\\', '/'));
            }
            catch { return path; }
        }
    }

    /// <summary>
    /// Dischi protetti (AACS / BD+): libbluray non decifra nulla da sola, carica una libreria esterna.
    /// Due strade, nessuna chiave nel programma:
    ///  - MakeMKV installato: la sua libmmbd64.dll (chiede le chiavi a MakeMKV, copre anche BD+);
    ///  - libaacs con il KEYDB.cfg dell'utente in %APPDATA%\aacs.
    /// libbluray legge LIBAACS_PATH / LIBBDPLUS_PATH con getenv del CRT: vanno scritte con il CRT
    /// (SetEnvironmentVariable non aggiorna la tabella gia' letta) e prima di creare mpv.
    /// </summary>
    internal static class DiscProtection
    {
        public enum Provider { None, MakeMkv, LibAacs }

        private static readonly object Gate = new();
        private static Provider? _prepared;

        [DllImport("ucrtbase.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
        private static extern int _wputenv_s(string name, string value);

        public static string KeyDbFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "aacs");
        public static string KeyDbPath => Path.Combine(KeyDbFolder, "KEYDB.cfg");
        public static bool HasKeyDb { get { try { return new FileInfo(KeyDbPath) is { Exists: true, Length: > 0 }; } catch { return false; } } }

        public static string? MakeMkvFolder()
        {
            var folders = new List<string?>();
            try
            {
                foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry32, Microsoft.Win32.RegistryView.Registry64 })
                {
                    using var root = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view);
                    using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MakeMKV");
                    if (key?.GetValue("UninstallString") is string uninstall)
                        folders.Add(Path.GetDirectoryName(uninstall.Trim('"')));
                }
            }
            catch { }
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MakeMKV"));
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MakeMKV"));
            return folders.FirstOrDefault(folder => !string.IsNullOrEmpty(folder) && File.Exists(Path.Combine(folder, "libmmbd64.dll")) && File.Exists(Path.Combine(folder, "makemkvcon64.exe")));
        }

        public static string? MakeMkvVersion()
        {
            try
            {
                return MakeMkvFolder() is string folder
                    ? System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(folder, "makemkvcon64.exe")).FileVersion?.TrimStart('v')
                    : null;
            }
            catch { return null; }
        }

        private static string LibAacsFolder => Path.Combine(AppContext.BaseDirectory, "third-parties", "libaacs");
        public static bool HasLibAacs => File.Exists(Path.Combine(LibAacsFolder, "libaacs.dll"));

        public static Provider Available => MakeMkvFolder() != null ? Provider.MakeMkv : HasLibAacs && HasKeyDb ? Provider.LibAacs : Provider.None;

        /// <summary>Da chiamare prima di aprire un disco: sceglie la libreria e la rende visibile a libbluray.</summary>
        public static Provider Prepare()
        {
            lock (Gate)
            {
                Provider provider = Available;
                if (_prepared == provider) return provider;
                try
                {
                    if (provider == Provider.MakeMkv)
                    {
                        string library = Path.Combine(MakeMkvFolder()!, "libmmbd64");
                        Preload(library + ".dll");
                        Point("LIBAACS_PATH", library);
                        Point("LIBBDPLUS_PATH", library);
                    }
                    else if (provider == Provider.LibAacs)
                    {
                        // libbluray cerca le dipendenze solo nella cartella del programma e in System32:
                        // quelle di libaacs si caricano qui, cosi' sono gia' in memoria.
                        foreach (string dependency in new[] { "libgpg-error-0.dll", "libgcrypt-20.dll", "libaacs.dll", "libbdplus.dll" })
                            Preload(Path.Combine(LibAacsFolder, dependency));
                        Point("LIBAACS_PATH", Path.Combine(LibAacsFolder, "libaacs"));
                        Point("LIBBDPLUS_PATH", File.Exists(Path.Combine(LibAacsFolder, "libbdplus.dll")) ? Path.Combine(LibAacsFolder, "libbdplus") : "");
                    }
                    else
                    {
                        Point("LIBAACS_PATH", "");
                        Point("LIBBDPLUS_PATH", "");
                    }
                    Dbg.Log("[DISC] protection provider: " + provider, Dbg.LogLevel.Info);
                }
                catch (Exception ex) { Dbg.Warn("[DISC] prepare: " + ex.Message); }
                _prepared = provider;
                return provider;
            }
        }

        // La libreria gia' in memoria viene trovata anche da chi la cerca solo per nome (la libbluray
        // dentro LAV Splitter e dentro ffmpeg non leggono le variabili qui sotto allo stesso modo).
        private static void Preload(string file)
        {
            if (File.Exists(file) && !NativeLibrary.TryLoad(file, out _))
                Dbg.Warn("[DISC] could not load " + Path.GetFileName(file));
        }

        // Valore vuoto = variabile rimossa. libbluray aggiunge ".dll" da sola. Ogni runtime C tiene
        // la propria copia dell'ambiente: libmpv usa ucrtbase, LAV Filters msvcrt.
        private static void Point(string name, string library)
        {
            int result = _wputenv_s(name, library);
            if (result != 0) Dbg.Warn($"[DISC] {name}: errno {result}");
            try { MsvcrtPutEnv(name, library); } catch (Exception ex) { Dbg.Warn("[DISC] msvcrt: " + ex.Message); }
        }

        [DllImport("msvcrt.dll", EntryPoint = "_wputenv_s", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MsvcrtPutEnv(string name, string value);

        public static string Status(bool english)
        {
            if (MakeMkvFolder() != null)
                return "MakeMKV " + (MakeMkvVersion() ?? "") + (english ? " detected" : " rilevato");
            if (HasLibAacs && HasKeyDb) return english ? "libaacs with your KEYDB.cfg" : "libaacs con il tuo KEYDB.cfg";
            if (HasKeyDb) return english ? "KEYDB.cfg present, libaacs missing" : "KEYDB.cfg presente, manca libaacs";
            return english ? "Not configured" : "Non configurato";
        }

        /// <summary>Copia il file di chiavi dell'utente dove libaacs lo cerca. Il contenuto non viene letto ne' registrato.</summary>
        public static bool ImportKeyDb(string sourcePath, out string error)
        {
            error = "";
            try
            {
                var info = new FileInfo(sourcePath);
                if (!info.Exists || info.Length == 0 || info.Length > 512L << 20) { error = "size"; return false; }
                // Un KEYDB e' testo ("0x<disc id> = titolo | V | 0x<chiave>"): scarta file binari o archivi scelti per errore.
                byte[] head = new byte[(int)Math.Min(info.Length, 1 << 16)];
                using (var stream = info.OpenRead()) stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
                if (Array.IndexOf(head, (byte)0) >= 0 && !(head.Length > 1 && head[0] == 0xFF && head[1] == 0xFE)) { error = "format"; return false; }
                Directory.CreateDirectory(KeyDbFolder);
                File.Copy(sourcePath, KeyDbPath, overwrite: true);
                lock (Gate) _prepared = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
