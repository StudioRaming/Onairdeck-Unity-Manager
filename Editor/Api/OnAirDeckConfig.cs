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
        /// <summary>
        /// The website the Manager signs in on (/plugin-auth) and opens for its own links: Atelier (owner 2026-11-04; was
        /// https://onairdeck.com). Links the server sends (product_url) keep their own host.
        /// </summary>
        public const string WebsiteUrl = "https://atelier.io.kr";
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
            { "73f4e203-ffd0-4e88-9b78-dc116fd74057", "com.studioraming.mingtoon" },  // Ming Light Controller
            { "39a68f32-437f-48e0-8082-da3b67b3c319", "com.studioraming.vmodular" }   // VModular Plus 1.0.0 (license of the Plus option)
        };

        /// <summary>
        /// Purchases that are add-ons of a core package (owner report 2026-11-03: VModular Plus needs VModular, like MingToon Core and its URP
        /// add-on): product id to the core and the asset files of the add-on option. A row is the add-on only when every file it lists
        /// is one of them, so a purchase of the base product alone never is (CoreAddons.CoreOfPurchase). Empty on purpose (desk read
        /// 2026-11-04): a VModular Plus purchase row lists the base file and the Plus file together, so it carries its own core and installs
        /// both in order; only the Plus license row is an add-on (<see cref="CoreAddonLicenses"/>).
        /// </summary>
        public static readonly Dictionary<string, PurchaseAddon> CoreAddonPurchases = new Dictionary<string, PurchaseAddon>
        {
        };

        /// <summary>
        /// Cores sold as a .unitypackage, not as a package (lead decision 2026-11-04, G4 option A): VModular 1.0.0 imports into
        /// Assets/Editor/VModularEditor and Assets/VModualr_InAvartar (the folder name is spelled that way in the package), never into
        /// Packages/. Its marker file tells that it is here; its license (0c873fc3, the product's base license, every buyer of the product
        /// has it) installs it before an add-on; a base purchase row (file 2a0417aa of product 7aac2c69) also carries it. A later package
        /// form (com.studioraming.vmodular in Packages/) counts as here through the package check, so it needs no change here.
        /// </summary>
        public static readonly Dictionary<string, FileCore> FileCores = new Dictionary<string, FileCore>
        {
            {
                "com.studioraming.vmodular", new FileCore
                {
                    LicenseIds = new[] { "0c873fc3-af50-4a2c-987f-cce1b9949185" },
                    Markers = new[] { "Assets/Editor/VModularEditor/VModularManager.cs" },
                    Folders = new[] { "Assets/Editor/VModularEditor", "Assets/VModualr_InAvartar" },
                    PurchaseProductId = "7aac2c69-e72e-46f9-a928-5a9779392165",
                    PurchaseFileIds = new[] { "2a0417aa-0f1f-43b8-a7e7-c53824bf4ff6" }
                }
            }
        };

        /// <summary>Packages that other products are add-ons of: the display name used in "install X first".</summary>
        public static readonly Dictionary<string, string> CorePackages = new Dictionary<string, string>
        {
            { "com.studioraming.mingtoon", "MingToon Core" },
            { "com.studioraming.vmodular", "VModular" }
        };

        /// <summary>
        /// Cores whose add-ons the Manager cannot list yet. Removing one is refused, so it never goes before an add-on the Manager would
        /// not see; take a core out of here when its add-on ids are in. Empty since the VModular Plus ids are in (2026-11-04).
        /// </summary>
        public static readonly string[] CoreAddonsPending = new string[0];

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

        /// <summary>
        /// Folder names (any depth, case-insensitive) that no shipped install holds: a version-control or IDE folder means the old folder is
        /// someone's working copy, not a buyer's install. Such a folder is never cleaned up by the Manager or handed to a product installer;
        /// the user is told to move it out of the project by hand (owner report 2026-11-02: a MingToon git checkout in Assets/). A file
        /// named ".git" (a worktree or submodule pointer) counts too.
        /// </summary>
        public static readonly string[] WorkingCopyFolderNames =
        {
            ".git", ".svn", ".hg", ".bzr", ".serena", ".vs", ".idea", ".vscode"
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
