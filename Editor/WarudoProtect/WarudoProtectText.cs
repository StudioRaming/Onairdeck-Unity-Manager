namespace OnAirDeck.UnityManager.WarudoProtect
{
    /// <summary>ko/en/ja copy of the WARUDO protection window and build log (ManagerText.Pick; placeholders in braces).</summary>
    internal static class WarudoProtectText
    {
        private static string Pick(string ko, string en, string ja) { return ManagerText.Pick(ko, en, ja); }

        public static string WindowTitle { get { return Pick("WARUDO 보호", "WARUDO Protection", "WARUDO 保護"); } }
        public static string Intro
        {
            get
            {
                return Pick(
                    "Warudo/Build Mod 가 끝나면 같은 폴더에 .srlock 파일이 함께 만들어집니다(새 모드는 자동으로 보호). 판매 파일로는 .srlock 만 올리세요. 라이선스 입력은 필요 없습니다: 그 파일이 들어 있는 상품을 산 사람(묶음 상품 포함)이 아틀리에 매니저에 로그인하면 WARUDO 에서 열 수 있습니다.",
                    "Warudo/Build Mod also writes a .srlock file next to the .warudo (new mods are protected automatically). Upload only the .srlock as the product file. No license id is needed: buyers of any product carrying the file (bundles included) open it in WARUDO while signed in to the Atelier Manager.",
                    "Warudo/Build Mod の完了時に同じフォルダーへ .srlock ファイルも作成されます(新しいモッドは自動で保護)。販売ファイルには .srlock だけをアップロードしてください。ライセンスの入力は不要です: そのファイルを含む商品(セット商品を含む)の購入者がアトリエマネージャーにログインすると WARUDO で開けます。");
            }
        }
        public static string NoUMod { get { return Pick("이 프로젝트에서 WARUDO SDK(UMod)를 찾지 못했습니다. WARUDO 모드 프로젝트에서 여세요.", "WARUDO SDK (UMod) was not found in this project. Open this in a WARUDO mod project.", "このプロジェクトに WARUDO SDK (UMod) が見つかりません。WARUDO モッドプロジェクトで開いてください。"); } }
        public static string HookOff { get { return Pick("빌드 훅이 아직 꺼져 있습니다.", "The build hook is not active yet.", "ビルドフックはまだ無効です。"); } }
        public static string HookOn { get { return Pick("빌드 훅 켜짐: Build Mod 가 끝날 때마다 보호 파일을 만듭니다.", "Build hook active: every Build Mod writes the protected file.", "ビルドフック有効: Build Mod のたびに保護ファイルを作成します。"); } }
        public static string EnableHook { get { return Pick("빌드 훅 켜기 (스크립트 다시 컴파일)", "Turn on the build hook (recompiles scripts)", "ビルドフックを有効にする(スクリプト再コンパイル)"); } }
        public static string Guide { get { return Pick("가이드 보기", "Open the guide", "ガイドを見る"); } }
        public static string Mods { get { return Pick("모드", "Mods", "モッド"); } }
        public static string NoMods { get { return Pick("아직 등록된 모드가 없습니다. 한 번 빌드하면 자동으로 목록에 올라옵니다.", "No mods yet. A build adds its mod here automatically.", "まだモッドがありません。一度ビルドすると自動で追加されます。"); } }
        public static string Protect { get { return Pick("보호", "Protect", "保護"); } }
        public static string Kind { get { return Pick("종류", "Kind", "種類"); } }
        public static string KindAuto { get { return Pick("자동 (출력 폴더)", "Auto (output folder)", "自動(出力フォルダー)"); } }
        public static string KindProp { get { return Pick("소품", "Prop", "小物"); } }
        public static string KindEnvironment { get { return Pick("배경", "Environment", "背景"); } }
        public static string KindCharacter { get { return Pick("캐릭터", "Character", "キャラクター"); } }
        public static string Title { get { return Pick("표시 이름", "Display name", "表示名"); } }
        public static string Remove { get { return Pick("지우기", "Remove", "削除"); } }
        public static string AddMod { get { return Pick("모드 이름으로 추가", "Add by mod name", "モッド名で追加"); } }
        public static string LockExisting { get { return Pick("이미 빌드한 .warudo 잠그기…", "Lock an already built .warudo…", "ビルド済みの .warudo をロック…"); } }
        public static string PickWarudo { get { return Pick("잠글 .warudo 선택", "Choose the .warudo to lock", "ロックする .warudo を選択"); } }

        // ---- build log / dialogs ----
        public static string NewModRegistered(string mod) { return Pick("[Atelier] '{m}' 모드를 WARUDO 보호 목록에 올렸습니다(보호 켜짐). 끄려면 Window ▸ Atelier WARUDO Protection.", "[Atelier] Added '{m}' to the WARUDO protection list (protected). To turn it off: Window ▸ Atelier WARUDO Protection.", "[Atelier] '{m}' を WARUDO 保護リストに追加しました(保護オン)。オフにするには Window ▸ Atelier WARUDO Protection。").Replace("{m}", mod); }
        public static string NotReady(string mod, string why) { return Pick("[Atelier] '{m}' 보호 파일을 만들지 않았습니다: {w}", "[Atelier] No protected file for '{m}': {w}", "[Atelier] '{m}' の保護ファイルを作成しませんでした: {w}").Replace("{m}", mod).Replace("{w}", why); }
        public static string KindUnknown { get { return Pick("종류를 알 수 없습니다. 보호 창에서 소품/배경/캐릭터를 골라 주세요.", "Unknown kind. Choose prop, environment or character in the protection window.", "種類が不明です。保護ウィンドウで小物/背景/キャラクターを選んでください。"); } }
        public static string Locked(string path) { return Pick("[Atelier] 보호 파일을 만들었습니다: {p}\n판매 파일로는 이 .srlock 만 올리세요. 원본 .warudo 는 올리지 마세요.", "[Atelier] Protected file written: {p}\nUpload only this .srlock as the product file, never the plain .warudo.", "[Atelier] 保護ファイルを作成しました: {p}\n販売ファイルにはこの .srlock だけをアップロードし、元の .warudo はアップロードしないでください。").Replace("{p}", path); }
        public static string Failed(string mod, string why) { return Pick("[Atelier] '{m}' 보호 파일 만들기 실패: {w}", "[Atelier] Could not write the protected file for '{m}': {w}", "[Atelier] '{m}' の保護ファイル作成に失敗: {w}").Replace("{m}", mod).Replace("{w}", why); }
        public static string LockedShort(string file) { return Pick("보호 파일 생성: {f}", "Protected file written: {f}", "保護ファイル作成: {f}").Replace("{f}", file); }
        public static string LastLocked { get { return Pick("마지막 보호 파일", "Last protected file", "最後の保護ファイル"); } }
        public static string OpenFolder { get { return Pick("폴더 열기", "Open folder", "フォルダーを開く"); } }
    }
}
