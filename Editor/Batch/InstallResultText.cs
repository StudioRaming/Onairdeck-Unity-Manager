using System;
using System.Collections.Generic;
using System.Text;

namespace OnAirDeck.UnityManager
{
    /// <summary>Why an install did not finish, in the words the result list shows.</summary>
    internal enum InstallFailureKind
    {
        /// <summary>An older copy (VCC, BOOTH, an old Assets folder) is in the way and was not moved.</summary>
        OldInstall,
        /// <summary>A package it needs (MingToon Core for an add-on) is not available to this account.</summary>
        Dependency,
        /// <summary>The account may not download it, or this PC holds no seat.</summary>
        License,
        /// <summary>No answer from the server or the download broke off.</summary>
        Network,
        /// <summary>The user said no or pressed cancel.</summary>
        Cancelled,
        /// <summary>The sign-in ended.</summary>
        Session,
        /// <summary>Anything else; the original message is shown.</summary>
        Other
    }

    /// <summary>
    /// One finished batch item for the summary: the name the list shows, whether it finished, and why not. Headless (no UnityEngine), so
    /// the summary wording and the failure sorting are checked by Tools~/Verification. Language: 0 Korean, 1 English, 2 Japanese
    /// (<see cref="LegacyInstallText.Language"/>).
    /// </summary>
    internal sealed class InstallResultLine
    {
        public string Name = "";
        public BatchStatus Status;
        public InstallFailureKind Kind;
        /// <summary>The plain reason ("" when it finished).</summary>
        public string Reason = "";
        /// <summary>What to do next ("" when it finished).</summary>
        public string Next = "";
    }

    internal static class InstallResultText
    {
        /// <summary>Sorts a failure. The exception types decide; a message is never parsed except the download's HTTP status.</summary>
        public static InstallFailureKind Classify(Exception e)
        {
            if (e == null) return InstallFailureKind.Other;
            if (e is LegacyDeclinedException || e is OperationCanceledException) return InstallFailureKind.Cancelled;
            if (e is SessionExpiredException) return InstallFailureKind.Session;
            if (e is LegacyCleanupRequiredException) return InstallFailureKind.OldInstall;
            if (e is DependencyUnavailableException || e is AddonNeedsCoreException) return InstallFailureKind.Dependency;
            if (e is DeviceLimitException || e is DeviceReleasedException) return InstallFailureKind.License;
            if (e is DownloadFailedException) return ((DownloadFailedException)e).ResponseCode == 0 ? InstallFailureKind.Network : InstallFailureKind.Other;
            var api = e as ApiException;
            if (api != null)
            {
                if (api.Status == 0) return InstallFailureKind.Network;
                if (api.Status == 403 || api.Status == 404) return InstallFailureKind.License;
                return InstallFailureKind.Other;
            }
            // A restore after a failed install wraps the real failure: sort by what failed.
            if (e is PackageInstallException && e.InnerException != null)
            {
                var inner = Classify(e.InnerException);
                if (inner != InstallFailureKind.Other && inner != InstallFailureKind.Cancelled) return inner;
            }
            return InstallFailureKind.Other;
        }

        /// <summary>The reason line. Texts written for the user (old install, dependency) are kept; network and license get a plain sentence.</summary>
        public static string Reason(int language, Exception e)
        {
            var kind = Classify(e);
            var needsCore = e as AddonNeedsCoreException;
            if (needsCore != null)
            {
                var core = CoreAddons.CoreName(needsCore.Core);
                return Pick(language, core + "가 이 프로젝트에 없어서 설치하지 않았어요.", core + " is not in this project, so nothing was installed.", "このプロジェクトに" + core + "がないため、インストールしませんでした。");
            }
            switch (kind)
            {
                case InstallFailureKind.Network:
                    return Pick(language, "서버에 연결하지 못했거나 다운로드가 끊겼어요.", "Could not reach the server, or the download broke off.", "サーバーに接続できないか、ダウンロードが途中で切れました。");
                case InstallFailureKind.License:
                    if (e is DeviceLimitException || e is DeviceReleasedException)
                        return Pick(language, "이 PC는 라이선스 기기 수를 넘었어요.", "This PC is over the license's device limit.", "このPCはライセンスの台数上限を超えています。");
                    return Pick(language, "이 계정으로 파일을 받을 수 없어요 (라이선스 파일을 못 받았어요).", "This account cannot download the file (no license file was given).", "このアカウントではファイルを受け取れません（ライセンスファイルを受け取れませんでした）。");
                case InstallFailureKind.Cancelled:
                    return Pick(language, "취소했어요. 아무것도 바뀌지 않았어요.", "Cancelled. Nothing was changed.", "キャンセルしました。何も変更していません。");
                case InstallFailureKind.Session:
                    return Pick(language, "로그인이 끝났어요.", "The sign-in ended.", "ログインが切れました。");
                default:
                    return Clean(e == null ? "" : e.Message);
            }
        }

        public static string Next(int language, InstallFailureKind kind)
        {
            switch (kind)
            {
                case InstallFailureKind.OldInstall:
                    return Pick(language, "위 안내대로 정리한 뒤 [실패한 항목 다시 시도]를 눌러 주세요.", "Clear it as described, then press [Retry failed].", "案内どおり整理してから［失敗した項目を再試行］を押してください。");
                case InstallFailureKind.Dependency:
                    return Pick(language, "MingToon Core를 먼저 받을 수 있는지 아틀리에에서 확인해 주세요.", "Check on the Atelier that you can get MingToon Core first.", "先にMingToon Coreを受け取れるかアトリエで確認してください。");
                case InstallFailureKind.License:
                    return Pick(language, "마이페이지 > 내 라이선스에서 라이선스와 기기를 확인해 주세요.", "Check the license and devices in My Page > My licenses.", "マイページ > マイライセンスでライセンスと端末を確認してください。");
                case InstallFailureKind.Network:
                    return Pick(language, "인터넷 연결을 확인하고 [실패한 항목 다시 시도]를 눌러 주세요.", "Check the connection and press [Retry failed].", "接続を確認して［失敗した項目を再試行］を押してください。");
                case InstallFailureKind.Session:
                    return Pick(language, "다시 로그인한 뒤 [실패한 항목 다시 시도]를 눌러 주세요.", "Sign in again, then press [Retry failed].", "再ログインしてから［失敗した項目を再試行］を押してください。");
                case InstallFailureKind.Cancelled:
                    return "";
                default:
                    return Pick(language, "[실패한 항목 다시 시도]를 눌러 보고, 그래도 안 되면 [오류 보내기]로 알려 주세요.", "Press [Retry failed]; if it fails again, send it with [Report].", "［失敗した項目を再試行］を押し、それでも失敗する場合は［エラーを送る］で知らせてください。");
            }
        }

        /// <summary>One line per item, from a finished queue. <paramref name="names"/> gives the list name of each item (same order); "" keeps the item name.</summary>
        public static List<InstallResultLine> Lines(int language, IList<BatchResult> results, IList<string> names)
        {
            var lines = new List<InstallResultLine>();
            for (var i = 0; i < results.Count; i++)
            {
                var result = results[i];
                var name = names != null && i < names.Count && !string.IsNullOrEmpty(names[i]) ? names[i] : result.Name;
                var line = new InstallResultLine { Name = name, Status = result.Status };
                if (result.Status == BatchStatus.Failed)
                {
                    line.Kind = Classify(result.Error);
                    line.Reason = result.Error != null ? Reason(language, result.Error) : Clean(result.Message);
                    // The old-install texts start with the product name; the line already says it.
                    if (line.Reason.StartsWith(name + ": ", StringComparison.Ordinal)) line.Reason = line.Reason.Substring(name.Length + 2);
                    line.Next = Next(language, line.Kind);
                }
                else if (result.Status == BatchStatus.Skipped || result.Status == BatchStatus.Pending || result.Status == BatchStatus.Paused)
                {
                    line.Kind = InstallFailureKind.Cancelled;
                    line.Reason = Pick(language, "시작하지 않았어요 (앞에서 멈췄어요).", "Not started (the run stopped before it).", "開始していません（前の項目で止まりました）。");
                }
                lines.Add(line);
            }
            return lines;
        }

        /// <summary>
        /// The banner text: the count line, then each item with what happened and, for a failure, why and what to do next.
        /// "0 finished, 2 failed" alone told the user nothing (owner report 2026-11-01).
        /// </summary>
        public static string Summary(int language, IList<InstallResultLine> lines)
        {
            var done = 0;
            var failed = 0;
            foreach (var line in lines)
            {
                if (line.Status == BatchStatus.Done) done++;
                else failed++;
            }
            var sb = new StringBuilder();
            if (failed == 0) sb.Append(Pick(language, done + "개를 모두 마쳤어요.", "All " + done + " finished.", done + "件すべて完了しました。"));
            else sb.Append(Pick(language, done + "개를 마쳤고 " + failed + "개는 못 했어요.", done + " finished, " + failed + " not.", done + "件完了、" + failed + "件はできませんでした。"));
            foreach (var line in lines)
            {
                sb.Append('\n');
                if (line.Status == BatchStatus.Done)
                {
                    sb.Append("- ").Append(line.Name).Append(Pick(language, ": 설치했어요", ": installed", ": インストールしました"));
                    continue;
                }
                sb.Append("- ").Append(line.Name).Append(Pick(language, ": 못 했어요. ", ": not installed. ", ": できませんでした。")).Append(line.Reason);
                if (line.Next.Length > 0) sb.Append("\n   ").Append(Pick(language, "다음: ", "Next: ", "次に: ")).Append(line.Next);
            }
            return sb.ToString();
        }

        private static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var t = text.Trim();
            return t.Length > 600 ? t.Substring(0, 600) + "…" : t;
        }

        private static string Pick(int language, string ko, string en, string ja)
        {
            return language == 0 ? ko : language == 2 ? ja : en;
        }
    }
}
