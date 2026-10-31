namespace OnAirDeck.UnityManager
{
    /// <summary>The project check (U3): the bar, the list of conflicts, their fixes and the undo list. Placeholders in braces.</summary>
    internal static partial class ManagerText
    {
        /// <summary>{n} conflicts found.</summary>
        public static string CfBar { get { return Pick("이 프로젝트에서 충돌 {n}건을 찾았어요.", "Found {n} conflict(s) in this project.", "このプロジェクトで競合が{n}件見つかりました。"); } }
        /// <summary>{n} fixes that can still be undone.</summary>
        public static string CfUndoBar { get { return Pick("정리한 항목 {n}건을 되돌릴 수 있어요.", "{n} clean-up(s) can still be undone.", "整理した{n}件は元に戻せます。"); } }
        public static string CfView { get { return Pick("보기", "View", "表示"); } }
        public static string CfTitle { get { return Pick("프로젝트 점검", "Project check", "プロジェクトチェック"); } }
        public static string CfScanning { get { return Pick("프로젝트를 살펴보는 중…", "Checking the project…", "プロジェクトを確認中…"); } }
        public static string CfNone { get { return Pick("충돌을 찾지 못했어요.", "No conflicts found.", "競合は見つかりませんでした。"); } }
        public static string CfRescan { get { return Pick("다시 점검", "Check again", "再チェック"); } }
        public static string CfFailed { get { return Pick("점검하지 못했어요: {m}", "The check failed: {m}", "チェックできませんでした: {m}"); } }

        /// <summary>{n} GUIDs.</summary>
        public static string CfGuidTitle { get { return Pick("같은 GUID를 쓰는 파일 ({n}개)", "Files sharing a GUID ({n})", "同じGUIDのファイル ({n}件)"); } }
        public static string CfGuidBody { get { return Pick("Unity는 그중 하나만 쓰고, 다른 쪽을 가리키던 참조(머티리얼, 프리팹)는 끊어져요. 패키지 안의 것이 진짜이고 Assets 쪽은 예전에 복사된 것일 때가 많아요.", "Unity uses only one of them; references to the other (materials, prefabs) break. Usually the one inside the package is the real one and the Assets copy is left over from an older install.", "Unityはどちらか一方しか使わず、もう一方への参照(マテリアル、プレハブ)は切れます。多くの場合、パッケージ内が本物で、Assets側は以前の導入で残ったコピーです。"); } }
        /// <summary>{p} package.</summary>
        public static string CfOldTitle { get { return Pick("{p}의 예전 복사본이 Assets에 남아 있어요", "An old copy of {p} is still under Assets", "{p}の古いコピーがAssetsに残っています"); } }
        public static string CfOldBody { get { return Pick("패키지로 설치된 버전과 예전에 Assets에 들어간 복사본이 함께 있으면 스크립트와 셰이더가 겹쳐요.", "The installed package and an older copy under Assets overlap: their scripts and shaders clash.", "パッケージ版とAssetsの古いコピーが重なり、スクリプトやシェーダーが衝突します。"); } }
        /// <summary>{a} assembly.</summary>
        public static string CfAsmTitle { get { return Pick("어셈블리 이름 {a}이(가) 두 곳 이상에 있어요", "The assembly name {a} is used more than once", "アセンブリ名 {a} が複数あります"); } }
        public static string CfAsmBody { get { return Pick("같은 이름의 어셈블리가 둘이면 Unity가 스크립트를 컴파일하지 못해요.", "Two assemblies with one name stop Unity from compiling scripts.", "同じ名前のアセンブリが2つあると、Unityはスクリプトをコンパイルできません。"); } }
        /// <summary>{p} package.</summary>
        public static string CfManTitle { get { return Pick("{p}이(가) Packages 폴더와 manifest.json 둘 다에 있어요", "{p} is both in the Packages folder and in manifest.json", "{p}がPackagesフォルダとmanifest.jsonの両方にあります"); } }
        public static string CfManBody { get { return Pick("Unity는 Packages 폴더에 있는 것을 쓰고 manifest.json 항목({v})은 무시해요. 항목을 빼면 실제로 쓰는 것과 목록이 같아져요.", "Unity uses the folder under Packages and ignores the manifest.json entry ({v}). Taking the entry out makes the list match what is used.", "UnityはPackagesフォルダのものを使い、manifest.jsonの項目({v})は無視します。項目を外すと、実際に使うものと一覧が一致します。"); } }
        /// <summary>{n} more paths.</summary>
        public static string CfMore { get { return Pick("외 {n}개", "and {n} more", "ほか{n}件"); } }

        public static string CfMove { get { return Pick("백업으로 옮기기", "Move to backup", "バックアップへ移動"); } }
        public static string CfReinstall { get { return Pick("다시 설치", "Reinstall", "再インストール"); } }
        public static string CfNoLicense { get { return Pick("이 계정에 이 패키지의 라이선스가 없어 다시 설치할 수 없어요.", "This account has no license for this package, so it cannot be reinstalled here.", "このアカウントにはこのパッケージのライセンスがないため、再インストールできません。"); } }
        public static string CfRemoveEntry { get { return Pick("manifest.json에서 빼기", "Remove from manifest.json", "manifest.jsonから外す"); } }
        public static string CfShow { get { return Pick("Project 창에서 보기", "Show in Project", "Projectウィンドウで表示"); } }

        /// <summary>{list} paths.</summary>
        public static string CfMoveAsk { get { return Pick("아래 항목을 Library/OnAirDeck/Backup으로 옮겨요. 지우지 않고, 이 화면에서 되돌릴 수 있어요.\n\n{list}", "These items move to Library/OnAirDeck/Backup. Nothing is deleted, and you can undo it on this screen.\n\n{list}", "以下をLibrary/OnAirDeck/Backupへ移動します。削除はせず、この画面で元に戻せます。\n\n{list}"); } }
        /// <summary>{p} package, {v} entry value.</summary>
        public static string CfRemoveAsk { get { return Pick("manifest.json에서 \"{p}\": \"{v}\" 항목을 빼요. 파일은 먼저 백업하고, 이 화면에서 되돌릴 수 있어요.", "The entry \"{p}\": \"{v}\" leaves manifest.json. The file is backed up first, and you can undo it on this screen.", "manifest.jsonから \"{p}\": \"{v}\" を外します。先にファイルをバックアップし、この画面で元に戻せます。"); } }
        public static string CfMoved { get { return Pick("백업으로 옮겼어요. 프로젝트 점검 화면에서 되돌릴 수 있어요.", "Moved to the backup. You can undo it in the project check.", "バックアップへ移動しました。プロジェクトチェックで元に戻せます。"); } }
        public static string CfRemoved { get { return Pick("manifest.json에서 뺐어요. 프로젝트 점검 화면에서 되돌릴 수 있어요.", "Removed from manifest.json. You can undo it in the project check.", "manifest.jsonから外しました。プロジェクトチェックで元に戻せます。"); } }

        public static string CfUndoTitle { get { return Pick("되돌릴 수 있는 정리", "Clean-ups you can undo", "元に戻せる整理"); } }
        /// <summary>{when} time, {list} paths.</summary>
        public static string CfUndoMove { get { return Pick("{when}에 백업으로 옮긴 항목: {list}", "Moved to the backup at {when}: {list}", "{when}にバックアップへ移動: {list}"); } }
        /// <summary>{when} time, {p} package.</summary>
        public static string CfUndoEntry { get { return Pick("{when}에 manifest.json에서 뺀 항목: {p}", "Removed from manifest.json at {when}: {p}", "{when}にmanifest.jsonから外した項目: {p}"); } }
        public static string CfUndo { get { return Pick("되돌리기", "Undo", "元に戻す"); } }
        public static string CfKeep { get { return Pick("이대로 두기", "Keep it", "このままにする"); } }
        public static string CfRestored { get { return Pick("되돌렸어요.", "Undone.", "元に戻しました。"); } }
    }
}
