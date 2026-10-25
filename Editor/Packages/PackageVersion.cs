using System;
using System.Text.RegularExpressions;

namespace OnAirDeck.UnityManager
{
    /// <summary>Semantic-version helpers for package versions ("0.1.16", "1.0.0-beta.2").</summary>
    internal static class PackageVersion
    {
        // The exact-version form the add-on contract allows in vpmDependencies (no ranges, no "v" prefix).
        private static readonly Regex Exact = new Regex(@"^[0-9]{1,9}\.[0-9]{1,9}\.[0-9]{1,9}(-[0-9A-Za-z.-]+)?\z", RegexOptions.CultureInvariant);

        public static bool IsExact(string version)
        {
            return !string.IsNullOrEmpty(version) && Exact.IsMatch(version);
        }

        /// <summary>Semver precedence. Prerelease sorts below the release; unparsable input sorts lowest.</summary>
        public static int Compare(string a, string b)
        {
            var okA = IsExact(a);
            var okB = IsExact(b);
            if (!okA || !okB) return okA == okB ? string.CompareOrdinal(a, b) : (okA ? 1 : -1);

            string preA, preB;
            var coreA = Split(a, out preA);
            var coreB = Split(b, out preB);
            for (var i = 0; i < 3; i++)
            {
                var cmp = coreA[i].CompareTo(coreB[i]);
                if (cmp != 0) return cmp;
            }
            if (preA.Length == 0 && preB.Length == 0) return 0;
            if (preA.Length == 0) return 1;
            if (preB.Length == 0) return -1;

            var partsA = preA.Split(new[] { '.' });
            var partsB = preB.Split(new[] { '.' });
            for (var i = 0; i < Math.Max(partsA.Length, partsB.Length); i++)
            {
                if (i >= partsA.Length) return -1;
                if (i >= partsB.Length) return 1;
                long numA, numB;
                var isNumA = long.TryParse(partsA[i], out numA);
                var isNumB = long.TryParse(partsB[i], out numB);
                int cmp;
                if (isNumA && isNumB) cmp = numA.CompareTo(numB);
                else if (isNumA) cmp = -1;
                else if (isNumB) cmp = 1;
                else cmp = string.CompareOrdinal(partsA[i], partsB[i]);
                if (cmp != 0) return cmp;
            }
            return 0;
        }

        private static long[] Split(string version, out string prerelease)
        {
            var dash = version.IndexOf('-');
            prerelease = dash >= 0 ? version.Substring(dash + 1) : "";
            var core = dash >= 0 ? version.Substring(0, dash) : version;
            var parts = core.Split(new[] { '.' });
            return new[] { long.Parse(parts[0]), long.Parse(parts[1]), long.Parse(parts[2]) };
        }
    }
}
