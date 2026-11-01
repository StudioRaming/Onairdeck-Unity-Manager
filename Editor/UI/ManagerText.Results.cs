namespace OnAirDeck.UnityManager
{
    /// <summary>Install results, the switch from VCC/BOOTH, and the core-first rule of add-ons. Placeholders in braces.</summary>
    internal static partial class ManagerText
    {
        /// <summary>{n} failed items selected again.</summary>
        public static string RetryFailed { get { return Pick("실패한 {n}개 다시 시도", "Retry {n} failed", "失敗した{n}件を再試行"); } }
        /// <summary>{r} the reason and what to do next.</summary>
        public static string LastFailed { get { return Pick("지난번 설치를 못 했어요. {r}", "The last install did not finish. {r}", "前回のインストールはできませんでした。{r}"); } }
        public static string TryAgain { get { return Pick("다시 시도", "Try Again", "再試行"); } }

        public static string VsTitle { get { return Pick("기존 MingToon을 아틀리에 매니저 버전으로 바꿀까요?", "Switch the existing MingToon to the Atelier Manager version?", "既存のMingToonをアトリエマネージャー版に切り替えますか?"); } }
        /// <summary>{list} one line per package. Short: Unity's dialog on Windows clips a longer body.</summary>
        public static string VsBody { get { return Pick("{list}\n\nVCC(또는 BOOTH 설치기)로 설치된 MingToon이 있어요. 바꾸면 VCC 목록(vpm-manifest.json)에서 빼고 아틀리에 매니저 버전으로 설치해요. 머티리얼과 프리셋은 그대로이고, 원래 파일은 백업해 두니 되돌릴 수 있어요.", "{list}\n\nMingToon is installed with VCC (or the BOOTH installer). Switching takes it out of VCC's list (vpm-manifest.json) and installs the Atelier Manager version. Materials and presets stay; the original file is backed up, so you can undo this.", "{list}\n\nVCC(またはBOOTHのインストーラー)で入れたMingToonがあります。切り替えるとVCCの一覧(vpm-manifest.json)から外し、アトリエマネージャー版を入れます。マテリアルとプリセットはそのままで、元のファイルはバックアップするので元に戻せます。"); } }
        public static string VsYes { get { return Pick("바꾸고 설치", "Switch and Install", "切り替えてインストール"); } }
        /// <summary>{r} why the switch could not start.</summary>
        public static string VsFailed { get { return Pick("VCC 목록에서 빼지 못해서 설치하지 않았어요. {r}", "Could not take it out of VCC's list, so nothing was installed. {r}", "VCCの一覧から外せなかったため、インストールしませんでした。{r}"); } }

        /// <summary>{c} the core's name.</summary>
        public static string CoreFirstBtn { get { return Pick("{c}를 먼저 설치해요", "Install {c} First", "先に{c}を入れる"); } }
        /// <summary>{a} the add-on, {c} the core.</summary>
        public static string CoreFirstAsk { get { return Pick("{a}은(는) {c}가 있어야 동작해요. {c}를 먼저 설치한 뒤 {a}를 설치할까요?", "{a} works only with {c}. Install {c} first, then {a}?", "{a}は{c}がないと動きません。先に{c}を入れてから{a}を入れますか?"); } }
        /// <summary>{a} the add-on, {c} the core.</summary>
        public static string CoreMissing { get { return Pick("{a}은(는) {c}가 있어야 동작해요. 이 계정에는 {c}가 없어서 설치하지 않았어요. 아틀리에에서 {c}를 먼저 받아 주세요.", "{a} works only with {c}. This account has no {c}, so nothing was installed. Get {c} on the Atelier first.", "{a}は{c}がないと動きません。このアカウントには{c}がないため、インストールしませんでした。先にアトリエで{c}を入手してください。"); } }
        /// <summary>{c} the core.</summary>
        public static string CoreNeededHint { get { return Pick("{c}가 설치되어 있어야 동작해요.", "Needs {c} installed to work.", "{c}が入っている必要があります。"); } }
    }
}
