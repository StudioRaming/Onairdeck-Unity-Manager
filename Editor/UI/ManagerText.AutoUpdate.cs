namespace OnAirDeck.UnityManager
{
    /// <summary>The automatic self-update. Placeholders in braces.</summary>
    internal static partial class ManagerText
    {
        /// <summary>{a} the version before, {b} the version now.</summary>
        public static string AuDone { get { return Pick("업데이트됨 {a} → {b}. Atelier Manager가 스스로 새 버전을 받았어요.", "Updated {a} → {b}. Atelier Manager installed its new version by itself.", "アップデート済み {a} → {b}。Atelier Managerが新しいバージョンを自動で受け取りました。"); } }
        /// <summary>{a} the version kept, {b} the version that failed.</summary>
        public static string AuBack { get { return Pick("{b}이(가) 이 Unity에서 컴파일되지 않아 {a}(으)로 되돌렸어요. 이 버전은 다시 자동으로 받지 않아요.", "{b} did not compile in this Unity, so {a} was put back. This version is not installed automatically again.", "{b}がこのUnityでコンパイルできなかったため、{a}に戻しました。このバージョンは自動では再び受け取りません。"); } }
        /// <summary>{v} the version being installed.</summary>
        public static string AuRunning { get { return Pick("Atelier Manager {v}을(를) 받는 중이에요. 끝나면 스크립트를 다시 불러와요.", "Installing Atelier Manager {v}. Scripts reload when it is done.", "Atelier Manager {v}を受け取っています。終わるとスクリプトを再読み込みします。"); } }
        /// <summary>{v} version, {m} reason.</summary>
        public static string AuFailed { get { return Pick("Atelier Manager {v} 자동 업데이트에 실패했어요: {m}. 지금 버전은 그대로이고, 업데이트 버튼으로 다시 받을 수 있어요.", "The automatic update to Atelier Manager {v} failed: {m}. The current version is unchanged; the Update button installs it again.", "Atelier Manager {v}の自動アップデートに失敗しました: {m}。現在のバージョンはそのままで、アップデートボタンで再度受け取れます。"); } }
    }
}
