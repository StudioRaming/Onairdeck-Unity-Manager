using System.Collections.Generic;
using OnAirDeck.License;

namespace OnAirDeck.UnityManager
{
    /// <summary>The offline licence panel (ManagerWindow.Offline) and what a closed account is told. Placeholders in braces.</summary>
    internal static partial class ManagerText
    {
        public static string OlEntry { get { return Pick("라이선스 파일 사용 (탈퇴한 계정 또는 서비스 종료)", "Use a licence file (closed account or service end)", "ライセンスファイルを使う(退会済みまたはサービス終了)"); } }
        public static string OlTitle { get { return Pick("라이선스 파일", "Offline licences", "ライセンスファイル"); } }
        public static string OlSub { get { return Pick("이 화면은 로그인과 인터넷 연결 없이 쓸 수 있습니다. 가져온 파일은 이 PC의 모든 프로젝트에 적용됩니다.", "This screen needs no sign-in and no internet. An imported file serves every project on this PC.", "この画面はログインもインターネット接続もなしで使えます。読み込んだファイルはこのPCのすべてのプロジェクトに適用されます。"); } }
        public static string OlBack { get { return Pick("뒤로", "Back", "戻る"); } }
        public static string OlCodeTitle { get { return Pick("이 PC의 기기 코드", "This PC's device code", "このPCの機器コード"); } }
        public static string OlNoDevice { get { return Pick("이 PC의 기기 정보를 읽지 못했습니다. 고객센터에 문의해 주세요.", "This PC's device id could not be read. Please contact support.", "このPCの機器情報を読み取れませんでした。カスタマーサポートにお問い合わせください。"); } }
        public static string OlLabel { get { return Pick("이름표(선택)", "Label (optional)", "ラベル(任意)"); } }
        public static string OlCopy { get { return Pick("복사", "Copy", "コピー"); } }
        public static string OlSaveCode { get { return Pick("파일로 저장", "Save as file", "ファイルに保存"); } }
        public static string OlImport { get { return Pick("라이선스 파일 가져오기", "Import licence file", "ライセンスファイルを読み込む"); } }
        public static string OlList { get { return Pick("가져온 라이선스", "Imported licences", "読み込んだライセンス"); } }
        public static string OlEmpty { get { return Pick("아직 가져온 파일이 없습니다.", "No files imported yet.", "まだ読み込んだファイルはありません。"); } }
        public static string OlRemove { get { return Pick("지우기", "Remove", "削除"); } }
        public static string OlRemoveAsk { get { return Pick("이 PC에서 이 라이선스 파일을 지울까요? 내려받은 원본이 있으면 다시 가져올 수 있습니다.", "Remove this licence file from this PC? You can import it again from your download.", "このPCからこのライセンスファイルを削除しますか?ダウンロードした元のファイルがあれば再度読み込めます。"); } }
        public static string OlNotRecognized { get { return Pick("라이선스 파일이 아닙니다. 탈퇴 안내 메일이나 화면에서 내려받은 파일을 골라 주세요.", "This is not a licence file. Choose the file from the withdrawal page or email.", "ライセンスファイルではありません。退会案内のメールまたは画面からダウンロードしたファイルを選んでください。"); } }
        public static string OlClosed { get { return Pick("계정이 탈퇴 처리되었습니다. 탈퇴 안내 화면이나 메일에서 받은 라이선스 파일을 가져오세요.", "Your account was closed. Import the licence file from the withdrawal page or email.", "アカウントは退会処理されました。退会案内の画面またはメールで受け取ったライセンスファイルを読み込んでください。"); } }

        /// <summary>One licence's state on this PC.</summary>
        public static string OlStatus(LicenseStatus status)
        {
            switch (status)
            {
                case LicenseStatus.Valid: return Pick("이 PC에서 사용할 수 있음", "Works on this PC", "このPCで使えます");
                case LicenseStatus.OfflineDeviceMismatch: return Pick("다른 PC용 파일", "For another PC", "別のPC用のファイル");
                case LicenseStatus.OfflineKeyUnknown: return Pick("이 버전에서 읽을 수 없음. 아틀리에 매니저를 업데이트해 주세요.", "This version cannot read it. Update the Atelier Manager.", "このバージョンでは読み取れません。アトリエマネージャーを更新してください。");
                case LicenseStatus.OfflineDeviceUnavailable: return Pick("이 PC의 기기 정보를 읽지 못함", "This PC's device id could not be read", "このPCの機器情報を読み取れません");
                default: return Pick("손상된 파일", "Damaged file", "破損したファイル");
            }
        }

        /// <summary>The import's outcome with its reasons: nothing is skipped silently.</summary>
        public static string OlResult(OfflineImportResult r)
        {
            if (r.NotRecognized) return OlNotRecognized;
            var parts = new List<string>();
            if (r.Imported.Count > 0) parts.Add(Pick("가져옴: ", "Imported: ", "読み込み: ") + string.Join(", ", r.Imported.ToArray()));
            if (r.OtherPc > 0) parts.Add(Pick("{n}개는 다른 PC용 파일입니다", "{n} files are for another PC", "{n}件は別のPC用のファイルです").Replace("{n}", r.OtherPc.ToString()));
            if (r.OtherClient > 0) parts.Add(Pick("{n}개는 WARUDO 파일입니다. WARUDO 아틀리에 매니저에서 가져오세요", "{n} are WARUDO files: import them in the WARUDO Atelier Manager", "{n}件はWARUDOのファイルです。WARUDOアトリエマネージャーで読み込んでください").Replace("{n}", r.OtherClient.ToString()));
            if (r.UnknownKey > 0) parts.Add(Pick("{n}개는 이 버전에서 읽을 수 없습니다. 아틀리에 매니저를 업데이트해 주세요", "{n} cannot be read by this version: update the Atelier Manager", "{n}件はこのバージョンでは読み取れません。アトリエマネージャーを更新してください").Replace("{n}", r.UnknownKey.ToString()));
            if (r.Invalid > 0) parts.Add(Pick("{n}개는 손상되었습니다", "{n} are damaged", "{n}件は破損しています").Replace("{n}", r.Invalid.ToString()));
            return parts.Count > 0 ? string.Join("\n", parts.ToArray()) : Pick("가져올 항목이 없습니다.", "Nothing to import.", "読み込む項目はありません。");
        }
    }
}
