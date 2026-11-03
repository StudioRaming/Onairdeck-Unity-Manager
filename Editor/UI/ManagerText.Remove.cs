namespace OnAirDeck.UnityManager
{
    /// <summary>Removing a product (ManagerWindow.Remove). Placeholders in braces.</summary>
    internal static partial class ManagerText
    {
        public static string BRemove { get { return Pick("제거", "Remove", "削除"); } }
        public static string RmTogether { get { return Pick("함께 제거", "Remove Together", "一緒に削除"); } }
        /// <summary>{n} product.</summary>
        public static string RmAskT { get { return Pick("{n} 제거", "Remove {n}", "{n} を削除"); } }
        public static string RmAsk { get { return Pick("아래 항목을 이 프로젝트에서 지울까요? 파일은 휴지통으로 옮겨서 휴지통에서 되돌릴 수 있어요. 아틀리에 매니저가 설치한 파일만 지우고, 직접 만든 파일은 그대로 둬요.", "Remove these from this project? Files go to the trash, where you can restore them. Only files the Atelier Manager installed are removed; files you made stay.", "以下をこのプロジェクトから削除しますか? ファイルはゴミ箱に移すので、ゴミ箱から戻せます。アトリエマネージャーが入れたファイルだけを削除し、自分で作ったファイルは残します。"); } }
        /// <summary>{p} Packages/&lt;name&gt;.</summary>
        public static string RmPkgLine { get { return Pick("패키지 {p} (Library/OnAirDeck/Backup으로 옮겨 둬요)", "Package {p} (moved to Library/OnAirDeck/Backup)", "パッケージ {p} (Library/OnAirDeck/Backup に移します)"); } }
        /// <summary>{n} files another product also installed.</summary>
        public static string RmShared { get { return Pick("다른 상품도 쓰는 파일 {n}개는 남겨요", "{n} files another product also uses stay", "他の商品も使うファイル{n}件は残します"); } }
        /// <summary>{n} files that are no longer what the product installed.</summary>
        public static string RmChanged { get { return Pick("설치한 뒤 바뀐 파일 {n}개는 남겨요", "{n} files changed since the install stay", "インストール後に変わったファイル{n}件は残します"); } }
        /// <summary>{f} files, {d} folders.</summary>
        public static string RmCounts { get { return Pick("파일 {f}개, 빈 폴더 {d}개", "{f} files, {d} empty folders", "ファイル{f}件、空のフォルダー{d}件"); } }
        public static string RmListInConsole { get { return Pick("지울 파일 전체 목록은 Console에 있어요.", "The full list is in the Console.", "削除するファイルの全一覧は Console にあります。"); } }
        public static string RmAddonsUnknown { get { return Pick("{n}의 애드온 정보가 아직 준비 중이라, 애드온보다 먼저 지워지지 않도록 지금은 자동으로 지우지 않아요. Packages 폴더에서 직접 지워 주세요.", "The add-ons of {n} are not known to the Manager yet, so it does not remove {n} (it could go before an add-on). Remove it by hand in the Packages folder.", "{n} のアドオン情報がまだ準備中のため、アドオンより先に消えないよう今は自動では削除しません。Packages フォルダーで手で削除してください。"); } }
        public static string RmMore { get { return Pick("외 {n}개", "and {n} more", "ほか{n}件"); } }
        /// <summary>{c} core, {a} its installed add-ons.</summary>
        public static string RmAddonsFirst { get { return Pick("{c}를 지우려면 먼저 애드온을 지워야 해요: {a}. 애드온부터 함께 지울까요?", "{a} need {c}, so they are removed first. Remove them together with {c}?", "{c} を削除するには先にアドオンを削除する必要があります: {a}。アドオンから一緒に削除しますか?"); } }
        /// <summary>{a} add-on, {c} core.</summary>
        public static string RmAddonBlocks { get { return Pick("{a}가 {c}를 쓰고 있어서 {c}를 지우지 않았어요.", "{a} uses {c}, so {c} was not removed.", "{a} が {c} を使っているため、{c} は削除しませんでした。"); } }
        /// <summary>{n} product, {f} folder.</summary>
        public static string RmNoRecord { get { return Pick("{n}: 아틀리에 매니저가 무엇을 설치했는지 기록이 없어서 자동으로 지우지 않아요. 폴더를 열어 직접 지워 주세요: {f}", "{n}: the Atelier Manager has no record of what it installed, so it removes nothing on its own. Open the folder and remove it yourself: {f}", "{n}: アトリエマネージャーに何を入れたかの記録がないため、自動では削除しません。フォルダーを開いて手で削除してください: {f}"); } }
        public static string RmNoRecordNoFolder { get { return Pick("{n}: 아틀리에 매니저가 무엇을 설치했는지 기록이 없어서 자동으로 지우지 않아요. Project 창에서 직접 지워 주세요.", "{n}: the Atelier Manager has no record of what it installed, so it removes nothing on its own. Remove it yourself in the Project window.", "{n}: アトリエマネージャーに何を入れたかの記録がないため、自動では削除しません。Project ウィンドウで手で削除してください。"); } }
        public static string RmVcc { get { return Pick("{n}은 VCC가 관리하는 패키지예요. VCC에서 제거해 주세요.", "{n} is managed by VCC. Remove it in VCC.", "{n} は VCC が管理しているパッケージです。VCC で削除してください。"); } }
        /// <summary>{n} package, {d} the packages that need it.</summary>
        public static string RmNeeded { get { return Pick("{d}가 {n}를 쓰고 있어서 지우지 않았어요. {d}를 먼저 지워 주세요.", "{d} need {n}, so it was not removed. Remove {d} first.", "{d} が {n} を使っているため削除しませんでした。先に {d} を削除してください。"); } }
        public static string RmNothing { get { return Pick("{n}: 이 프로젝트에 지울 파일이 없어요.", "{n}: nothing of it is left in this project.", "{n}: このプロジェクトに削除するファイルはありません。"); } }
        /// <summary>{n} files moved to the trash.</summary>
        public static string RmDone { get { return Pick("지웠어요. 파일 {n}개를 휴지통으로 옮겼어요.", "Removed. {n} files went to the trash.", "削除しました。ファイル{n}件をゴミ箱に移しました。"); } }
        /// <summary>{b} backup folders.</summary>
        public static string RmBackup { get { return Pick("패키지는 여기에 보관했어요: {b}", "The package is kept at: {b}", "パッケージはここに保管しました: {b}"); } }
        /// <summary>{list} what could not be removed.</summary>
        public static string RmFailed { get { return Pick("지우지 못한 것이 있어서 거기서 멈췄어요: {list}", "Some could not be removed, so it stopped there: {list}", "削除できないものがあったため、そこで止めました: {list}"); } }
    }
}
