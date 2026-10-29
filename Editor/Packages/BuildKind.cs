using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace OnAirDeck.UnityManager
{
    /// <summary>What a version is, for its chip in the version list and for which version is offered by default.</summary>
    internal enum BuildKind { Release, Hotfix, Preview, Beta, Prerelease }

    /// <summary>
    /// Reads the build kind of a version. A package version follows semver: a prerelease part ("0.1.15-preview.1",
    /// "1.0.0-beta.2") makes it a preview, beta or other prerelease, and nothing else does. A hotfix is an ordinary
    /// patch release (semver has no hotfix rank); the seller's label ("0.1.15 Hotfix 1") only adds the Hotfix chip.
    /// A file (.unitypackage) has no version to parse, so its kind comes from the label the seller typed.
    /// </summary>
    internal static class BuildKinds
    {
        // Whole words only, so "Preset" or "Alphabet" in a label is not a prerelease. Korean and Japanese words have no
        // such collisions and are matched anywhere.
        private static readonly Regex PreviewWord = new Regex(@"(^|[^a-z])preview([^a-z]|$)|프리뷰|プレビュー", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex BetaWord = new Regex(@"(^|[^a-z])beta([^a-z]|$)|베타|ベータ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex OtherPreWord = new Regex(@"(^|[^a-z])(alpha|rc|pre)([^a-z]|$)|알파|アルファ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex HotfixWord = new Regex(@"(^|[^a-z])hotfix([^a-z]|$)|핫픽스|ホットフィックス", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Kind of a package version; <paramref name="label"/> (the seller's version label) can only add Hotfix.</summary>
        public static BuildKind OfPackage(string version, string label)
        {
            if (PackageVersion.IsPrerelease(version))
            {
                var pre = version.Substring(version.IndexOf('-') + 1);
                if (PreviewWord.IsMatch(pre)) return BuildKind.Preview;
                if (BetaWord.IsMatch(pre)) return BuildKind.Beta;
                return BuildKind.Prerelease;
            }
            return HotfixWord.IsMatch(label ?? "") ? BuildKind.Hotfix : BuildKind.Release;
        }

        /// <summary>Kind of a file version from the seller's label ("2.0.1 beta", "1.0-preview.2", "3.0 RC1", "2.0.2 핫픽스").</summary>
        public static BuildKind OfLabel(string label)
        {
            label = label ?? "";
            if (PreviewWord.IsMatch(label)) return BuildKind.Preview;
            if (BetaWord.IsMatch(label)) return BuildKind.Beta;
            if (OtherPreWord.IsMatch(label)) return BuildKind.Prerelease;
            return HotfixWord.IsMatch(label) ? BuildKind.Hotfix : BuildKind.Release;
        }

        public static bool IsPrerelease(BuildKind kind)
        {
            return kind == BuildKind.Preview || kind == BuildKind.Beta || kind == BuildKind.Prerelease;
        }

        /// <summary>
        /// Which of <paramref name="kinds"/> (newest first) to offer by default: the first release or hotfix, so a preview
        /// or beta is installed only when picked by hand. <paramref name="takePrerelease"/> (the project already runs a
        /// prerelease) offers the first of all, and so does a list with no release at all. -1 when the list is empty.
        /// </summary>
        public static int IndexOfOffered(IList<BuildKind> kinds, bool takePrerelease)
        {
            if (kinds == null || kinds.Count == 0) return -1;
            if (takePrerelease) return 0;
            for (var i = 0; i < kinds.Count; i++)
                if (!IsPrerelease(kinds[i])) return i;
            return 0;
        }
    }
}
