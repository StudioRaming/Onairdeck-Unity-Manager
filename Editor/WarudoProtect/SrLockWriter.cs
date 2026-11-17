using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace OnAirDeck.UnityManager.WarudoProtect
{
    /// <summary>
    /// Writes the .srlock v2 container: a built .warudo encrypted so that only an account holding the seller license can
    /// open it in WARUDO (the StudioRaming plugin decrypts with a key the Atelier Manager gets from the server).
    ///
    ///   "SRLK" | 0x02 | uint32 LE headerLength | header (UTF-8 JSON) | AES-256-CBC/PKCS7 ciphertext | HMAC-SHA256 (32)
    ///   cek = 32 random bytes; encKey = SHA256(cek ‖ "enc"); macKey = SHA256(cek ‖ "mac"); the HMAC covers every byte before it.
    ///   wrap_key = RSA-OAEP-SHA1(server public key wrap_kid, cek ‖ ascii(lower-case license_id)).
    ///
    /// Only the server's PUBLIC key is here, so this code holds no secret: whoever reads it learns how files are wrapped,
    /// not how to unwrap them. Byte-level reference: Vdesk scripts/warudo-srlock.mjs (same constants, same tests).
    /// OAEP-SHA1 because Mono's RSACryptoServiceProvider only does OAEP with SHA-1; it is sound for key wrapping.
    /// Streams the whole way (backgrounds reach 2.8 GB) and checks its own output before it is renamed into place.
    /// Files above SparseThreshold use the "sparse" layout (see the constants); smaller ones are encrypted in full.
    /// </summary>
    internal static class SrLockWriter
    {
        public const string Extension = ".srlock";
        public const string CurrentKid = "srw1";

        private static readonly byte[] Magic = { (byte)'S', (byte)'R', (byte)'L', (byte)'K' };
        private const byte Version = 2;
        private const int MacLength = 32;
        private const int BufferSize = 1 << 20;

        // Layout "sparse" above this size (spec: Vdesk scripts/warudo-srlock.mjs): the StudioRaming plugin decrypts in
        // WARUDO with Mono's managed AES (~23 MB/s, HMAC ~41 MB/s measured on a 1.5 GB background), so a fully encrypted
        // 2.8 GB background would add ~3 minutes to every scene load. Sparse encrypts the head (uMod header, zip entry and
        // bundle headers), the tail (zip central directory) and a run at the start of every chunk; the rest is copied.
        public const long SparseThreshold = 64L * 1024 * 1024;
        public const long SparseHead = 16L * 1024 * 1024;
        public const long SparseTail = 16L * 1024 * 1024;
        public const long SparseChunk = 4L * 1024 * 1024;
        public const long SparseRun = 256L * 1024;
        private const string CipherFull = "aes-256-cbc+hmac-sha256";
        private const string CipherSparse = "aes-256-ctr-sparse+hmac-sha256";

        // Wrap key srw1, public half (RSA-2048, base64url). The private half exists only as a server secret.
        private const string Srw1Modulus =
            "ySOP9yEL18UUljTYAid3GqquVP2Q-rs_yh1ybtOCI6Be1mGuZbP29cRcuacC1Y0HRa_SCGiU5xlLBOEAmmsqUwWPCHogUY4HE36x4gfYr0NPxF5eqgfS_pt0f7eH7E7D6VnF8pQFfZwP5jZlvu0qNyFQ_4YKK0r7y6HanvYCUBN1fMnsNM8rbsiQJZ95IkLdF5TKN6WqIB4vxH7Z_1yHwYz_fIGLL5vbFprkWYJ_feNg-6536g4BnRh3AUzz6JiSRkSt_4GKy-oaq-8ZYZLD1_Y-F28eIvjZWsu8eYghtT_pxmm78rfzBLUlH8TDVITDLx1H2NhorE1jolab_icq1Q";
        private const string Srw1Exponent = "AQAB";

        public static readonly string[] Kinds = { "prop", "environment", "character" };

        [Serializable]
        internal sealed class Header
        {
            public int v;
            public string kind;
            public string license_id;
            public string title;
            public string source_name;
            public string mod_name;
            public string built_at;
            public long plain_size;
            public string plain_sha256;
            /// <summary>"full" or "sparse"; files from the first hook (Unity Manager PR #47) have none = full.</summary>
            public string layout;
            public string cipher;
            public string iv;
            public string wrap_alg;
            public string wrap_kid;
            public string wrap_key;
            public long sparse_head;
            public long sparse_tail;
            public long sparse_chunk;
            public long sparse_run;
        }

        public static bool IsLicenseId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length != 36) return false;
            for (int i = 0; i < 36; i++)
            {
                char c = id[i];
                bool dash = i == 8 || i == 13 || i == 18 || i == 23;
                if (dash ? c != '-' : !Uri.IsHexDigit(c)) return false;
            }
            return true;
        }

        public static bool IsKind(string kind)
        {
            return Array.IndexOf(Kinds, kind) >= 0;
        }

        /// <summary>Locks <paramref name="inputPath"/> into <paramref name="outputPath"/> (replaced atomically). Throws on any failure.</summary>
        public static Header Lock(string inputPath, string outputPath, string licenseId, string kind, string title, string modName)
        {
            licenseId = (licenseId ?? "").Trim().ToLowerInvariant();
            if (!IsLicenseId(licenseId)) throw new ArgumentException("license id is not a seller license id (uuid)");
            if (!IsKind(kind)) throw new ArgumentException("kind must be prop, environment or character");
            if (!IsUMod(inputPath)) throw new InvalidDataException("not a uMod (.warudo) file: " + inputPath);

            byte[] cek = RandomBytes(32);
            byte[] iv = RandomBytes(16);
            byte[] encKey;
            byte[] macKey;
            DeriveKeys(cek, out encKey, out macKey);

            string plainSha;
            long plainSize;
            HashFile(inputPath, out plainSha, out plainSize);
            bool sparse = plainSize > SparseThreshold;

            var header = new Header
            {
                v = Version,
                kind = kind,
                license_id = licenseId,
                title = string.IsNullOrEmpty(title) ? Path.GetFileNameWithoutExtension(inputPath) : title,
                source_name = Path.GetFileName(inputPath),
                mod_name = string.IsNullOrEmpty(modName) ? Path.GetFileNameWithoutExtension(inputPath) : modName,
                built_at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                plain_size = plainSize,
                plain_sha256 = plainSha,
                layout = sparse ? "sparse" : "full",
                cipher = sparse ? CipherSparse : CipherFull,
                iv = Convert.ToBase64String(iv),
                wrap_alg = "rsa-oaep-sha1",
                wrap_kid = CurrentKid,
                wrap_key = Convert.ToBase64String(Wrap(cek, licenseId)),
            };
            if (sparse)
            {
                header.sparse_head = SparseHead;
                header.sparse_tail = SparseTail;
                header.sparse_chunk = SparseChunk;
                header.sparse_run = SparseRun;
            }
            byte[] headerBytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(header));

            string tmp = outputPath + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                using (var hmac = new HMACSHA256(macKey))
                {
                    using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize))
                    {
                        var fixedPart = new byte[9];
                        Buffer.BlockCopy(Magic, 0, fixedPart, 0, 4);
                        fixedPart[4] = Version;
                        WriteUInt32LE(fixedPart, 5, (uint)headerBytes.Length);
                        WriteAndMac(output, hmac, fixedPart, fixedPart.Length);
                        WriteAndMac(output, hmac, headerBytes, headerBytes.Length);

                        if (sparse)
                        {
                            long[][] ranges = SparseRanges(plainSize, SparseHead, SparseTail, SparseChunk, SparseRun);
                            using (var ctr = new SparseCtr(encKey, iv))
                            using (var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize))
                            {
                                var buffer = new byte[BufferSize];
                                long pos = 0;
                                int read;
                                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    ctr.Transform(ranges, pos, buffer, read);
                                    WriteAndMac(output, hmac, buffer, read);
                                    pos += read;
                                }
                            }
                        }
                        else
                        {
                            using (Aes aes = NewAes(encKey, iv))
                            using (ICryptoTransform encryptor = aes.CreateEncryptor())
                            using (var tee = new MacTeeStream(output, hmac))
                            using (var crypto = new CryptoStream(tee, encryptor, CryptoStreamMode.Write))
                            using (var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize))
                            {
                                var buffer = new byte[BufferSize];
                                int read;
                                while ((read = input.Read(buffer, 0, buffer.Length)) > 0) crypto.Write(buffer, 0, read);
                                crypto.FlushFinalBlock();
                            }
                        }

                        hmac.TransformFinalBlock(new byte[0], 0, 0);
                        output.Write(hmac.Hash, 0, MacLength);
                    }
                }

                if (sparse) VerifySparseHead(tmp, cek, inputPath, plainSize);
                else Verify(tmp, cek, plainSha);
                if (File.Exists(outputPath)) File.Delete(outputPath);
                File.Move(tmp, outputPath);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
                throw;
            }
            return header;
        }

        /// <summary>Re-reads the written file with the key still in memory: HMAC, then a full decrypt compared to the plain hash.</summary>
        private static void Verify(string path, byte[] cek, string plainSha)
        {
            byte[] encKey;
            byte[] macKey;
            DeriveKeys(cek, out encKey, out macKey);
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize))
            {
                long size = file.Length;
                var fixedPart = new byte[9];
                ReadExactly(file, fixedPart, 9);
                long bodyStart = 9 + ReadUInt32LE(fixedPart, 5);
                if (bodyStart + 16 + MacLength > size) throw new InvalidDataException("self-check: file too short");
                var headerBytes = new byte[bodyStart - 9];
                ReadExactly(file, headerBytes, headerBytes.Length);
                var header = JsonUtility.FromJson<Header>(Encoding.UTF8.GetString(headerBytes));

                using (var hmac = new HMACSHA256(macKey))
                {
                    file.Position = 0;
                    var buffer = new byte[BufferSize];
                    long left = size - MacLength;
                    while (left > 0)
                    {
                        int read = file.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                        if (read <= 0) throw new EndOfStreamException();
                        hmac.TransformBlock(buffer, 0, read, null, 0);
                        left -= read;
                    }
                    hmac.TransformFinalBlock(new byte[0], 0, 0);
                    var tag = new byte[MacLength];
                    ReadExactly(file, tag, MacLength);
                    if (!FixedTimeEquals(hmac.Hash, tag)) throw new InvalidDataException("self-check: integrity mismatch");
                }

                using (Aes aes = NewAes(encKey, Convert.FromBase64String(header.iv)))
                using (ICryptoTransform decryptor = aes.CreateDecryptor())
                using (var body = new RangeReadStream(file, bodyStart, size - MacLength - bodyStart))
                using (var crypto = new CryptoStream(body, decryptor, CryptoStreamMode.Read))
                using (SHA256 sha = SHA256.Create())
                {
                    var buffer = new byte[BufferSize];
                    int read;
                    while ((read = crypto.Read(buffer, 0, buffer.Length)) > 0) sha.TransformBlock(buffer, 0, read, null, 0);
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    if (ToHex(sha.Hash) != plainSha) throw new InvalidDataException("self-check: decrypted bytes differ");
                }
            }
        }

        /// <summary>
        /// Sparse files: the body must have the plain length and its first 1 MB must decrypt to the input's first 1 MB
        /// (a full re-read would cost minutes on Mono for a large background; the write pass MACs exactly what it wrote).
        /// </summary>
        private static void VerifySparseHead(string path, byte[] cek, string inputPath, long plainSize)
        {
            byte[] encKey;
            byte[] macKey;
            DeriveKeys(cek, out encKey, out macKey);
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize))
            using (var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize))
            {
                var fixedPart = new byte[9];
                ReadExactly(file, fixedPart, 9);
                long bodyStart = 9 + ReadUInt32LE(fixedPart, 5);
                if (file.Length - MacLength - bodyStart != plainSize) throw new InvalidDataException("self-check: sparse length mismatch");
                var headerBytes = new byte[bodyStart - 9];
                ReadExactly(file, headerBytes, headerBytes.Length);
                var header = JsonUtility.FromJson<Header>(Encoding.UTF8.GetString(headerBytes));
                int n = (int)Math.Min(BufferSize, plainSize);
                var body = new byte[n];
                ReadExactly(file, body, n);
                var plain = new byte[n];
                ReadExactly(input, plain, n);
                long[][] ranges = SparseRanges(plainSize, header.sparse_head, header.sparse_tail, header.sparse_chunk, header.sparse_run);
                using (var ctr = new SparseCtr(encKey, Convert.FromBase64String(header.iv))) ctr.Transform(ranges, 0, body, n);
                if (!FixedTimeEquals(body, plain)) throw new InvalidDataException("self-check: decrypted head differs");
            }
        }

        /// <summary>The encrypted byte ranges [start, end) of a sparse file, merged and sorted (starts are multiples of 16).</summary>
        internal static long[][] SparseRanges(long plainSize, long head, long tail, long chunk, long run)
        {
            var list = new System.Collections.Generic.List<long[]>();
            Action<long, long> add = (a, b) => { b = Math.Min(b, plainSize); if (b > a) list.Add(new[] { a, b }); };
            add(0, Math.Min(head, plainSize));
            for (long c = 0; c < plainSize; c += chunk) add(c, c + run);
            add(Math.Max(0, plainSize - tail) / 16 * 16, plainSize);
            list.Sort((x, y) => x[0].CompareTo(y[0]));
            var merged = new System.Collections.Generic.List<long[]>();
            foreach (var r in list)
            {
                if (merged.Count > 0 && r[0] <= merged[merged.Count - 1][1])
                {
                    merged[merged.Count - 1][1] = Math.Max(merged[merged.Count - 1][1], r[1]);
                }
                else
                {
                    merged.Add(new[] { r[0], r[1] });
                }
            }
            return merged.ToArray();
        }

        /// <summary>
        /// AES-256-CTR over the encrypted ranges only: counter block = iv[0..8] ‖ BE64(offset / 16). Keystream is made in
        /// batches of counter blocks through one ECB call (a call per 16 bytes is far too slow on Mono).
        /// </summary>
        internal sealed class SparseCtr : IDisposable
        {
            private const int BatchBlocks = 4096;
            private readonly Aes aes;
            private readonly ICryptoTransform ecb;
            private readonly byte[] nonce = new byte[8];
            private readonly byte[] counters = new byte[BatchBlocks * 16];
            private readonly byte[] keystream = new byte[BatchBlocks * 16];

            public SparseCtr(byte[] encKey, byte[] iv)
            {
                Buffer.BlockCopy(iv, 0, nonce, 0, 8);
                aes = Aes.Create();
                aes.KeySize = 256;
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                aes.Key = encKey;
                ecb = aes.CreateEncryptor();
            }

            /// <summary>XOR buf[0..count) (plain offset <paramref name="pos"/>) with the keystream where it lies in a range.</summary>
            public void Transform(long[][] ranges, long pos, byte[] buf, int count)
            {
                long end = pos + count;
                foreach (long[] r in ranges)
                {
                    if (r[1] <= pos) continue;
                    if (r[0] >= end) break;
                    long from = Math.Max(r[0], pos);
                    long to = Math.Min(r[1], end);
                    long block = from / 16;
                    int skip = (int)(from % 16);
                    long at = from;
                    while (at < to)
                    {
                        long need = to - at + skip;
                        int blocks = (int)Math.Min(BatchBlocks, (need + 15) / 16);
                        for (int i = 0; i < blocks; i++)
                        {
                            int o = i * 16;
                            Buffer.BlockCopy(nonce, 0, counters, o, 8);
                            ulong b = (ulong)(block + i);
                            for (int k = 0; k < 8; k++) counters[o + 15 - k] = (byte)(b >> (8 * k));
                        }
                        ecb.TransformBlock(counters, 0, blocks * 16, keystream, 0);
                        int usable = (int)Math.Min(blocks * 16 - skip, to - at);
                        int bufAt = (int)(at - pos);
                        for (int i = 0; i < usable; i++) buf[bufAt + i] ^= keystream[skip + i];
                        at += usable;
                        block += blocks;
                        skip = 0;
                    }
                }
            }

            public void Dispose()
            {
                ecb.Dispose();
                aes.Dispose();
            }
        }

        private static byte[] Wrap(byte[] cek, string licenseId)
        {
            var payload = new byte[32 + 36];
            Buffer.BlockCopy(cek, 0, payload, 0, 32);
            Encoding.ASCII.GetBytes(licenseId, 0, 36, payload, 32);
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.ImportParameters(new RSAParameters { Modulus = FromBase64Url(Srw1Modulus), Exponent = FromBase64Url(Srw1Exponent) });
                return rsa.Encrypt(payload, true); // true = OAEP (SHA-1)
            }
        }

        private static void DeriveKeys(byte[] cek, out byte[] encKey, out byte[] macKey)
        {
            using (SHA256 sha = SHA256.Create())
            {
                encKey = sha.ComputeHash(Concat(cek, Encoding.ASCII.GetBytes("enc")));
                macKey = sha.ComputeHash(Concat(cek, Encoding.ASCII.GetBytes("mac")));
            }
        }

        private static Aes NewAes(byte[] key, byte[] iv)
        {
            Aes aes = Aes.Create();
            aes.KeySize = 256;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = key;
            aes.IV = iv;
            return aes;
        }

        private static bool IsUMod(string path)
        {
            if (!File.Exists(path)) return false;
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var head = new byte[4];
                return f.Read(head, 0, 4) == 4 && head[0] == 'U' && head[1] == 'M' && head[2] == 'O' && head[3] == 'D';
            }
        }

        private static void HashFile(string path, out string sha256Hex, out long size)
        {
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize))
            using (SHA256 sha = SHA256.Create())
            {
                size = f.Length;
                sha256Hex = ToHex(sha.ComputeHash(f));
            }
        }

        private static byte[] RandomBytes(int n)
        {
            var b = new byte[n];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(b);
            return b;
        }

        private static void WriteAndMac(Stream output, HashAlgorithm hmac, byte[] data, int count)
        {
            output.Write(data, 0, count);
            hmac.TransformBlock(data, 0, count, null, 0);
        }

        private static void WriteUInt32LE(byte[] b, int at, uint v)
        {
            b[at] = (byte)v;
            b[at + 1] = (byte)(v >> 8);
            b[at + 2] = (byte)(v >> 16);
            b[at + 3] = (byte)(v >> 24);
        }

        private static uint ReadUInt32LE(byte[] b, int at)
        {
            return (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24));
        }

        private static void ReadExactly(Stream s, byte[] b, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(b, got, count - got);
                if (n <= 0) throw new EndOfStreamException();
                got += n;
            }
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, r, 0, a.Length);
            Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
            return r;
        }

        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static string ToHex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            for (int i = 0; i < b.Length; i++) sb.Append(b[i].ToString("x2"));
            return sb.ToString();
        }

        private static byte[] FromBase64Url(string s)
        {
            string b64 = s.Replace('-', '+').Replace('_', '/');
            switch (b64.Length % 4)
            {
                case 2: b64 += "=="; break;
                case 3: b64 += "="; break;
            }
            return Convert.FromBase64String(b64);
        }

        /// <summary>Write-only pass-through that feeds every byte to the HMAC; disposing it leaves the inner stream open.</summary>
        private sealed class MacTeeStream : Stream
        {
            private readonly Stream inner;
            private readonly HashAlgorithm mac;

            public MacTeeStream(Stream inner, HashAlgorithm mac)
            {
                this.inner = inner;
                this.mac = mac;
            }

            public override bool CanRead { get { return false; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { inner.Flush(); }
            public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (count <= 0) return;
                inner.Write(buffer, offset, count);
                mac.TransformBlock(buffer, offset, count, null, 0);
            }

            protected override void Dispose(bool disposing)
            {
                // The FileStream belongs to the caller.
                base.Dispose(disposing);
            }
        }

        /// <summary>Read-only window [start, start+length) of a seekable stream; disposing it leaves the inner stream open.</summary>
        private sealed class RangeReadStream : Stream
        {
            private readonly Stream inner;
            private readonly long end;
            private long position;

            public RangeReadStream(Stream inner, long start, long length)
            {
                this.inner = inner;
                position = start;
                end = start + length;
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count)
            {
                long left = end - position;
                if (left <= 0) return 0;
                if (count > left) count = (int)left;
                inner.Position = position;
                int n = inner.Read(buffer, offset, count);
                position += n;
                return n;
            }
        }
    }
}
