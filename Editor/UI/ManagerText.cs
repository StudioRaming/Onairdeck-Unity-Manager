using OnAirDeck.UnityManager.Theme;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// ko/en/ja copy of the Atelier Manager window (mockup v3.2 string table plus the extra strings in ManagerText.Extra.cs).
    /// Same pattern as DeviceReleasedText: one string per property, language 0 ko, 1 en, 2 ja. The language is the shared
    /// StudioRaming setting (SRLanguage). Placeholders such as {v} are filled by the caller with string.Replace.
    /// </summary>
    internal static partial class ManagerText
    {
        public const string LanguageKey = "OnAirDeck.UnityManager.Language";

        /// <summary>0 ko, 1 en, 2 ja.</summary>
        public static int Language { get { return (int)SRLanguage.Get(LanguageKey); } }

        public static string Pick(string ko, string en, string ja)
        {
            return Pick(Language, ko, en, ja);
        }

        public static string Pick(int language, string ko, string en, string ja)
        {
            switch (language)
            {
                case 0: return ko;
                case 2: return ja;
                default: return en;
            }
        }

        // ---- mockup string table (generated from work/mgr-device-ui-20261027/mockup/index.html, T) ----
        public static string Tagline { get { return Pick("산 상품을 Unity로 바로 받아요.", "Get your purchases into Unity.", "買った商品をUnityに入れます。"); } }
        public static string LangName { get { return Pick("한국어", "English", "日本語"); } }
        public static string Web { get { return Pick("웹사이트", "Website", "ウェブサイト"); } }
        public static string Back { get { return Pick("돌아가기", "Back", "戻る"); } }
        public static string Cancel { get { return Pick("취소", "Cancel", "キャンセル"); } }
        public static string UpdNew { get { return Pick("새 {v} 버전이 나왔어요", "Version {v} is out", "新しいバージョン {v} が出ました"); } }
        public static string UpdBtn { get { return Pick("업데이트", "Update", "アップデート"); } }
        public static string UpdWhy { get { return Pick("받는 중이라 지금은 못 해요. 다 받으면 눌러 주세요.", "A download is running, so this waits. Press it when the download ends.", "ダウンロード中なので、今はできません。終わったら押してください。"); } }
        public static string ReqTitle { get { return Pick("이 버전은 더 이상 쓸 수 없어요", "This version can no longer be used", "このバージョンはもう使えません"); } }
        public static string ReqBody { get { return Pick("새 {v} 버전으로 업데이트하면 바로 다시 쓸 수 있어요.", "Update to version {v} and you can use it again right away.", "バージョン {v} に更新すればすぐまた使えます。"); } }
        public static string ReqBtn { get { return Pick("지금 업데이트", "Update Now", "今すぐアップデート"); } }
        public static string Welcome { get { return Pick("아틀리에에서 산 상품을 Unity로 바로 받아요.", "Get what you bought at Atelier straight into Unity.", "アトリエで買った商品を、Unityにすぐ入れられます。"); } }
        public static string S1 { get { return Pick("로그인", "Log in", "ログイン"); } }
        public static string S1d { get { return Pick("아틀리에 계정으로 로그인해요.", "Use your Atelier account.", "アトリエのアカウントでログインします。"); } }
        public static string S2 { get { return Pick("상품 고르기", "Pick a product", "商品を選ぶ"); } }
        public static string S2d { get { return Pick("내가 산 상품이 목록에 나와요.", "Your purchases show up in a list.", "買った商品が一覧に出ます。"); } }
        public static string S3 { get { return Pick("받기", "Get it", "受け取る"); } }
        public static string S3d { get { return Pick("버튼을 누르면 Unity로 받아요.", "Press the button and it comes into Unity.", "ボタンを押すと、Unityに入ります。"); } }
        public static string Login { get { return Pick("아틀리에 계정으로 로그인", "Log In with Atelier Account", "アトリエのアカウントでログイン"); } }
        public static string Signup { get { return Pick("계정이 없나요? 가입하기", "No account? Sign up", "アカウントがありませんか？ 登録する"); } }
        public static string SiTitle { get { return Pick("브라우저에서 '허용'을 눌러 주세요", "Press 'Allow' in your browser", "ブラウザで「許可」を押してください"); } }
        public static string SiHint { get { return Pick("브라우저가 안 열렸다면 아래 버튼으로 다시 열어요.", "If the browser did not open, use the button below.", "ブラウザが開かないときは、下のボタンで開き直します。"); } }
        public static string Reopen { get { return Pick("브라우저 다시 열기", "Reopen Browser", "ブラウザを開き直す"); } }
        public static string ToTitle { get { return Pick("시간이 지났어요", "Time ran out", "時間が過ぎました"); } }
        public static string ToBody { get { return Pick("로그인이 끝나지 않았어요. 다시 해 볼까요?", "The login did not finish. Want to try again?", "ログインが終わりませんでした。もう一度試しますか？"); } }
        public static string ToBtn { get { return Pick("다시 로그인", "Log In Again", "もう一度ログイン"); } }
        public static string LoggedIn { get { return Pick("로그인됨", "Logged in", "ログイン中"); } }
        public static string Logout { get { return Pick("로그아웃", "Log Out", "ログアウト"); } }
        public static string Manage { get { return Pick("기기 관리", "Manage Devices", "デバイス管理"); } }
        public static string ChipNone { get { return Pick("받기 전", "Not Downloaded", "未ダウンロード"); } }
        public static string ChipGot { get { return Pick("받음", "Downloaded", "ダウンロード済み"); } }
        public static string ChipNew { get { return Pick("새 버전", "New Version", "新バージョン"); } }
        public static string ChipInst { get { return Pick("설치됨", "Installed", "インストール済み"); } }
        public static string ChipLatest { get { return Pick("최신", "Up to Date", "最新"); } }
        public static string BGet { get { return Pick("받기", "Download", "ダウンロード"); } }
        public static string BAgain { get { return Pick("다시 받기", "Download Again", "もう一度ダウンロード"); } }
        public static string BInst { get { return Pick("설치", "Install", "インストール"); } }
        public static string BUpd { get { return Pick("업데이트", "Update", "アップデート"); } }
        public static string PickVer { get { return Pick("버전 고르기", "Choose Version", "バージョンを選ぶ"); } }
        public static string Search { get { return Pick("상품 찾기", "Search products", "商品を探す"); } }
        public static string Dling { get { return Pick("받는 중", "Downloading", "ダウンロード中"); } }
        public static string WaitOthers { get { return Pick("하나가 끝날 때까지 다른 상품은 잠시 기다려요.", "Other products wait until this one finishes.", "終わるまで、ほかの商品は少し待ちます。"); } }
        public static string WaitTip { get { return Pick("다른 작업이 끝나면 눌러요.", "Press it when the other task ends.", "ほかの作業が終わったら押せます。"); } }
        public static string DoneFolder { get { return Pick("다 받았어요. Project 창 > Assets > StudioRaming_Onairdeck > ", "Done. In the Project window, go to Assets > StudioRaming_Onairdeck > ", "ダウンロードできました。Projectウィンドウの Assets > StudioRaming_Onairdeck > "); } }
        public static string DoneFolderEnd { get { return Pick("", "", " にあります。"); } }
        public static string OpenFolder { get { return Pick("폴더 열기", "Show Folder", "フォルダを開く"); } }
        public static string DonePkg { get { return Pick("다 받았어요. Unity 가져오기 창에서 '가져오기'를 눌러 주세요.", "Done. In Unity's import window, press 'Import'.", "ダウンロードできました。Unityのインポートウィンドウで「インポート」を押してください。"); } }
        public static string EmptyT { get { return Pick("아직 산 상품이 없어요", "You have no products yet", "まだ買った商品がありません"); } }
        public static string EmptyM { get { return Pick("아틀리에에서 상품을 사면 여기에 나와요.", "Products you buy at Atelier show up here.", "アトリエで商品を買うと、ここに出ます。"); } }
        public static string EmptyB { get { return Pick("아틀리에 둘러보기", "Browse Atelier", "アトリエを見る"); } }
        public static string Contact { get { return Pick("문의하기", "Ask a Question", "お問い合わせ"); } }
        public static string PMis { get { return Pick("MingToon URP 애드온과 코어 버전이 안 맞아요. 버튼을 누르면 맞는 버전으로 설치해요.", "MingToon URP Add-on and the core do not match in version. The button installs the matching version.", "MingToon URP アドオンとコアのバージョンが合っていません。ボタンを押すと、合うバージョンをインストールします。"); } }
        public static string PMisB { get { return Pick("맞춰서 설치", "Install Matching Version", "合わせてインストール"); } }
        public static string PRen { get { return Pick("MingToon 상업 라이선스를 갱신하지 못했어요. 아틀리에에서 라이선스 상태를 확인해 주세요.", "MingToon Commercial License could not be renewed. Please check the license status at Atelier.", "MingToon 商用ライセンスを更新できませんでした。アトリエでライセンスの状態を確認してください。"); } }
        public static string PRenB { get { return Pick("아틀리에 열기", "Open Atelier", "アトリエを開く"); } }
        public static string PLoad { get { return Pick("상품 목록을 못 불러왔어요. 인터넷 연결을 확인해 주세요.", "Could not load your products. Please check your internet connection.", "商品の一覧を読み込めませんでした。インターネット接続を確認してください。"); } }
        public static string PLoadB { get { return Pick("다시 시도", "Try Again", "もう一度試す"); } }
        public static string DlTitle { get { return Pick("이 PC에서 쓰려면 다른 기기 하나를 빼 주세요", "Remove one other device to use this PC", "このPCで使うには、ほかのデバイスを1台外してください"); } }
        public static string RemoveDev { get { return Pick("이 기기 빼기", "Remove This Device", "このデバイスを外す"); } }
        public static string DlNote { get { return Pick("뺀 기기는 다음 확인 때(최대 7일) 사용이 멈춰요.", "A removed device stops working at its next check (up to 7 days).", "外したデバイスは、次の確認のとき(最大7日)に使えなくなります。"); } }
        public static string ThisTag { get { return Pick("지금 이 PC", "This PC", "このPC"); } }
        public static string DmTitle { get { return Pick("기기 관리", "Manage Devices", "デバイス管理"); } }
        public static string DmSub { get { return Pick("라이선스마다 쓸 수 있는 PC 수가 정해져 있어요.", "Each license has a limit on how many PCs can use it.", "ライセンスごとに、使えるPCの数が決まっています。"); } }
        public static string AskTitle { get { return Pick("문의하기", "Ask a Question", "お問い合わせ"); } }
        public static string CBug { get { return Pick("고장 신고", "Report a Bug", "不具合の報告"); } }
        public static string CIdea { get { return Pick("기능 제안", "Suggest a Feature", "機能の提案"); } }
        public static string CQ { get { return Pick("질문", "Question", "質問"); } }
        public static string Msg { get { return Pick("내용", "Message", "内容"); } }
        public static string ErrCode { get { return Pick("오류 코드 (있으면)", "Error Code (if any)", "エラーコード (あれば)"); } }
        public static string WhatSent { get { return Pick("함께 보내는 정보 보기", "See What Is Sent With It", "一緒に送る情報を見る"); } }
        public static string KMgr { get { return Pick("매니저 버전", "Manager version", "マネージャーのバージョン"); } }
        public static string KUnity { get { return Pick("Unity 버전", "Unity version", "Unityのバージョン"); } }
        public static string KOs { get { return Pick("운영체제", "Operating system", "OS"); } }
        public static string KAcc { get { return Pick("로그인 계정", "Logged-in account", "ログイン中のアカウント"); } }
        public static string KDev { get { return Pick("기기 이름", "Device name", "デバイス名"); } }
        public static string Send { get { return Pick("보내기", "Send", "送る"); } }
        public static string SendWhy { get { return Pick("내용을 적으면 보낼 수 있어요.", "You can send it once you write a message.", "内容を書くと送れます。"); } }
        public static string AppTitle { get { return Pick("아틀리에 매니저", "Atelier Manager", "アトリエマネージャー"); } }
        public static string AutoNew { get { return Pick("새 버전 {n}개가 있어요", "{n} new versions are available", "新しいバージョンが{n}件あります"); } }
        public static string UpdAll { get { return Pick("모두 업데이트", "Update All", "すべてアップデート"); } }
        public static string ViewNew { get { return Pick("보기", "Show", "見る"); } }
        public static string SRecent { get { return Pick("최근 산 순서", "Recently Bought", "買った順"); } }
        public static string SName { get { return Pick("이름순", "Name", "名前順"); } }
        public static string SSeller { get { return Pick("판매자별", "By Seller", "販売者別"); } }
        public static string STodo { get { return Pick("할 일 먼저", "To-Do First", "やることを先に"); } }
        public static string FAll { get { return Pick("전체", "All", "すべて"); } }
        public static string FFav { get { return Pick("즐겨찾기", "Favorites", "お気に入り"); } }
        public static string SellerAll { get { return Pick("판매자: 전체", "Seller: All", "販売者: すべて"); } }
        public static string FavGroup { get { return Pick("즐겨찾기", "Favorites", "お気に入り"); } }
        public static string AllGroup { get { return Pick("모든 상품", "All Products", "すべての商品"); } }
        public static string OwnChip { get { return Pick("내 상품(판매자)", "My Product (Seller)", "自分の商品(販売者)"); } }
        public static string RelNotes { get { return Pick("릴리스 노트 보기", "Show Release Notes", "リリースノートを見る"); } }
        public static string BatchGet { get { return Pick("한 번에 받기", "Download Selected", "まとめてダウンロード"); } }
        public static string ClearSel { get { return Pick("선택 해제", "Clear Selection", "選択を解除"); } }
        public static string ChipDling { get { return Pick("받는 중", "Downloading", "ダウンロード中"); } }
        public static string ChipWait { get { return Pick("대기 중", "Waiting", "待機中"); } }
        public static string Rem { get { return Pick("빼기", "Remove", "外す"); } }
        public static string MAcctDev { get { return Pick("기기 관리", "Manage Devices", "デバイス管理"); } }
        public static string MAcctLang { get { return Pick("언어", "Language", "言語"); } }
        public static string MAcctWeb { get { return Pick("웹사이트", "Website", "ウェブサイト"); } }
        public static string MAcctOut { get { return Pick("로그아웃", "Sign Out", "ログアウト"); } }
        public static string RefreshTip { get { return Pick("새로고침", "Refresh", "更新"); } }
        public static string SellerShort { get { return Pick("모든 판매자", "All Sellers", "すべての販売者"); } }
        public static string FOwn { get { return Pick("내 상품(판매자)", "My Products (Seller)", "自分の商品(販売者)"); } }
        public static string BackList { get { return Pick("목록", "List", "一覧"); } }
        public static string TabDesc { get { return Pick("설명", "Description", "説明"); } }
        public static string TabVer { get { return Pick("버전 기록", "Version History", "バージョン履歴"); } }
        public static string TabDev { get { return Pick("기기", "Devices", "デバイス"); } }
        public static string More { get { return Pick("더 보기", "Show More", "もっと見る"); } }
        public static string ViewAt { get { return Pick("아틀리에에서 보기", "View on Atelier", "アトリエで見る"); } }
        public static string StNone { get { return Pick("아직 안 받았어요", "Not downloaded yet", "まだダウンロードしていません"); } }
        public static string StGot { get { return Pick("{v} 받음, {d}", "{v} downloaded, {d}", "{v} ダウンロード済み、{d}"); } }
        public static string StNew { get { return Pick("설치됨 {i} · 최신 {l}", "Installed {i} · Latest {l}", "インストール済み {i} · 最新 {l}"); } }
        public static string StInst { get { return Pick("설치됨 {v}", "Installed {v}", "インストール済み {v}"); } }
        public static string StLatest { get { return Pick("설치됨 {v} · 최신", "Installed {v} · Latest", "インストール済み {v} · 最新"); } }
        public static string InfoSeller { get { return Pick("판매자", "Seller", "販売者"); } }
        public static string InfoBought { get { return Pick("산 날짜", "Bought On", "購入日"); } }
        public static string InfoMine { get { return Pick("내 상품", "My product", "自分の商品"); } }
        public static string InfoKind { get { return Pick("종류", "Type", "種類"); } }
        public static string InfoKindPkg { get { return Pick("Unity 패키지", "Unity Package", "Unity パッケージ"); } }
        public static string InfoLatest { get { return Pick("최신 버전 날짜", "Latest Version Date", "最新バージョンの日付"); } }
        public static string VerInstall { get { return Pick("이 버전 설치", "Install This Version", "このバージョンをインストール"); } }
        public static string VerGet { get { return Pick("이 버전 받기", "Get This Version", "このバージョンをダウンロード"); } }
        public static string StPkgNot { get { return Pick("아직 설치 안 했어요 · 최신 {l}", "Not installed yet · Latest {l}", "まだインストールしていません · 最新 {l}"); } }
        public static string InfoKindFile { get { return Pick("파일", "File", "ファイル"); } }
        public static string RcNew { get { return Pick("새 버전", "Update", "新版"); } }
        public static string RcNone { get { return Pick("받기 전", "To get", "未取得"); } }
        public static string RcGot { get { return Pick("받음", "Got", "取得済"); } }
        public static string RcInst { get { return Pick("설치됨", "Installed", "導入済"); } }
        public static string RcDling { get { return Pick("받는 중", "Getting", "取得中"); } }
        public static string RcWait { get { return Pick("대기 중", "Waiting", "待機中"); } }
        public static string RcNewBuy { get { return Pick("새로 산 상품", "New Purchase", "新規購入"); } }
        public static string NewBuyBar { get { return Pick("새로 산 상품 {n}개", "{n} new purchases", "新しく買った商品が{n}件"); } }
        public static string GetNow { get { return Pick("바로 받기", "Download Now", "今すぐダウンロード"); } }
        public static string PDep { get { return Pick("MingToon 코어(무료)가 필요해요. 아틀리에에서 먼저 받아 주세요.", "MingToon Core (free) is required. Please get it on Atelier first.", "MingToon コア(無料)が必要です。先にアトリエで入手してください。"); } }
        public static string PDepB { get { return Pick("아틀리에에서 받기", "Get on Atelier", "アトリエで入手"); } }
    }
}
