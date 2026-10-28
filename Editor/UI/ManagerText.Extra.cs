namespace OnAirDeck.UnityManager
{
    /// <summary>Strings the mockup table has no entry for (generic versions of its example sentences, messages, dialogs). Placeholders in braces.</summary>
    internal static partial class ManagerText
    {
        public static string BackupLeft { get { return Pick("이전 설치 백업이 남아 있어요. 설치가 끝나지 않았어요.", "A backup of the old install is still there. The install did not finish.", "以前のインストールのバックアップが残っています。インストールが完了していません。"); } }
        public static string Restore { get { return Pick("되돌리기", "Restore", "元に戻す"); } }
        public static string Close { get { return Pick("닫기", "Close", "閉じる"); } }
        public static string Loading { get { return Pick("불러오는 중...", "Loading...", "読み込み中..."); } }
        public static string Preparing { get { return Pick("준비하는 중...", "Preparing...", "準備中..."); } }
        public static string Sending { get { return Pick("보내는 중...", "Sending...", "送信中..."); } }
        public static string None { get { return Pick("없음", "None", "なし"); } }
        public static string NoMatch { get { return Pick("조건에 맞는 상품이 없어요.", "No products match.", "条件に合う商品がありません。"); } }
        public static string PickProduct { get { return Pick("왼쪽에서 상품을 골라 주세요.", "Pick a product on the left.", "左から商品を選んでください。"); } }
        public static string NoVersions { get { return Pick("버전 기록이 아직 없어요.", "No version history yet.", "バージョン履歴はまだありません。"); } }
        public static string NoDevices { get { return Pick("이 라이선스를 쓰는 기기가 아직 없어요.", "No device uses this license yet.", "このライセンスを使うデバイスはまだありません。"); } }
        public static string NoLimits { get { return Pick("기기 수가 정해진 라이선스가 없어요.", "None of your licenses has a device limit.", "台数制限のあるライセンスはありません。"); } }
        public static string FavOn { get { return Pick("즐겨찾기에 넣기", "Add to favorites", "お気に入りに追加"); } }
        public static string FavOff { get { return Pick("즐겨찾기에서 빼기", "Remove from favorites", "お気に入りから外す"); } }
        public static string OtherSeller { get { return Pick("기타", "Other", "その他"); } }
        public static string Requested { get { return Pick("다른 패키지가 요청했어요", "Requested by another package", "他のパッケージが要求しました"); } }
        public static string NotOwned { get { return Pick("이 계정에는 요청한 상품이 없어요.", "This account does not have the requested product.", "このアカウントには要求された商品がありません。"); } }

        public static string ReqBodyPlain { get { return Pick("새 버전으로 업데이트하면 바로 다시 쓸 수 있어요.", "Update to the new version and you can use it again right away.", "新しいバージョンにアップデートするとすぐに使えます。"); } }
        public static string SiLeft { get { return Pick("남은 시간 {t}", "Time left {t}", "残り時間 {t}"); } }

        public static string SelectedN { get { return Pick("{n}개 골랐어요", "{n} selected", "{n}件選択中"); } }
        public static string BatchRunning { get { return Pick("{p} 받는 중", "Getting {p}", "{p} 処理中"); } }
        public static string BatchDone { get { return Pick("{d}개를 마쳤어요.", "{d} finished.", "{d}件完了しました。"); } }
        public static string BatchDoneFailed { get { return Pick("{d}개를 마쳤고 {f}개는 못 했어요.", "{d} finished, {f} failed.", "{d}件完了、{f}件は失敗しました。"); } }
        public static string DevCount { get { return Pick("{u} / {m}대", "{u} / {m} devices", "{u} / {m}台"); } }
        public static string LastSeen { get { return Pick("마지막 사용 {d}", "Last used {d}", "最終使用 {d}"); } }

        public static string StNewFile { get { return Pick("새 {l} 버전이 있어요", "New version {l} is available", "新しいバージョン {l} があります"); } }
        public static string ReissueLicense { get { return Pick("라이선스 다시 받기", "Get License Again", "ライセンスを再取得"); } }
        public static string LicenseReissued { get { return Pick("라이선스를 다시 받았어요.", "The license was renewed.", "ライセンスを更新しました。"); } }

        public static string Cancelled { get { return Pick("취소했어요.", "Cancelled.", "キャンセルしました。"); } }
        public static string Declined { get { return Pick("설치하지 않았어요. 바뀐 것은 없어요.", "Not installed. Nothing was changed.", "インストールしませんでした。変更はありません。"); } }
        public static string CleanupNeeded { get { return Pick("이전 설치 정리 필요: 예전 방식으로 설치된 파일이 있어요. 설치를 누르면 백업으로 옮기고 새로 설치해요.", "Old install needs clean-up: files from the old way of installing were found. Install moves them to a backup and installs again.", "以前のインストールの整理が必要です: 古い方法で入れたファイルがあります。インストールするとバックアップに移して入れ直します。"); } }

        public static string MisBody { get { return Pick("{pkg} {ver} 버전은 {dep} {req} 버전이 필요한데 {have}. 버튼을 누르면 맞는 버전으로 설치해요.", "{pkg} {ver} needs {dep} {req}, but {have}. The button installs the matching version.", "{pkg} {ver} には {dep} {req} が必要ですが、{have}。ボタンで合うバージョンを入れます。"); } }
        public static string MisNone { get { return Pick("{dep} 패키지가 설치되어 있지 않아요", "{dep} is not installed", "{dep} が入っていません"); } }
        public static string MisHave { get { return Pick("{dep} {i} 버전이 설치되어 있어요", "{dep} {i} is installed", "{dep} {i} が入っています"); } }
        public static string DepBody { get { return Pick("{p} 패키지가 필요해요. 아틀리에에서 먼저 받아 주세요.", "The package {p} is required. Please get it on Atelier first.", "パッケージ {p} が必要です。先にアトリエで入手してください。"); } }
        public static string RenBody { get { return Pick("{n} 라이선스를 갱신하지 못했어요. 아틀리에에서 라이선스 상태를 확인해 주세요.", "The license of {n} could not be renewed. Please check the license status on Atelier.", "{n} のライセンスを更新できませんでした。アトリエでライセンスの状態を確認してください。"); } }

        public static string DlBody { get { return Pick("'{name}' 라이선스는 {max}대까지 쓸 수 있어요. 지금 {used}대가 쓰고 있어요.", "The license '{name}' allows {max} devices. {used} are using it now.", "ライセンス「{name}」は{max}台まで使えます。今は{used}台が使っています。"); } }
        public static string Sent { get { return Pick("보냈어요. 접수 번호 {id}", "Sent. Reference {id}", "送信しました。受付番号 {id}"); } }
        public static string KPackages { get { return Pick("설치된 패키지", "Installed packages", "インストール済みパッケージ"); } }
        public static string AttachLog { get { return Pick("최근 Console 오류 {n}개 함께 보내기", "Attach {n} recent Console errors", "最近のConsoleエラー {n} 件を添付"); } }

        public static string DlgDlT { get { return Pick("받기", "Download", "ダウンロード"); } }
        public static string DlgDl { get { return Pick("받으면 이 상품은 환불할 수 없어요. 받을까요?", "You cannot get a refund for this product once you download it. Download it?", "ダウンロードすると、この商品は返金できません。ダウンロードしますか?"); } }
        public static string DlgInT { get { return Pick("{n} {v} 설치", "Install {n} {v}", "{n} {v} をインストール"); } }
        public static string DlgIn { get { return Pick("{n} {v} 버전을 설치할까요? 지금 버전은 자동으로 백업돼요.", "Install {n} {v}? Your current version is backed up automatically.", "{n} {v} をインストールしますか? 今のバージョンは自動でバックアップされます。"); } }
        public static string DlgBatchT { get { return Pick("한 번에 받기", "Download Selected", "まとめてダウンロード"); } }
        public static string DlgBatch { get { return Pick("선택한 {n}개를 한 번에 받을까요? 받은 뒤 바로 가져와요. 받으면 환불할 수 없어요.", "Download the {n} selected products at once? They are imported right after the download. You cannot get a refund once downloaded.", "選んだ{n}件をまとめてダウンロードしますか? ダウンロード後すぐにインポートします。ダウンロードすると返金できません。"); } }
        public static string DlgUpdAll { get { return Pick("새 버전이 있는 {n}개를 모두 업데이트할까요? 지금 버전은 자동으로 백업돼요.", "Update all {n} products that have a new version? Current versions are backed up automatically.", "新しいバージョンのある{n}件をすべてアップデートしますか? 今のバージョンは自動でバックアップされます。"); } }
        public static string DlgRemT { get { return Pick("기기 빼기", "Remove Device", "デバイスを外す"); } }
        public static string DlgRem { get { return Pick("'{name}'을 이 라이선스에서 뺄까요? 뺀 기기는 다음 확인 때(최대 7일) 사용이 멈춰요.", "Remove '{name}' from this license? A removed device stops working at its next check (up to 7 days).", "「{name}」をこのライセンスから外しますか? 外したデバイスは、次の確認のとき(最大7日)に使えなくなります。"); } }

        public static string SelfUpdating { get { return Pick("매니저를 업데이트하는 중이에요. 끝나면 Unity가 스크립트를 다시 불러와요.", "Updating the Manager. Unity reloads scripts when it finishes.", "マネージャーをアップデート中です。終わるとUnityがスクリプトを再読み込みします。"); } }
        public static string SelfUpdated { get { return Pick("{v} 버전으로 업데이트했어요.", "Updated to {v}.", "{v} にアップデートしました。"); } }
        public static string SelfUpdateFailed { get { return Pick("업데이트하지 못했어요: {m}", "The update failed: {m}", "アップデートできませんでした: {m}"); } }
        public static string InfoLatestVersion { get { return Pick("최신 버전", "Latest Version", "最新バージョン"); } }
        public static string FbEmpty { get { return Pick("먼저 내용을 적어 주세요.", "Write a message first.", "先に内容を書いてください。"); } }
        public static string FbTooLong { get { return Pick("내용이 {n}자를 넘었어요.", "The message is longer than {n} characters.", "内容が{n}文字を超えています。"); } }
        public static string FbCodeTooLong { get { return Pick("오류 코드가 {n}자를 넘었어요.", "The error code is longer than {n} characters.", "エラーコードが{n}文字を超えています。"); } }
        public static string HoOpen { get { return Pick("{n} 설치기 열기", "Open the {n} Installer", "{n} インストーラーを開く"); } }
        public static string HoDone { get { return Pick("{n} 설치를 마쳤어요.", "{n} is installed.", "{n} のインストールが完了しました。"); } }
        public static string HoNoMenu { get { return Pick("설치기 메뉴를 찾지 못했어요. 아틀리에에서 설치기를 받아 주세요.", "The installer menu was not found. Get the installer from Atelier.", "インストーラーのメニューが見つかりません。アトリエから入手してください。"); } }
    }
}
