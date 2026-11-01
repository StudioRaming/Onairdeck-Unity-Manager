using System.Collections.Generic;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Public endpoints of the OnAirDeck backend. Nothing here is secret: the publishable key is
    /// the same one the website ships, and every plugin-* function authenticates with the user's
    /// own opaque session token, not with this key.
    /// </summary>
    internal static class OnAirDeckConfig
    {
        public const string WebsiteUrl = "https://onairdeck.com";
        public const string FunctionsUrl = "https://pxxhdnsqezitxlkievhf.supabase.co/functions/v1";
        public const string PublishableKey = "sb_publishable_SdtMMXg2e0zHhubDu1SLxw_0zbQgv4-";

        /// <summary>
        /// Sent as ?client= on the sign-in URL for diagnostics only. The consent page deliberately
        /// ignores it: a desktop tool can't prove its identity, so it shows generic plugin text.
        /// </summary>
        public const string ClientId = "unity";

        public const string LogPrefix = "[OnAirDeck] ";

        /// <summary>
        /// VPM listings package dependencies may be downloaded from, searched in this order. A fixed list:
        /// never a URL read from a zip or from the server. Only the preview listing carries prerelease
        /// versions (for example 0.1.14-preview.2), so a preview add-on can find its exact core.
        /// </summary>
        public static readonly string[] TrustedVpmListings =
        {
            "https://studioraming.github.io/mingtoon-distribution/index.json",
            "https://studioraming.github.io/mingtoon-distribution/preview/index.json"
        };

        /// <summary>
        /// Packages that older versions shipped as a plain folder under Assets/ (no package.json, so a scan
        /// cannot find them). Installing the package over such a project would duplicate its files.
        /// </summary>
        public static readonly Dictionary<string, string> LegacyAssetFolders = new Dictionary<string, string>
        {
            { "com.studioraming.mingtoon", "Assets/StudioRaming/MingToon" }
        };

        /// <summary>
        /// The old Assets folder of <see cref="LegacyAssetFolders"/> lists what it shipped: a folder in it (folder-relative) whose
        /// *Manifest.json files hold "files": [{"path", "sha256"}] with project-relative paths (MingToon 0.1.7 and older, BOOTH .unitypackage).
        /// When every file in the folder is one of them, unchanged, the Manager moves the folder to its backup itself
        /// (<see cref="ReleaseManifestHandler"/>), so the install works without the product's own installer file.
        /// </summary>
        public static readonly Dictionary<string, string> LegacyReleaseManifestFolders = new Dictionary<string, string>
        {
            { "com.studioraming.mingtoon", "Release" }
        };

        /// <summary>
        /// Add-ons that need MingToon Core but do not say so in a package.json (owner rule 2026-11-01): license id to the core package.
        /// A package add-on that declares its core in vpmDependencies (the URP add-on pins it exactly) is found from that instead, and so is
        /// any package named "&lt;core&gt;.&lt;suffix&gt;". Ming Light Controller ships as a .unitypackage with empty vpmDependencies.
        /// </summary>
        public static readonly Dictionary<string, string> CoreAddonLicenses = new Dictionary<string, string>
        {
            { "73f4e203-ffd0-4e88-9b78-dc116fd74057", "com.studioraming.mingtoon" }   // Ming Light Controller
        };

        /// <summary>Packages that other products are add-ons of: the display name used in "install X first".</summary>
        public static readonly Dictionary<string, string> CorePackages = new Dictionary<string, string>
        {
            { "com.studioraming.mingtoon", "MingToon Core" }
        };

        /// <summary>
        /// Packages that only the Manager updates (owner decision 2026-10-31: no more MingToon updates through VCC). A VCC install of
        /// one of them is offered a move to the Manager (VpmMigration), which takes its vpm-manifest.json entries out.
        /// </summary>
        public static readonly string[] ManagerOnlyVpmPackages =
        {
            "com.studioraming.mingtoon",
            "com.studioraming.mingtoon.urp"
        };

        /// <summary>
        /// Products whose old Assets install the Manager never moves or deletes: the product's own installer does it. The Manager
        /// imports that installer (a file of the same license version whose name ends with <see cref="LegacyInstallerSpec.InstallerSuffix"/>,
        /// today MingToon-&lt;ver&gt;-update.unitypackage). The installer only cleans up; the Manager installs the package zip itself
        /// (MingToonLegacyCleanup). See LegacyHandoff.
        /// </summary>
        public static readonly Dictionary<string, LegacyInstallerSpec> LegacyInstallers = new Dictionary<string, LegacyInstallerSpec>
        {
            {
                "com.studioraming.mingtoon",
                new LegacyInstallerSpec
                {
                    Folder = "Assets/StudioRaming/MingToon",
                    InstallerSuffix = "-update.unitypackage",
                    RequestPath = "ProjectSettings/MingToonInstaller/manager-request.json",
                    ResultPath = "ProjectSettings/MingToonInstaller/manager-result.json",
                    MenuPath = "Tools/MingToon/Open VPM Installer"
                }
            }
        };

        /// <summary>
        /// Folders (and everything in them) that no legacy rule may move, whichever rule found them. A path inside one, or a folder that
        /// contains one, is never part of a plan. User presets, generated files, the product installer and MLC live here.
        /// </summary>
        public static readonly string[] NeverMoveAssetFolders =
        {
            "Assets/MingToonPresets",
            "Assets/StudioRaming/MingToonGenerated",
            "Assets/StudioRaming/MingToonInstaller",
            "Assets/StudioRaming/MingLightController",
            "Assets/MLC"
        };
    }

    /// <summary>Where one product's own installer takes over the old Assets install (see <see cref="OnAirDeckConfig.LegacyInstallers"/>).</summary>
    internal sealed class LegacyInstallerSpec
    {
        /// <summary>The old install folder, project-relative.</summary>
        public string Folder = "";
        /// <summary>The installer is the file of the license version whose name ends with this (case-insensitive).</summary>
        public string InstallerSuffix = "";
        /// <summary>Project-relative file the Manager writes to ask the installer for the clean-up (UTF-8 without BOM).</summary>
        public string RequestPath = "";
        /// <summary>Project-relative file the installer writes its answer to (matched by requestCreatedUtc).</summary>
        public string ResultPath = "";
        /// <summary>Menu item that opens the installer by hand.</summary>
        public string MenuPath = "";
    }
}
