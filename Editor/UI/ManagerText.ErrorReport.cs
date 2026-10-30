namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// The install error report card and the button next to a red message. All of its copy is in this block (ko, en, ja), so the
    /// final wording can be swapped here without touching the window. The Korean is the wording agreed with the WARUDO Manager.
    /// Placeholders in braces.
    /// </summary>
    internal static partial class ManagerText
    {
        /// <summary>Card title; {n} is the product name.</summary>
        public static string ErTitle { get { return Pick("설치한 {n}에서 오류가 났어요", "{n} ran into an error after it was installed", "インストールした{n}でエラーが出ました"); } }
        public static string ErShowLog { get { return Pick("보낼 내용 보기", "Show what will be sent", "送信する内容を見る"); } }
        /// <summary>Under the expander: the text below is exactly what is sent.</summary>
        public static string ErLogNote { get { return Pick("아래 내용이 그대로 전송돼요. 개인 폴더 경로와 사용자 이름은 가려져 있어요.", "The text below is sent as it is. Your personal folder path and user name are hidden.", "以下の内容がそのまま送信されます。個人フォルダのパスとユーザー名は隠してあります。"); } }
        public static string ErNoteLabel { get { return Pick("어떤 상황이었는지 덧붙여 주세요", "Tell us what happened", "どんな状況だったか補足してください"); } }
        public static string ErNoteHint { get { return Pick("예: 설치하고 Unity를 켰더니 이런 화면이 나왔어요 (선택)", "e.g. I installed it, opened Unity and saw this (optional)", "例: インストールしてUnityを起動したら、この画面が出ました (任意)"); } }
        public static string ErSend { get { return Pick("보내기", "Send", "送信"); } }
        public static string ErSkip { get { return Pick("보내지 않기", "Don't send", "送らない"); } }
        public static string ErSending { get { return Pick("보내는 중…", "Sending…", "送信中…"); } }
        public static string ErSignIn { get { return Pick("로그인하면 보낼 수 있어요.", "Sign in to send this report.", "サインインすると送信できます。"); } }
        /// <summary>{n} more reports wait behind this one.</summary>
        public static string ErMore { get { return Pick("이어서 오류 {n}건", "{n} more after this", "このあと{n}件"); } }
        /// <summary>Shown in the message bar after sending, when the server returned no id.</summary>
        public static string ErThanks { get { return Pick("보냈어요. 알려 주셔서 고마워요.", "Sent. Thank you for telling us.", "送信しました。ご連絡ありがとうございます。"); } }
        /// <summary>Shown in the message bar after sending; {id} is the short report id.</summary>
        public static string ErSent { get { return Pick("보냈어요. 접수 번호 {id}", "Sent. Reference {id}", "送信しました。受付番号 {id}"); } }
        /// <summary>The same error was already sent from this project.</summary>
        public static string ErDuplicate { get { return Pick("같은 오류는 이미 알려 주셨어요.", "You already told us about this error.", "同じエラーはすでに送信済みです。"); } }
        /// <summary>Button next to a red message.</summary>
        public static string ErBtn { get { return Pick("버그 리포트 보내기", "Send a bug report", "バグレポートを送る"); } }
    }
}
