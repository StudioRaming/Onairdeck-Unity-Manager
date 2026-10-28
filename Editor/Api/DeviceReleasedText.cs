using System;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Texts for the "this PC was released" bar (contract v7). A pure helper over a language index (0 ko, 1 en, 2 ja, the
    /// order of SRLang) because the window code that shows it lives in Editor/UI. The window shows the bar while
    /// DeviceLimitNotice.Current.Kind is DeviceReleased, and its button runs DeviceLimitNotice.RetryAsync() (a click: the
    /// retries send X-Atelier-Reactivate). A full seat then comes back as the normal device_limit screen.
    /// </summary>
    internal static class DeviceReleasedText
    {
        public static string Title(int language)
        {
            switch (language)
            {
                case 0: return "이 PC는 해제되어 있어요";
                case 2: return "このPCは解除されています";
                default: return "This PC is released";
            }
        }

        public static string Body(int language)
        {
            switch (language)
            {
                case 0: return "자동 갱신은 멈춰 있어요. 이 PC에서 다시 쓰려면 아래 버튼을 눌러 주세요.";
                case 2: return "自動更新は止まっています。このPCでまた使うには下のボタンを押してください。";
                default: return "Automatic renewal is paused. Press the button to use this PC again.";
            }
        }

        public static string Button(int language)
        {
            switch (language)
            {
                case 0: return "이 PC에서 다시 사용";
                case 2: return "このPCで再び使う";
                default: return "Use on this PC again";
            }
        }
    }
}
