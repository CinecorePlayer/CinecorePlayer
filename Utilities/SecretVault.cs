#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Chiavi e credenziali del player, mai in chiaro ne' nel codice ne' sul disco.
    /// - Quelle dell'utente (chiave TMDb personale, account, token) sono cifrate con DPAPI:
    ///   le puo' leggere solo lo stesso utente Windows su questo PC.
    /// - Quelle integrate nel programma (devono funzionare su ogni PC) sono cifrate con AES-GCM
    ///   e una chiave ricavata dal programma stesso: non compaiono nel sorgente, nell'eseguibile
    ///   o in una ricerca di stringhe, ma chi smonta il programma puo' comunque ricavarle.
    ///   E' il limite di qualsiasi chiave distribuita dentro un'applicazione.
    /// </summary>
    internal static class SecretVault
    {
        private const string UserPrefix = "dpapi:";
        private static readonly byte[] UserEntropy = Encoding.UTF8.GetBytes("CinecorePlayer2025/secrets/v1");

        // ----- Chiavi integrate (AES-256-GCM: nonce 12 | tag 16 | testo cifrato, in base64) -----
        private static readonly Dictionary<string, string> BuiltInBlobs = new(StringComparer.Ordinal)
        {
            ["tmdb"] = "G3Tf2vpwArKjNVnb6WF6FyoWzKArTiwnzoqQh/6UTFDWed/vfN8OZ7HRsE4VVF61rPNH1Kp1sqDQlQmN",
            ["trakt.id"] = "vWR3k9eT5X94bb4hmNWS9k7uEMpkDLfwRFr1IJ0H2xUlwIPoXObDckkRO6ya8vai4HZNPDg6hz9UHkyD21MXnC+eYTAJX1E=",
            ["trakt.secret"] = "5Il73MChfPYBVN4tsTvJ7vOMQNk/4OmM2vGdkC6crPOFoFDc72ocifwvenUuj6ImNink625lmj4Mrf6XT7/xSXX3GtrUtp4=",
        };

        private static readonly Dictionary<string, string?> BuiltInCache = new(StringComparer.Ordinal);

        private static byte[] BuiltInKey()
        {
            // Pezzi sparsi, uniti solo in memoria.
            byte[] a = { 0x43, 0x9d, 0x11, 0xe7, 0x5a, 0x02, 0xb8, 0x6f, 0xd4, 0x31, 0x8c, 0x77, 0x0e, 0xa9, 0x52, 0xc6 };
            byte[] b = Encoding.UTF8.GetBytes(typeof(SecretVault).FullName + "|" + nameof(BuiltInBlobs));
            byte[] c = BitConverter.GetBytes(0x6C1E_52A7_19F3_0BD5UL);
            byte[] material = new byte[a.Length + b.Length + c.Length];
            a.CopyTo(material, 0); b.CopyTo(material, a.Length); c.CopyTo(material, a.Length + b.Length);
            for (int i = 0; i < material.Length; i++) material[i] ^= (byte)(0x3d + i * 7);
            return Rfc2898DeriveBytes.Pbkdf2(material, a, 20000, HashAlgorithmName.SHA256, 32);
        }

        /// <summary>Una chiave integrata nel programma, oppure null se non c'e'.</summary>
        public static string? BuiltIn(string name)
        {
            lock (BuiltInCache)
            {
                if (BuiltInCache.TryGetValue(name, out string? cached)) return cached;
                string? value = null;
                try
                {
                    if (BuiltInBlobs.TryGetValue(name, out string? blob))
                    {
                        byte[] data = Convert.FromBase64String(blob);
                        byte[] plain = new byte[data.Length - 28];
                        using var aes = new AesGcm(BuiltInKey(), 16);
                        aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes(name));
                        value = Encoding.UTF8.GetString(plain);
                    }
                }
                catch (Exception ex) { Dbg.Warn("[VAULT] built-in '" + name + "' unreadable: " + ex.GetType().Name); }
                return BuiltInCache[name] = value;
            }
        }

        /// <summary>Prepara una chiave da integrare: il risultato va in <see cref="BuiltInBlobs"/> sotto lo stesso nome.</summary>
        public static string SealBuiltIn(string name, string value)
        {
            byte[] plain = Encoding.UTF8.GetBytes(value);
            byte[] data = new byte[28 + plain.Length];
            RandomNumberGenerator.Fill(data.AsSpan(0, 12));
            using var aes = new AesGcm(BuiltInKey(), 16);
            aes.Encrypt(data.AsSpan(0, 12), plain, data.AsSpan(28), data.AsSpan(12, 16), Encoding.UTF8.GetBytes(name));
            return Convert.ToBase64String(data);
        }

        // ----- Dati dell'utente (DPAPI, legati all'utente Windows) -----

        public static bool IsProtected(string? stored) => stored != null && stored.StartsWith(UserPrefix, StringComparison.Ordinal);

        public static string Protect(string value) =>
            UserPrefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), UserEntropy, DataProtectionScope.CurrentUser));

        /// <summary>Il valore in chiaro, oppure null se non e' stato cifrato da questo utente su questo PC.</summary>
        public static string? Unprotect(string? stored)
        {
            if (!IsProtected(stored)) return null;
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored![UserPrefix.Length..]), UserEntropy, DataProtectionScope.CurrentUser)); }
            catch { return null; }
        }

        /// <summary>Toglie le chiavi dagli indirizzi prima di scriverli in un registro.</summary>
        public static string Redact(string? text) =>
            string.IsNullOrEmpty(text) ? string.Empty
                : System.Text.RegularExpressions.Regex.Replace(text, @"(?i)\b(api_key|apikey|key|token|access_token|client_secret|client_id)=[^&\s""']+", "$1=…");
    }
}
