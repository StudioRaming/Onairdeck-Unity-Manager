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
    }
}
