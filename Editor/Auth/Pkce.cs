using System;
using System.Security.Cryptography;
using System.Text;

namespace OnAirDeck.UnityManager
{
    /// <summary>PKCE (RFC 7636, S256) values for the browser sign-in.</summary>
    internal static class Pkce
    {
        /// <summary>43-character verifier from 32 random bytes, and its S256 challenge.</summary>
        public static void Create(out string verifier, out string challenge)
        {
            verifier = RandomString(32);
            using (var sha = SHA256.Create())
            {
                challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            }
        }

        public static string RandomString(int byteLength)
        {
            var bytes = new byte[byteLength];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            return Base64Url(bytes);
        }

        private static string Base64Url(byte[] bytes)
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }
}
