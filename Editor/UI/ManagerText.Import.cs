namespace OnAirDeck.UnityManager
{
    /// <summary>The confirm of the one-button import ("Unity로 가져오기" on the website). Placeholders in braces.</summary>
    internal static partial class ManagerText
    {
        /// <summary>{n} is the product name.</summary>
        public static string ImpTitle { get { return Pick("{n} 가져오기", "Import {n}", "{n}をインポート"); } }
        /// <summary>{n} product, {p} project folder name, {path} its full path.</summary>
        public static string ImpBody { get { return Pick("웹에서 요청한 {n}을(를) 이 Unity 프로젝트로 가져올까요?\n\n프로젝트: {p}\n위치: {path}", "Import {n}, requested on the website, into this Unity project?\n\nProject: {p}\nLocation: {path}", "ウェブでリクエストした{n}を、このUnityプロジェクトにインポートしますか?\n\nプロジェクト: {p}\n場所: {path}"); } }
        public static string ImpRefund { get { return Pick("처음 받으면 이 상품은 환불할 수 없어요.", "Once you download it for the first time, you cannot get a refund for this product.", "初めてダウンロードすると、この商品は返金できません。"); } }
        public static string ImpImport { get { return Pick("가져오기", "Import", "インポート"); } }
        public static string ImpCancel { get { return Pick("취소", "Cancel", "キャンセル"); } }
        /// <summary>The request goes back to the website, so another open project can take it.</summary>
        public static string ImpNotHere { get { return Pick("이 프로젝트 아님", "Not this project", "このプロジェクトではない"); } }
        /// <summary>{p} a package this import installed whose package.json cannot be read now.</summary>
        public static string ImpReadFail { get { return Pick("방금 설치한 {p}의 package.json을 읽지 못해서 나머지는 설치하지 않았어요. 잠시 뒤 목록에서 설치해 주세요.", "The package.json of {p}, installed a moment ago, could not be read, so the rest was not installed. Install it from the list in a moment.", "先ほどインストールした{p}のpackage.jsonを読めなかったため、残りはインストールしていません。少し後に一覧からインストールしてください。"); } }
        /// <summary>{p} package, {v} the version(s) the packages installed before need.</summary>
        public static string ImpPinFail { get { return Pick("{p}은(는) 먼저 설치한 패키지가 정한 버전({v})으로만 설치할 수 있는데, 받을 수 있는 버전에 없어요. 나머지는 설치하지 않았어요. 목록에서 버전을 확인해 주세요.", "{p} can only be installed at the version the packages installed before need ({v}), and that version is not offered. The rest was not installed; check the versions in the list.", "{p}は先にインストールしたパッケージが決めたバージョン({v})でしかインストールできませんが、そのバージョンがありません。残りはインストールしていません。一覧でバージョンを確認してください。"); } }
    }
}
