using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace CinecorePlayer2025.Utilities;

// Le chiavi dell'applicazione (Trakt, Last.fm) come su Windows: stesso cifrato e stessa chiave, ricavata dal nome
// completo della classe (CinecorePlayer2025.Utilities.SecretVault), che sul Mac e' identico.
internal static partial class SecretVault
{
        // ----- Chiavi integrate (AES-256-GCM: nonce 12 | tag 16 | testo cifrato, in base64) -----
        // Solo le chiavi dell'applicazione (registrate a nome di Cinecore Player). Le chiavi personali di un
        // account (TMDb, Subdl, OpenSubtitles...) le inserisce ogni utente e restano nel suo profilo.
        // I valori stanno in SecretVault.Keys.cs, che non si pubblica (vedi .gitignore). Senza quel file l'app compila e
        // funziona lo stesso, con Trakt e Last.fm spenti: chi compila dai sorgenti registra le sue app e mette le sue chiavi.
        private static readonly Dictionary<string, string> BuiltInBlobs = CreateBuiltInBlobs();

        private static Dictionary<string, string> CreateBuiltInBlobs()
        {
            var blobs = new Dictionary<string, string>(StringComparer.Ordinal);
            AddBuiltInBlobs(blobs);
            return blobs;
        }

        static partial void AddBuiltInBlobs(Dictionary<string, string> blobs);

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
            try { return Rfc2898DeriveBytes.Pbkdf2(material, a, 20000, HashAlgorithmName.SHA256, 32); }
            catch (CryptographicException) { return Pbkdf2Sha256(material, a, 20000, 32); }
        }

        /// <summary>
        /// PBKDF2-HMAC-SHA256 (RFC 8018) fatto qui: sotto Wine BCryptDeriveKeyPBKDF2 fallisce (0xC1000008)
        /// mentre HMAC funziona. Stesso risultato della versione di sistema.
        /// </summary>
        internal static byte[] Pbkdf2Sha256(byte[] password, byte[] salt, int iterations, int length)
        {
            using var hmac = new HMACSHA256(password);
            byte[] result = new byte[length];
            byte[] block = new byte[salt.Length + 4];
            salt.CopyTo(block, 0);
            for (int index = 1, offset = 0; offset < length; index++, offset += 32)
            {
                block[^4] = (byte)(index >> 24); block[^3] = (byte)(index >> 16); block[^2] = (byte)(index >> 8); block[^1] = (byte)index;
                byte[] u = hmac.ComputeHash(block);
                byte[] t = (byte[])u.Clone();
                for (int i = 1; i < iterations; i++)
                {
                    u = hmac.ComputeHash(u);
                    for (int j = 0; j < t.Length; j++) t[j] ^= u[j];
                }
                Array.Copy(t, 0, result, offset, Math.Min(32, length - offset));
            }
            return result;
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

        /// <summary>Toglie le chiavi dagli indirizzi prima di scriverli in un registro (come SecretVault di Windows).</summary>
        public static string Redact(string? text) =>
            string.IsNullOrEmpty(text) ? string.Empty
                : System.Text.RegularExpressions.Regex.Replace(text, @"(?i)\b(api_key|apikey|key|token|access_token|client_secret|client_id)=[^&\s""']+", "$1=…");
}
