using System;
using System.Collections.Generic;
using System.Globalization;

namespace OnAirDeck.UnityManager
{
    /// <summary>Unknown: a non-package license is installed (its marker is here) but the Manager has no record of which version.</summary>
    internal enum EntryState { None, Got, New, Installed, Latest, Unknown }

    internal enum SortMode { Recent, Name, Seller, Todo }

    internal enum FilterMode { All, New, None, Installed, Favorites, Own }

    /// <summary>One line of a product's version history (a file version of a purchase or a package version of a license).</summary>
    internal sealed class VersionRow
    {
        public string Label = "";
        public string Date = "";
        public string Notes = "";
        public long Size;
        public LicenseVersionEntry License;   // package version: install this
        public PurchaseVersion Purchase;      // file version
        public bool Installed;
        public bool Latest;                   // the version offered by default (licenses: the newest release, see BuildKinds.IndexOfOffered)
        public BuildKind Kind;                // licenses: release, hotfix, preview, beta or other prerelease (its chip)
    }

    /// <summary>A purchase or a seller license as the window lists it (key "p:&lt;id&gt;" / "l:&lt;id&gt;", the same keys the favorites and the new-purchase watcher use).</summary>
    internal sealed class CatalogEntry
    {
        public string Key = "";
        public FavoriteKind Kind;
        public PurchaseItem Purchase;
        public LicenseEntry License;
        public string Name = "";
        public string SellerName = "";
        public string ThumbUrl = "";
        public string Description = "";
        public string ProductUrl = "";
        public bool IsOwn;
        public DateTime SortDate = DateTime.MinValue;
        public string BoughtDate = "";
        public readonly List<VersionRow> Versions = new List<VersionRow>();   // newest first
        public string PackageName = "";       // license: the package of its newest package version
        public string InstalledVersion = "";  // license: what this project has
        public string LatestVersion = "";     // license: the version offered by default, which is never a preview or beta unless one is installed or nothing else exists
        public EntryState State = EntryState.None;
        public int MaxDevices;
        public int DevicesUsed;

        public bool IsLicense { get { return Kind == FavoriteKind.License; } }
        public bool HasPackageVersions { get { return Kind == FavoriteKind.License && !string.IsNullOrEmpty(PackageName); } }
        public bool HasDeviceLimit { get { return Kind == FavoriteKind.License && MaxDevices > 0; } }
        public string LicenseId { get { return License != null ? License.license_id : ""; } }
    }

    /// <summary>One line of the left list: a group header or a product row.</summary>
    internal sealed class ListLine
    {
        public bool Header;
        public string GroupId = "";
        public string Title = "";
        public int Count;
        public bool FavoriteGroup;
        public bool Collapsed;
        public CatalogEntry Entry;
    }

    /// <summary>Pure list logic of the window: merging, status, search, filter, sort and grouping (no Unity types, so it can be checked headless).</summary>
    internal static class ManagerCatalog
    {
        public const string FavoriteGroupId = "fav";
        public const string AllGroupId = "all";

        public static List<CatalogEntry> Build(PurchaseItem[] purchases, LicenseEntry[] licenses, Func<string, string> installedVersion, ISet<string> markerLicenseIds,
            IDictionary<string, InstallRecord> installRecords = null)
        {
            var list = new List<CatalogEntry>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (purchases != null)
            {
                foreach (var item in purchases)
                {
                    if (item == null) continue;
                    var id = !string.IsNullOrEmpty(item.product_id) ? item.product_id : item.item_id;
                    var key = FavoritesLogic.EntryKey(FavoriteKind.Purchase, id);
                    if (key == null || !keys.Add(key)) continue;
                    list.Add(FromPurchase(key, item));
                }
            }
            if (licenses != null)
            {
                foreach (var license in licenses)
                {
                    if (license == null) continue;
                    var key = FavoritesLogic.EntryKey(FavoriteKind.License, license.license_id);
                    if (key == null || !keys.Add(key)) continue;
                    var entry = FromLicense(key, license, installedVersion, markerLicenseIds, installRecords);
                    if (entry != null) list.Add(entry);
                }
            }
            return list;
        }

        private static CatalogEntry FromPurchase(string key, PurchaseItem item)
        {
            var entry = new CatalogEntry
            {
                Key = key,
                Kind = FavoriteKind.Purchase,
                Purchase = item,
                Name = item.name ?? "",
                SellerName = item.seller_name ?? "",
                ThumbUrl = item.thumbnail_url ?? "",
                Description = item.description ?? "",
                ProductUrl = item.product_url ?? "",
                IsOwn = item.IsOwn,
            };
            DateTime when;
            entry.SortDate = TryParseDate(!string.IsNullOrEmpty(item.purchased_at) ? item.purchased_at : item.date, out when) ? when : DateTime.MinValue;
            entry.BoughtDate = FormatDate(!string.IsNullOrEmpty(item.purchased_at) ? item.purchased_at : item.date);
            if (item.versions != null)
            {
                for (var i = 0; i < item.versions.Length; i++)
                {
                    var v = item.versions[i];
                    if (v == null) continue;
                    entry.Versions.Add(new VersionRow
                    {
                        Label = v.version ?? "",
                        Date = FormatDate(v.released_at),
                        Notes = v.notes ?? "",
                        Size = v.size_bytes,
                        Purchase = v,
                        Latest = entry.Versions.Count == 0,
                    });
                }
            }
            entry.LatestVersion = entry.Versions.Count > 0 ? entry.Versions[0].Label : "";
            entry.State = !item.downloaded ? EntryState.None : item.update_available ? EntryState.New : EntryState.Got;
            return entry;
        }

        private static CatalogEntry FromLicense(string key, LicenseEntry license, Func<string, string> installedVersion, ISet<string> markerLicenseIds,
            IDictionary<string, InstallRecord> installRecords)
        {
            var versions = new List<LicenseVersionEntry>();
            var files = new List<LicenseVersionEntry>();
            if (license.versions != null)
            {
                foreach (var v in license.versions)
                {
                    if (v == null) continue;
                    if (v.IsPackage) versions.Add(v);
                    else if (!string.IsNullOrEmpty(v.version_id)) files.Add(v);
                }
            }
            // Only a product with no package version is installed from its files (.unitypackage); otherwise the files are installers.
            if (versions.Count > 0) files.Clear();
            var marker = markerLicenseIds != null && markerLicenseIds.Contains(license.license_id ?? "");
            // A license with no installable package version is listed only when it still matters here: its product is installed
            // another way (contract v8 marker) or it has a device limit to manage.
            if (versions.Count == 0 && files.Count == 0 && !marker && license.max_devices <= 0) return null;
            versions.Sort((a, b) => PackageVersion.Compare(b.package_version, a.package_version));
            var newest = versions.Count > 0 ? versions[0] : null;
            var installed = newest != null && installedVersion != null ? (installedVersion(newest.package_name) ?? "") : "";
            // A preview or beta is offered only to a project that already runs a prerelease, or when there is nothing else.
            var packageKinds = versions.ConvertAll(v => BuildKinds.OfPackage(v.package_version, v.version_label));
            var offeredIndex = BuildKinds.IndexOfOffered(packageKinds, PackageVersion.IsPrerelease(installed));
            var offered = offeredIndex >= 0 ? versions[offeredIndex] : null;
            var entry = new CatalogEntry
            {
                Key = key,
                Kind = FavoriteKind.License,
                License = license,
                Name = license.name ?? "",
                SellerName = license.seller_name ?? "",
                ThumbUrl = license.thumbnail_url ?? "",
                Description = license.description ?? "",
                ProductUrl = license.product_url ?? "",
                IsOwn = license.IsOwn,
                MaxDevices = license.max_devices,
                DevicesUsed = license.devices_used,
                PackageName = offered != null ? offered.package_name : "",
                LatestVersion = offered != null ? offered.package_version : "",
            };
            entry.InstalledVersion = installed;
            DateTime latest = DateTime.MinValue;
            for (var i = 0; i < versions.Count; i++)
            {
                var v = versions[i];
                DateTime when;
                if (TryParseDate(v.released_at, out when) && when > latest) latest = when;
                entry.Versions.Add(new VersionRow
                {
                    Label = v.package_version,
                    Date = FormatDate(v.released_at),
                    Notes = v.release_notes ?? "",
                    Size = v.file_size,
                    License = v,
                    Installed = installed.Length > 0 && v.package_name == newest.package_name && v.package_version == installed,
                    Latest = i == offeredIndex,
                    Kind = packageKinds[i],
                });
            }
            files.Sort(NewestFirst);
            // A file has no version to parse: its label says whether it is a preview or beta, and the newest other file is offered.
            var fileLabels = files.ConvertAll(v => string.IsNullOrEmpty(v.version_label) ? (v.file_name ?? "") : v.version_label);
            var fileKinds = fileLabels.ConvertAll(l => BuildKinds.OfLabel(l));
            // Only an installed (marked) product has a record that counts; with no marker the files are not in this project.
            InstallRecord record = null;
            if (versions.Count == 0 && marker && installRecords != null) installRecords.TryGetValue(license.license_id ?? "", out record);
            // Like packages: a project that runs a preview or beta file is offered the newest of all, so it is never steered back.
            var offeredFile = BuildKinds.IndexOfOffered(fileKinds, record != null && BuildKinds.IsPrerelease(BuildKinds.OfLabel(record.Label ?? "")));
            for (var i = 0; i < files.Count; i++)
            {
                var v = files[i];
                DateTime when;
                if (TryParseDate(v.released_at, out when) && when > latest) latest = when;
                entry.Versions.Add(new VersionRow
                {
                    Label = fileLabels[i],
                    Date = FormatDate(v.released_at),
                    Notes = v.release_notes ?? "",
                    Size = v.file_size,
                    License = v,
                    Latest = i == offeredFile,
                    Kind = fileKinds[i],
                });
            }
            if (versions.Count == 0 && offeredFile >= 0) entry.LatestVersion = fileLabels[offeredFile];
            entry.SortDate = latest;
            entry.State = StateOfPackage(installed, entry.LatestVersion);
            if (versions.Count == 0 && marker && offeredFile >= 0) entry.State = StateOfFiles(entry, files, fileLabels, offeredFile, record);
            else if (entry.State == EntryState.None && marker) entry.State = EntryState.Latest;   // installed another way, nothing here to compare with
            return entry;
        }

        /// <summary>
        /// File versions newest first by their parsed release time, so the order (which picks the offered file and, with a record,
        /// the state) does not depend on how the time is written (fractions, offset). Unreadable times go last (the server column
        /// is NOT NULL); two unreadable ones keep the old text order.
        /// </summary>
        private static int NewestFirst(LicenseVersionEntry a, LicenseVersionEntry b)
        {
            DateTime x, y;
            var hasX = TryParseDate(a.released_at, out x);
            var hasY = TryParseDate(b.released_at, out y);
            if (hasX && hasY) return y.CompareTo(x);
            if (hasX != hasY) return hasX ? -1 : 1;
            return string.CompareOrdinal(b.released_at ?? "", a.released_at ?? "");
        }

        /// <summary>
        /// A marked (installed) non-package license against the Manager's install record: no record is Unknown; the offered file is
        /// Latest when it is the recorded version, New when it was released after it, Installed when the record is newer. A listed
        /// record is placed by its position in the list (newest first, the order the rows show); one no longer listed by its release
        /// time, and Unknown when either time cannot be read (no guess that could offer an older version as an update). The
        /// recorded row is marked installed. The file rows follow <paramref name="files"/> after the package rows (none here).
        /// </summary>
        private static EntryState StateOfFiles(CatalogEntry entry, List<LicenseVersionEntry> files, List<string> fileLabels, int offeredFile, InstallRecord record)
        {
            if (record == null) return EntryState.Unknown;
            entry.InstalledVersion = record.Label ?? "";
            var recordIndex = -1;
            for (var i = 0; i < files.Count; i++)
            {
                if (!string.Equals(files[i].version_id, record.VersionId, StringComparison.OrdinalIgnoreCase)) continue;
                entry.Versions[i].Installed = true;
                entry.InstalledVersion = fileLabels[i];
                recordIndex = i;
            }
            if (recordIndex >= 0)
                return recordIndex == offeredFile ? EntryState.Latest : recordIndex > offeredFile ? EntryState.New : EntryState.Installed;
            var offered = files[offeredFile];
            DateTime offeredAt, recordedAt;
            if (!TryParseDate(offered.released_at, out offeredAt) || !TryParseDate(record.ReleasedAt, out recordedAt)) return EntryState.Unknown;
            return offeredAt > recordedAt ? EntryState.New : EntryState.Installed;
        }

        /// <summary>Not installed: None. Older than the newest: New. The newest: Latest (no chip). Newer than any version offered: Installed.</summary>
        public static EntryState StateOfPackage(string installed, string latest)
        {
            if (string.IsNullOrEmpty(installed)) return EntryState.None;
            var c = PackageVersion.Compare(latest ?? "", installed);
            if (c > 0) return EntryState.New;
            return c == 0 ? EntryState.Latest : EntryState.Installed;
        }

        // ------------------------------------------------------------ dates

        public static bool TryParseDate(string text, out DateTime value)
        {
            return DateTime.TryParse(text ?? "", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out value);
        }

        /// <summary>The server's UTC time as a date in this computer's time zone (a release at 23:30 UTC is the next day in Korea).</summary>
        public static string FormatDate(string text)
        {
            return FormatDate(text, TimeZoneInfo.Local);
        }

        public static string FormatDate(string text, TimeZoneInfo zone)
        {
            if (string.IsNullOrEmpty(text)) return "";
            DateTime when;
            if (TryParseDate(text, out when)) return TimeZoneInfo.ConvertTimeFromUtc(when, zone ?? TimeZoneInfo.Local).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return text.Length > 10 ? text.Substring(0, 10) : text;
        }

        // ------------------------------------------------------------ list

        /// <summary>Distinct seller names, sorted; the seller dropdown shows "all" first and these after it.</summary>
        public static List<string> Sellers(IList<CatalogEntry> entries)
        {
            var set = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            var list = new List<string>();
            foreach (var e in entries)
                if (!string.IsNullOrEmpty(e.SellerName) && set.Add(e.SellerName)) list.Add(e.SellerName);
            list.Sort(StringComparer.CurrentCultureIgnoreCase);
            return list;
        }

        public static bool Matches(CatalogEntry e, string search, FilterMode filter, string seller, bool favorite)
        {
            if (!string.IsNullOrEmpty(seller) && !string.Equals(e.SellerName, seller, StringComparison.CurrentCultureIgnoreCase)) return false;
            switch (filter)
            {
                case FilterMode.New: if (e.State != EntryState.New) return false; break;
                case FilterMode.None: if (e.State != EntryState.None) return false; break;
                case FilterMode.Installed: if (e.State == EntryState.None) return false; break;
                case FilterMode.Favorites: if (!favorite) return false; break;
                case FilterMode.Own: if (!e.IsOwn) return false; break;
            }
            var text = (search ?? "").Trim();
            if (text.Length == 0) return true;
            return (e.Name ?? "").IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0
                || (e.SellerName ?? "").IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private static int Todo(CatalogEntry e)
        {
            switch (e.State)
            {
                case EntryState.New: return 0;
                case EntryState.None:
                case EntryState.Unknown: return 1;
                default: return 2;
            }
        }

        public static void Sort(List<CatalogEntry> entries, SortMode mode)
        {
            Comparison<CatalogEntry> recent = (a, b) => b.SortDate.CompareTo(a.SortDate);
            Comparison<CatalogEntry> name = (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            Comparison<CatalogEntry> byDate = recent;
            switch (mode)
            {
                case SortMode.Name:
                    entries.Sort(name);
                    break;
                case SortMode.Seller:
                    entries.Sort((a, b) =>
                    {
                        var c = string.Compare(a.SellerName, b.SellerName, StringComparison.CurrentCultureIgnoreCase);
                        return c != 0 ? c : name(a, b);
                    });
                    break;
                case SortMode.Todo:
                    entries.Sort((a, b) =>
                    {
                        var c = Todo(a).CompareTo(Todo(b));
                        return c != 0 ? c : byDate(a, b);
                    });
                    break;
                default:
                    entries.Sort(byDate);
                    break;
            }
        }

        /// <summary>
        /// The lines of the list: a pinned favorites group first in every sort, then "all products" (or one group per seller
        /// when sorted by seller). Entries of a collapsed group are left out. <paramref name="entries"/> must be sorted already.
        /// </summary>
        public static List<ListLine> Group(IList<CatalogEntry> entries, SortMode sort, Func<CatalogEntry, bool> isFavorite, ISet<string> collapsed,
            string favoriteTitle, string allTitle, string otherSellerTitle)
        {
            var lines = new List<ListLine>();
            var favorites = new List<CatalogEntry>();
            var rest = new List<CatalogEntry>();
            foreach (var e in entries) (isFavorite(e) ? favorites : rest).Add(e);
            AddGroup(lines, FavoriteGroupId, favoriteTitle, favorites, collapsed, true);
            if (sort == SortMode.Seller)
            {
                string current = null;
                var bucket = new List<CatalogEntry>();
                foreach (var e in rest)
                {
                    var seller = e.SellerName ?? "";
                    if (current != null && !string.Equals(current, seller, StringComparison.CurrentCultureIgnoreCase))
                    {
                        AddGroup(lines, "s:" + current, current.Length == 0 ? otherSellerTitle : current, bucket, collapsed, false);
                        bucket = new List<CatalogEntry>();
                    }
                    current = seller;
                    bucket.Add(e);
                }
                if (current != null) AddGroup(lines, "s:" + current, current.Length == 0 ? otherSellerTitle : current, bucket, collapsed, false);
            }
            else
            {
                AddGroup(lines, AllGroupId, allTitle, rest, collapsed, false);
            }
            return lines;
        }

        private static void AddGroup(List<ListLine> lines, string id, string title, List<CatalogEntry> entries, ISet<string> collapsed, bool favorite)
        {
            if (entries.Count == 0) return;
            var closed = collapsed != null && collapsed.Contains(id);
            lines.Add(new ListLine { Header = true, GroupId = id, Title = title, Count = entries.Count, FavoriteGroup = favorite, Collapsed = closed });
            if (closed) return;
            foreach (var e in entries) lines.Add(new ListLine { GroupId = id, Entry = e });
        }

        /// <summary>First visible letter of the name (a surrogate pair counts as one), for the fallback tile.</summary>
        public static string FirstLetter(string name)
        {
            var text = (name ?? "").Trim();
            if (text.Length == 0) return "?";
            if (char.IsHighSurrogate(text[0]) && text.Length > 1) return text.Substring(0, 2);
            return text.Substring(0, 1).ToUpperInvariant();
        }
    }
}
