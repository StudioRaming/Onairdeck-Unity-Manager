using System;
using System.Collections.Generic;
using System.IO;

namespace OnAirDeck.License
{
    /// <summary>
    /// The check a seller's editor code calls: <c>OnAirDeckLicense.Check("&lt;product id&gt;")</c>.
    /// Valid means: OnAirDeck Unity Manager is signed in on this computer (its session marker exists)
    /// and a license file under Assets/StudioRaming_Onairdeck/ carries a valid OnAirDeck signature for
    /// this product, for that account, and for the current time. Works offline.
    /// Honest limit: this is a client-side check. It stops casual sharing; it cannot stop someone who
    /// decompiles and edits the assembly that calls it.
    /// </summary>
    public static class OnAirDeckLicense
    {
        public const string LicenseFileName = "onairdeck.license";
        public const string InstallRoot = "Assets/StudioRaming_Onairdeck";
        public const string SessionMarkerFolder = "OnAirDeck";
        public const string SessionMarkerFile = "session.json";
        public const string SignInHint = "Sign in with OnAirDeck Unity Manager (Window > OnAirDeck Unity Manager) and reopen this tool. If the license is missing, download the asset again from the Manager.";

        /// <summary>Unity project root. Null means the current directory, which the Unity Editor keeps at the project folder.</summary>
        public static string ProjectRoot;

        /// <summary>Path of the Manager's session marker. Null means &lt;local app data&gt;/OnAirDeck/session.json.</summary>
        public static string SessionMarkerPath;

        /// <summary>Check(productId) results are reused for this long, so calling it from OnGUI stays cheap. 0 disables.</summary>
        public static int CacheSeconds = 30;

        internal static Func<long> Clock = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        private sealed class CacheEntry { public LicenseCheckResult Result; public long At; }
        private static readonly Dictionary<string, CacheEntry> Cache = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Finds this product's license under Assets/StudioRaming_Onairdeck/ and verifies it for the signed-in account.</summary>
        public static LicenseCheckResult Check(string productId)
        {
            var key = (productId ?? "").Trim();
            var now = Clock();
            lock (Cache)
            {
                CacheEntry hit;
                if (CacheSeconds > 0 && Cache.TryGetValue(key, out hit) && now - hit.At < CacheSeconds) return hit.Result;
            }
            var result = CheckUncached(key, now);
            lock (Cache) { Cache[key] = new CacheEntry { Result = result, At = now }; }
            return result;
        }

        /// <summary>Verifies one specific license file for the product and the signed-in account. Not cached.</summary>
        public static LicenseCheckResult Check(string productId, string licensePath)
        {
            var userId = ReadSignedInUserId();
            string text;
            try { text = File.ReadAllText(licensePath); }
            catch (IOException) { return LicenseCheckResult.Fail(LicenseStatus.LicenseMissing, "The license file could not be read: " + licensePath); }
            catch (UnauthorizedAccessException) { return LicenseCheckResult.Fail(LicenseStatus.LicenseMissing, "The license file could not be read: " + licensePath); }
            var result = LicenseVerifier.Verify(text, productId, userId, Clock());
            result.LicensePath = licensePath;
            return result;
        }

        public static void ClearCache()
        {
            lock (Cache) Cache.Clear();
        }

        /// <summary>The user id in the Manager's session marker, or null when nobody is signed in.</summary>
        public static string ReadSignedInUserId()
        {
            var path = ResolveMarkerPath();
            if (!File.Exists(path)) return null;
            string json;
            try { json = File.ReadAllText(path); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            var id = MiniJson.GetString(MiniJson.ParseObject(json), "user_id");
            return string.IsNullOrEmpty(id) ? null : id;
        }

        private static LicenseCheckResult CheckUncached(string productId, long now)
        {
            var userId = ReadSignedInUserId();
            if (string.IsNullOrEmpty(userId)) return LicenseCheckResult.Fail(LicenseStatus.NotSignedIn, SignInHint);

            var candidates = FindLicenseFiles(productId);
            if (candidates.Count == 0)
                return LicenseCheckResult.Fail(LicenseStatus.LicenseMissing, "No OnAirDeck license for this product was found under " + InstallRoot + "/. Download the asset again with OnAirDeck Unity Manager.");

            // Newest expiry first; the first valid one wins, otherwise report the newest one's problem.
            LicenseCheckResult first = null;
            foreach (var candidate in candidates)
            {
                var result = LicenseVerifier.Verify(candidate.Text, productId, userId, now);
                result.LicensePath = candidate.Path;
                if (result.IsValid) return result;
                if (first == null) first = result;
            }
            return first;
        }

        private sealed class Candidate { public string Path; public string Text; public long Exp; }

        private static List<Candidate> FindLicenseFiles(string productId)
        {
            var list = new List<Candidate>();
            var root = Path.Combine(ResolveProjectRoot(), InstallRoot.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(root)) return list;
            string[] files;
            try { files = Directory.GetFiles(root, LicenseFileName, SearchOption.AllDirectories); }
            catch (IOException) { return list; }
            catch (UnauthorizedAccessException) { return list; }
            foreach (var path in files)
            {
                string text;
                try { text = File.ReadAllText(path); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                var fields = LicenseVerifier.PeekPayload(text);
                if (fields == null || !LicenseVerifier.SameId(MiniJson.GetString(fields, "product_id"), productId)) continue;
                long exp;
                MiniJson.TryGetInt(fields, "exp", out exp);
                list.Add(new Candidate { Path = path, Text = text, Exp = exp });
            }
            list.Sort((a, b) => b.Exp.CompareTo(a.Exp));
            return list;
        }

        private static string ResolveProjectRoot()
        {
            return string.IsNullOrEmpty(ProjectRoot) ? Directory.GetCurrentDirectory() : ProjectRoot;
        }

        private static string ResolveMarkerPath()
        {
            if (!string.IsNullOrEmpty(SessionMarkerPath)) return SessionMarkerPath;
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(Path.Combine(local, SessionMarkerFolder), SessionMarkerFile);
        }
    }
}
