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

        // The whole prerelease part of a hotfix: "hotfix.<n>", lower case, n without leading zeroes.
        private static readonly Regex HotfixPart = new Regex(@"\Ahotfix\.(0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant);

        /// <summary>
        /// Whether a version is a hotfix ("0.1.16-hotfix.1"): a stable release that ranks above the same x.y.z and below the
        /// next patch (0.1.16-beta.1 &lt; 0.1.16 &lt; 0.1.16-hotfix.1 &lt; 0.1.16-hotfix.2 &lt; 0.1.17-beta.1 &lt; 0.1.17). Owner
        /// decision 2026-11-04; the store's version compare and the MingToon package use the same rule. Any other suffix,
        /// "-hotfix" alone or "-Hotfix.1" included, stays a prerelease.
        /// </summary>
        public static bool IsHotfix(string version)
        {
            if (!IsExact(version)) return false;
            var dash = version.IndexOf('-');
            return dash >= 0 && HotfixPart.IsMatch(version.Substring(dash + 1));
        }

        /// <summary>Whether a version has a prerelease part ("1.0.0-beta.2", "0.1.15-preview.1"). A hotfix suffix is not one.</summary>
        public static bool IsPrerelease(string version)
        {
            return !string.IsNullOrEmpty(version) && version.IndexOf('-') >= 0 && !IsHotfix(version);
        }

        /// <summary>
        /// A vpmDependencies value that pins one version: "X" or "=X" (one leading "="). Anything else
        /// (ranges, wildcards, spaces) is refused. <paramref name="exact"/> is X.
        /// </summary>
        public static bool TryParsePin(string raw, out string exact)
        {
            exact = null;
            if (string.IsNullOrEmpty(raw)) return false;
            var candidate = raw[0] == '=' ? raw.Substring(1) : raw;
            if (!IsExact(candidate)) return false;
            exact = candidate;
            return true;
        }

        /// <summary>
        /// Semver precedence, with one addition: inside one x.y.z a hotfix ranks above the release and a prerelease below it,
        /// and hotfixes order by their number. Unparsable input sorts lowest.
        /// </summary>
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
            var rank = Rank(a, preA).CompareTo(Rank(b, preB));
            if (rank != 0) return rank;
            if (preA.Length == 0) return 0;   // both releases
            if (IsHotfix(a))
            {
                // Both hotfixes: by number, any length (no leading zeroes, so the longer number is the larger).
                var numA = preA.Substring(preA.IndexOf('.') + 1);
                var numB = preB.Substring(preB.IndexOf('.') + 1);
                var byLength = numA.Length.CompareTo(numB.Length);
                return byLength != 0 ? byLength : string.CompareOrdinal(numA, numB);
            }

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

        // Inside one x.y.z: 0 prerelease, 1 release, 2 hotfix.
        private static int Rank(string version, string prerelease)
        {
            if (prerelease.Length == 0) return 1;
            return IsHotfix(version) ? 2 : 0;
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
