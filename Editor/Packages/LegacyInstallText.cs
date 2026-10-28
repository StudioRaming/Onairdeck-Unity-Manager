using System;
using System.Collections.Generic;
using System.Text;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Texts of contract v9 (moving an old Assets install out of the way before a package install). Language index:
    /// 0 ko, 1 en, 2 ja (the order of SRLang). Pure, so the service and the window share one table.
    /// <see cref="LanguageProvider"/> is set by LegacyInstallDialog (reads the shared StudioRaming.Language preference);
    /// headless it stays English.
    /// </summary>
    internal static class LegacyInstallText
    {
        public const int ShownPaths = 10;

        /// <summary>Test seam and Unity hook: the language index used when no language is passed.</summary>
        public static Func<int> LanguageProvider = () => 1;

        public static int Language()
        {
            var provider = LanguageProvider;
            var value = provider == null ? 1 : provider();
            return value < 0 || value > 2 ? 1 : value;
        }

        public static string Title(int language)
        {
            switch (language)
            {
                case 0: return "이전 설치를 옮기고 설치할까요?";
                case 2: return "以前のインストールを移動してインストールしますか？";
                default: return "Move the old install and continue?";
            }
        }

        public static string Yes(int language)
        {
            switch (language) { case 0: return "옮기고 설치"; case 2: return "移動してインストール"; default: return "Move and install"; }
        }

        public static string No(int language)
        {
            switch (language) { case 0: return "설치 안 함"; case 2: return "インストールしない"; default: return "Do not install"; }
        }

        /// <summary>The dialog body: the sentence, then up to ShownPaths paths with file counts, then "and N more".</summary>
        public static string Body(int language, LegacyPlan plan)
        {
            var sb = new StringBuilder();
            var product = plan.ProductNames();
            var whole = false;
            var guid = false;
            foreach (var item in plan.Items) { if (item.Source == "guid") guid = true; else whole = true; }
            if (!whole && !guid) whole = true;
            switch (language)
            {
                case 0:
                    sb.Append(product).Append("의 이전 버전이 Assets 폴더에 있어요. 패키지로 설치하면 파일이 겹쳐서, 아래 항목을 프로젝트 밖(Library/OnAirDeck/Backup)으로 옮긴 뒤 설치해요. 지워지지 않고 백업에 남아요.");
                    if (whole) sb.Append("\n폴더는 통째로 옮겨요. 직접 넣은 파일도 같이 옮겨져요.");
                    if (guid) sb.Append("\n이 제품의 파일만 옮겨요. 직접 만든 파일은 그대로 있어요.");
                    break;
                case 2:
                    sb.Append(product).Append("の以前のバージョンがAssetsフォルダにあります。パッケージとしてインストールするとファイルが重複するため、下の項目をプロジェクトの外（Library/OnAirDeck/Backup）へ移動してからインストールします。削除はされず、バックアップに残ります。");
                    if (whole) sb.Append("\nフォルダはまるごと移動します。自分で入れたファイルも一緒に移動されます。");
                    if (guid) sb.Append("\nこの製品のファイルだけを移動します。自分で作ったファイルはそのまま残ります。");
                    break;
                default:
                    sb.Append("An older version of ").Append(product).Append(" is in your Assets folder. Installing it as a package would duplicate its files, so the items below are moved out of the project (Library/OnAirDeck/Backup) first. Nothing is deleted; it stays in the backup.");
                    if (whole) sb.Append("\nA folder moves as a whole, including files you added to it.");
                    if (guid) sb.Append("\nOnly this product's files move; your own files stay where they are.");
                    break;
            }
            AppendPaths(sb, language, plan.Items);
            return sb.ToString();
        }

        /// <summary>One dialog for a whole batch: every product that needs the move, then the paths.</summary>
        public static string BatchBody(int language, LegacyPlan plan)
        {
            var sb = new StringBuilder(Body(language, plan));
            switch (language)
            {
                case 0: sb.Append("\n\"설치 안 함\"을 고르면 위 제품만 이번 설치에서 빠지고, 나머지는 계속 설치해요."); break;
                case 2: sb.Append("\n「インストールしない」を選ぶと、上の製品だけ今回のインストールから外れ、残りは続けます。"); break;
                default: sb.Append("\nIf you choose \"Do not install\", only the products above are skipped; the rest continue."); break;
            }
            return sb.ToString();
        }

        private static void AppendPaths(StringBuilder sb, int language, IList<LegacyItem> items)
        {
            for (var i = 0; i < Math.Min(ShownPaths, items.Count); i++)
            {
                sb.Append("\n  ").Append(items[i].Path).Append(" (").Append(FileCount(language, items[i].Files)).Append(")");
            }
            if (items.Count > ShownPaths)
            {
                var more = items.Count - ShownPaths;
                switch (language)
                {
                    case 0: sb.Append("\n  외 ").Append(more).Append("개"); break;
                    case 2: sb.Append("\n  ほか").Append(more).Append("件"); break;
                    default: sb.Append("\n  and ").Append(more).Append(" more"); break;
                }
            }
        }

        public static string FileCount(int language, int files)
        {
            switch (language)
            {
                case 0: return "파일 " + files + "개";
                case 2: return files + "ファイル";
                default: return files + (files == 1 ? " file" : " files");
            }
        }

        /// <summary>The item state a background path shows instead of moving anything.</summary>
        public static string CleanupRequired(int language, string product)
        {
            switch (language)
            {
                case 0: return product + ": 이전 설치 정리 필요. 직접 눌러서 설치하면 이전 설치를 백업으로 옮겨요.";
                case 2: return product + ": 以前のインストールの整理が必要です。自分で押してインストールすると、以前のインストールをバックアップへ移動します。";
                default: return product + ": old install needs clean-up. Install it yourself and the old install is moved to the backup.";
            }
        }

        // ---------- hand-off to the product's own installer (OnAirDeckConfig.LegacyInstallers) ----------

        public static string HandoffTitle(int language, string product)
        {
            switch (language)
            {
                case 0: return "이전 " + product + " 설치를 정리할까요?";
                case 2: return "以前の" + product + "のインストールを整理しますか？";
                default: return "Clean up the old " + product + " install?";
            }
        }

        public static string HandoffBody(int language, string product)
        {
            switch (language)
            {
                case 0: return "이 프로젝트에 이전 " + product + "(Assets 설치본)이 있습니다. 이전 설치를 삭제하고 새 버전을 설치할까요? 출시 파일과 같은 파일만 지우며, 직접 추가하거나 고친 파일이 있으면 아무것도 지우지 않고 멈춥니다. 프리셋(Assets/MingToonPresets)과 생성 파일(MingToonGenerated)은 그대로 둡니다.";
                case 2: return "このプロジェクトに以前の" + product + "（Assets 版）があります。以前のインストールを削除して新しいバージョンをインストールしますか？ リリース済みファイルと同じファイルだけを削除し、自分で追加・編集したファイルがある場合は何も削除せずに停止します。プリセット（Assets/MingToonPresets）と生成ファイル（MingToonGenerated）はそのまま残します。";
                default: return "This project has an old " + product + " (Assets install). Delete the old install and install the new version? Only files identical to the released ones are deleted; if you added or edited a file, nothing is deleted and it stops. Presets (Assets/MingToonPresets) and generated files (MingToonGenerated) stay.";
            }
        }

        public static string HandoffYes(int language)
        {
            switch (language) { case 0: return "삭제하고 설치"; case 2: return "削除してインストール"; default: return "Delete and install"; }
        }

        public static string HandoffNo(int language)
        {
            switch (language) { case 0: return "설치 안 함"; case 2: return "インストールしない"; default: return "Do not install"; }
        }

        /// <summary>The installer was imported; it installs the package itself. This is not an install success.</summary>
        public static string HandedOff(int language, string product)
        {
            switch (language)
            {
                case 0: return product + " 설치기가 이어서 설치해요.";
                case 2: return product + " インストーラーが続けてインストールします。";
                default: return "The " + product + " installer continues the install.";
            }
        }

        /// <summary>Why nothing was imported: background run, no installer file in this version, or no way to ask.</summary>
        public static string HandoffUnavailable(int language, string product, string folder, bool hasInstaller)
        {
            var baseText = CleanupRequired(language, product);
            if (hasInstaller) return baseText + " (" + folder + ")";
            switch (language)
            {
                case 0: return product + ": 이전 설치 정리 필요 (" + folder + "). 이 버전에는 설치기 파일이 없어 아무것도 바꾸지 않았어요.";
                case 2: return product + ": 以前のインストールの整理が必要です（" + folder + "）。このバージョンにはインストーラーがないため、何も変更していません。";
                default: return product + ": old install needs clean-up (" + folder + "). This version has no installer file, nothing was changed.";
            }
        }

        public static string HandoffWaiting(int language, string package)
        {
            switch (language)
            {
                case 0: return package + " 설치기가 작업 중이에요. 끝날 때까지 다른 설치와 새로고침을 멈춰요.";
                case 2: return package + " のインストーラーが作業中です。終わるまで他のインストールと更新を止めます。";
                default: return "The " + package + " installer is working. Other installs and refreshes wait until it ends.";
            }
        }

        /// <summary>The installer does not support the Manager's clean-up request (no managerCleanup flag). Nothing was written or deleted.</summary>
        public static string HandoffUnsupported(int language, string product, string folder)
        {
            if (string.IsNullOrEmpty(folder)) folder = "-";
            switch (language)
            {
                case 0: return product + ": 이전 설치 정리 필요 (" + folder + "). 이 설치기는 Manager 정리를 지원하지 않아 아무것도 바꾸지 않았어요.";
                case 2: return product + ": 以前のインストールの整理が必要です（" + folder + "）。このインストーラーは Manager からの整理に対応していないため、何も変更していません。";
                default: return product + ": old install needs clean-up (" + folder + "). This installer does not support the Manager's clean-up, nothing was changed.";
            }
        }

        /// <summary>Between the installer's delete and the Manager's install: pink materials and missing scripts are expected, not an error.</summary>
        public static string HandoffInstalling(int language, string product)
        {
            switch (language)
            {
                case 0: return product + " 설치 중";
                case 2: return product + " をインストール中";
                default: return "Installing " + product;
            }
        }

        /// <summary>No answer after the Manager's install: the state is unknown.</summary>
        public static string HandoffNeedsCheck(int language, string product, string phase)
        {
            var shown = string.IsNullOrEmpty(phase) ? "-" : phase;
            switch (language)
            {
                case 0: return product + " 확인 필요 (단계: " + shown + "). 설치기에서 결과를 확인해 주세요.";
                case 2: return product + " の確認が必要です（段階: " + shown + "）。インストーラーで結果を確認してください。";
                default: return product + " needs a check (phase: " + shown + "). Look at the installer's result.";
            }
        }

        /// <summary>The installer's failure text by prefix: preflight changed nothing; removal and verify mean the product must be installed again.</summary>
        public static string HandoffFailure(int language, string product, string failure)
        {
            var kind = MingToonLegacyCleanup.FailureKind(failure);
            var detail = (failure ?? "").Trim();
            switch (kind)
            {
                case "preflight":
                case "rejected":
                    switch (language)
                    {
                        case 0: return product + " 이전 설치를 정리하지 못했어요. 아무것도 바뀌지 않았어요. " + detail;
                        case 2: return product + " の以前のインストールを整理できませんでした。何も変更されていません。" + detail;
                        default: return "The old " + product + " install was not cleaned up. Nothing was changed. " + detail;
                    }
                case "removal":
                case "verify":
                    switch (language)
                    {
                        case 0: return product + " 설치가 중간에 멈췄어요. " + product + "을(를) 다시 설치해야 해요. " + detail;
                        case 2: return product + " のインストールが途中で止まりました。" + product + " を再インストールしてください。" + detail;
                        default: return "The " + product + " install stopped halfway. Install " + product + " again. " + detail;
                    }
                default:
                    switch (language)
                    {
                        case 0: return product + " 설치기가 멈췄어요: " + detail;
                        case 2: return product + " インストーラーが停止しました: " + detail;
                        default: return "The " + product + " installer stopped: " + detail;
                    }
            }
        }

        /// <summary>One line for a hand-off status (what the window shows); Done and None are not worth a line.</summary>
        public static string HandoffStatusText(int language, string product, HandoffStatus status, string menuPath)
        {
            if (status == null || status.Kind == HandoffKind.None) return "";
            if (status.Kind == HandoffKind.Done) return HandedOff(language, product);
            if (status.Kind == HandoffKind.Pending)
                return status.Phase == MingToonLegacyCleanup.PhaseRemoved ? HandoffInstalling(language, product) : HandedOff(language, product);
            if (status.Reason == LegacyHandoff.ReasonNeedsCheck) return HandoffNeedsCheck(language, product, status.Phase);
            if (status.Reason == LegacyHandoff.ReasonUnsupported) return HandoffUnsupported(language, product, "") ;
            if (status.Reason == LegacyHandoff.ReasonNoProgress) return HandoffStopped(language, product, status.Reason, menuPath);
            return HandoffFailure(language, product, status.Reason);
        }

        /// <summary>The installer stopped (no progress). Nothing was changed by the Manager.</summary>
        public static string HandoffStopped(int language, string product, string reason, string menuPath)
        {
            switch (language)
            {
                case 0: return product + " 설치기가 멈췄어요: " + reason + ". 메뉴 " + menuPath + " 로 설치기를 다시 열 수 있어요.";
                case 2: return product + " インストーラーが停止しました: " + reason + "。メニュー " + menuPath + " からインストーラーを開けます。";
                default: return "The " + product + " installer stopped: " + reason + ". Open it again from the menu " + menuPath + ".";
            }
        }

        /// <summary>
        /// Why an old install is not moved: the folder is not proven to hold only the old install. Names the folder, and
        /// up to ShownPaths of the files that stopped it (user-added or changed). Nothing was changed.
        /// </summary>
        public static string BlockedBody(int language, IList<LegacyItem> blocked)
        {
            var sb = new StringBuilder();
            var names = new List<string>();
            foreach (var item in blocked) if (!names.Contains(item.Product)) names.Add(item.Product);
            var product = string.Join(", ", names.ToArray());
            switch (language)
            {
                case 0: sb.Append(product).Append(": 이전 설치 정리 필요. 폴더에 설치 목록과 다른 파일이 있거나, 확인할 목록이 없어서 옮기지 않았어요. 아무것도 바뀌지 않았어요."); break;
                case 2: sb.Append(product).Append(": 以前のインストールの整理が必要です。フォルダにインストール一覧と異なるファイルがあるか、確認する一覧がないため移動しませんでした。何も変更していません。"); break;
                default: sb.Append(product).Append(": old install needs clean-up. The folder has files that differ from the install list, or there is no list to check against, so nothing was moved or changed."); break;
            }
            foreach (var item in blocked)
            {
                sb.Append("\n  ").Append(item.Path);
                var shown = 0;
                foreach (var path in item.Unmatched)
                {
                    if (shown++ >= ShownPaths) { sb.Append("\n    ..."); break; }
                    sb.Append("\n    ").Append(path);
                }
            }
            return sb.ToString();
        }

        public static string StillColliding(int language)
        {
            switch (language)
            {
                case 0: return "옮긴 뒤에도 같은 GUID 파일이 남아 있어서 설치하지 않았어요. 옮긴 항목은 원래 자리로 되돌렸어요.";
                case 2: return "移動後も同じGUIDのファイルが残っているため、インストールしませんでした。移動した項目は元の場所へ戻しました。";
                default: return "Files with the same GUIDs were still there after the move, so nothing was installed. The moved items were put back.";
            }
        }

        public static string InstallFailedRestored(int language, string reason)
        {
            switch (language)
            {
                case 0: return "설치에 실패해서 옮겼던 이전 설치를 원래 자리로 되돌렸어요: " + reason;
                case 2: return "インストールに失敗したため、移動した以前のインストールを元の場所へ戻しました: " + reason;
                default: return "The install failed, so the old install that was moved has been put back: " + reason;
            }
        }

        public static string RestoreIncomplete(int language, string backupFolder)
        {
            switch (language)
            {
                case 0: return "일부 항목을 되돌리지 못했어요. 백업은 " + backupFolder + " 에 그대로 있어요.";
                case 2: return "一部の項目を戻せませんでした。バックアップは " + backupFolder + " にそのまま残っています。";
                default: return "Some items could not be put back. The backup is still in " + backupFolder + ".";
            }
        }

        public static string ManifestUnreadable(int language)
        {
            switch (language)
            {
                case 0: return "백업 기록을 읽을 수 없어서 되돌리지 않았어요.";
                case 2: return "バックアップの記録を読めないため、戻しませんでした。";
                default: return "The backup record could not be read, so nothing was put back.";
            }
        }

        public static string NothingToRestore(int language)
        {
            switch (language)
            {
                case 0: return "되돌릴 항목이 없어요.";
                case 2: return "戻す項目はありません。";
                default: return "There is nothing to put back.";
            }
        }

        public static string RestoreOccupied(int language, string firstPath, int count)
        {
            switch (language)
            {
                case 0: return "원래 자리에 이미 다른 파일이 있어서 되돌리지 않았어요: " + firstPath + (count > 1 ? " 외 " + (count - 1) + "개" : "");
                case 2: return "元の場所に別のファイルがあるため戻しませんでした: " + firstPath + (count > 1 ? " ほか" + (count - 1) + "件" : "");
                default: return "Something is already in the original place, so nothing was put back: " + firstPath + (count > 1 ? " and " + (count - 1) + " more" : "");
            }
        }

        public static string RestoredAll(int language)
        {
            switch (language)
            {
                case 0: return "이전 설치를 원래 자리로 되돌렸어요.";
                case 2: return "以前のインストールを元の場所へ戻しました。";
                default: return "The old install was put back.";
            }
        }

        /// <summary>Refusal for a package from another vendor (or a stub) that holds files with the incoming GUIDs.</summary>
        public static string ForeignRefusal(int language, string package, int files)
        {
            switch (language)
            {
                case 0: return package + " 이(가) 있는 프로젝트에는 설치할 수 없어요 (같은 GUID 파일 " + files + "개)";
                case 2: return package + " があるプロジェクトにはインストールできません（同じGUIDのファイル" + files + "件）";
                default: return "This package cannot be installed in a project that has " + package + " (" + files + " files share GUIDs).";
            }
        }
    }
}
