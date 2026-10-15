# OnAirDeck Unity Manager

A Unity Editor tool for OnAirDeck buyers:

- sign in and sign out through the browser,
- see your OnAirDeck purchase history,
- download purchased assets straight into the open project.

> Status: 0.4.0 development version. Sign-in, purchase listing, downloads and the sign-in/session edge cases have been tested in Unity 2021.3.18f1. Download verification tools are in `Tools~/Verification`; the release checks below track the remaining work before public release.

## Requirements

Unity **2021.3 or newer** (`package.json` enforces this minimum). Tested on 2021.3.18f1. Newer editors (2022.3, Unity 6) have not been verified yet; older editors are not supported for now, although the code keeps the C# 7.3 / pre-2020.2 fallbacks so support could be widened later.

## Install

In Unity, open **Window ▸ Package Manager ▸ + ▸ Add package from git URL…** and enter:

```
https://github.com/StudioRaming/Onairdeck-Unity-Manager.git
```

Package Manager fetches the package with Git, so Git must be installed. To pin a version, append a tag: `https://github.com/StudioRaming/Onairdeck-Unity-Manager.git#<tag>`.

## Use

1. Open **Window ▸ OnAirDeck Unity Manager** and click **Sign in with browser**. Approve the request on onairdeck.com. The sign-in is saved per computer, so it applies to every Unity project, and lasts 30 days unless you sign out.
2. Your purchases are listed with the files the seller made available for Unity. Items the seller has not enabled for Unity say "Not available in Unity".
3. Click **Download**. The first download of an item makes the purchase non-refundable (the same rule as the website), so Unity asks you to confirm.
   - `.unitypackage` files open Unity's normal import dialog and use the asset paths stored in the package.
   - `.zip` files are extracted, and other files are copied, into `Assets/StudioRaming_Onairdeck/<Product>/`.
4. **Update available** appears when the seller replaced a file after your last download.
5. Each download also saves a signed `onairdeck.license` in the product folder, and while you are signed in a small `OnAirDeck/session.json` marker lives in your local app-data folder (no token inside). Assets that check their license need both; the Manager refreshes the license file silently when it is about to expire.
6. When a newer Manager is published, the window shows **Update**. Packages installed from a git URL update in place; a copy installed from disk must be updated by hand.

Downloads made before 0.2.1 stay in their existing folders. Download again to place loose files or ZIP contents in the new folder. The manager does not move existing assets or their `.meta` files.

Sellers with active OnAirDeck Plus enable **Unity download** for each uploaded file in the Atelier asset editor. Buyers do not need Plus. Only files enabled for Unity and included in the purchased option are downloadable; external links are not downloaded by the manager.

## For sellers: checking the license from your asset

The package ships a small Editor-only assembly, `OnAirDeck.License`, that your editor code can call. It needs no network: it reads the buyer's `onairdeck.license` for your product, verifies OnAirDeck's RSA signature with the embedded public key, and confirms the file belongs to the account currently signed in to the Manager and has not expired.

```csharp
using OnAirDeck.License;
using UnityEditor;
using UnityEngine;

public class MyToolWindow : EditorWindow
{
    const string ProductId = "2d5f83b7-bacf-4104-b352-6c84180830c1"; // your product's id on OnAirDeck

    void OnGUI()
    {
        var license = OnAirDeckLicense.Check(ProductId); // cached for 30 s, safe to call every frame
        if (!license.IsValid)
        {
            EditorGUILayout.HelpBox(license.Message, MessageType.Warning);
            if (license.Status == LicenseStatus.NotSignedIn && GUILayout.Button("Open OnAirDeck Unity Manager"))
                EditorApplication.ExecuteMenuItem("Window/OnAirDeck Unity Manager");
            return;
        }
        // your tool
    }
}
```

- Scripts in a plain `Editor/` folder see the assembly automatically. If your editor code has its own assembly definition, add `OnAirDeck.License` to its references.
- `LicenseStatus` tells you why a check failed: `NotSignedIn`, `LicenseMissing`, `LicenseMalformed`, `InvalidSignature`, `WrongIssuer`, `WrongProduct`, `UserMismatch`, `NotYetValid`, `Expired`. `Message` is a ready-made English sentence.
- Licenses live 7 days and the Manager renews them when its window opens, so a buyer who has not opened the Manager for a week sees `Expired` until they do.
- This is a client-side check. It stops casual copying between accounts; it cannot stop someone who decompiles and edits your assembly. Keep the check inside the code you care about rather than in a separate script that is easy to delete.

## 한국어

### 설치

Unity에서 **Window ▸ Package Manager ▸ + ▸ Add package from git URL…**을 선택하고 아래 주소를 입력하세요.

```
https://github.com/StudioRaming/Onairdeck-Unity-Manager.git
```

Package Manager 는 Git 으로 패키지를 받아오므로 Git 이 설치되어 있어야 합니다. 특정 버전을 고정하려면 주소 뒤에 태그를 붙이세요: `https://github.com/StudioRaming/Onairdeck-Unity-Manager.git#<태그>`. 로컬 테스트는 **Add package from disk…**에서 이 저장소의 `package.json`을 선택하세요.

### 사용

1. **Window ▸ OnAirDeck Unity Manager ▸ Sign in with browser**를 누르고 onairdeck.com에서 로그인을 승인하세요. 로그인은 컴퓨터의 모든 Unity 프로젝트에서 공유되며, 로그아웃하지 않으면 30일 동안 유지됩니다.
2. 구매 목록에서 **Download** 또는 **Download again**을 누르세요. 최초 다운로드 시 환불이 불가능해진다는 확인 창이 표시됩니다.
3. 이미지 등 일반 파일과 ZIP 압축 해제 결과는 `Assets/StudioRaming_Onairdeck/<상품명>/`에 저장됩니다. `.unitypackage`는 Unity 가져오기 창을 열며 패키지에 저장된 경로를 사용합니다.
4. 판매자가 파일을 변경하면 **Update available**이 표시됩니다. **Refresh**로 목록을 갱신하세요.
5. 다운로드 시 상품 폴더에 서명된 `onairdeck.license` 파일이 함께 저장되고, 로그인 중에는 로컬 앱데이터 폴더에 `OnAirDeck/session.json` 표식(토큰은 포함되지 않음)이 유지됩니다. 라이선스를 확인하는 에셋은 이 두 파일이 필요하며, 만료가 가까워지면 매니저가 자동으로 갱신합니다.
6. 새 버전의 매니저가 공개되면 창에 **Update** 버튼이 표시됩니다. git URL로 설치한 패키지는 그 자리에서 업데이트되고, 디스크에서 설치한 복사본은 직접 갱신해야 합니다.

판매자는 활성 Plus 멤버십이 있어야 Atelier 상품 편집기의 파일별 **Unity download**를 켤 수 있습니다. 구매자에게는 Plus가 필요하지 않습니다. 구매 옵션에 포함되지 않은 파일과 외부 링크는 Unity에서 다운로드할 수 없습니다. 이전 버전으로 받은 파일은 기존 폴더에 남습니다.

### 판매자용: 에셋에서 라이선스 확인하기

패키지에 포함된 에디터 전용 어셈블리 `OnAirDeck.License`를 에디터 코드에서 호출하면 네트워크 없이 라이선스를 확인할 수 있습니다. `OnAirDeckLicense.Check("<상품 ID>")`는 구매자의 `onairdeck.license`를 찾아 OnAirDeck의 RSA 서명을 내장 공개키로 검증하고, 매니저에 로그인된 계정과 일치하는지, 만료되지 않았는지 확인합니다. 결과의 `IsValid`와 `Status`(`NotSignedIn`, `LicenseMissing`, `InvalidSignature`, `Expired`, `UserMismatch` 등), `Message`(영문 안내문)를 사용하세요. 결과는 30초간 캐시되므로 `OnGUI`에서 호출해도 됩니다. 에디터 코드에 어셈블리 정의가 있으면 참조에 `OnAirDeck.License`를 추가하세요. 영문 섹션에 예제 코드가 있습니다. 클라이언트 측 검사이므로 계정 간 복사는 막지만, 어셈블리를 디컴파일해 수정하는 사람까지 막지는 못합니다.

## 日本語

### インストール

Unityで **Window ▸ Package Manager ▸ + ▸ Add package from git URL…** を選び、次のURLを入力してください。

```
https://github.com/StudioRaming/Onairdeck-Unity-Manager.git
```

Package Manager は Git でパッケージを取得するため、Git のインストールが必要です。バージョンを固定するには URL の末尾にタグを付けてください: `https://github.com/StudioRaming/Onairdeck-Unity-Manager.git#<タグ>`。ローカルテストでは **Add package from disk…** から、このリポジトリの `package.json` を選択してください。

### 使い方

1. **Window ▸ OnAirDeck Unity Manager ▸ Sign in with browser** を押し、onairdeck.comでサインインを承認してください。サインインは同じコンピューターの全Unityプロジェクトで共有され、サインアウトしなければ30日間有効です。
2. 購入一覧の **Download** または **Download again** を押してください。初回ダウンロード時は、購入が返金対象外になることを確認するダイアログが表示されます。
3. 画像などの通常ファイルとZIPの展開結果は `Assets/StudioRaming_Onairdeck/<商品名>/` に保存されます。`.unitypackage` はUnityのインポート画面を開き、パッケージに保存されたパスを使用します。
4. 出品者がファイルを変更すると **Update available** が表示されます。**Refresh** で一覧を更新してください。
5. ダウンロード時に署名付きの `onairdeck.license` が商品フォルダーに保存され、サインイン中はローカルのアプリデータフォルダーに `OnAirDeck/session.json` のマーカー（トークンは含みません）が置かれます。ライセンスを確認するアセットにはこの2つのファイルが必要で、期限が近づくとマネージャーが自動的に更新します。
6. 新しいバージョンのマネージャーが公開されると、ウィンドウに **Update** ボタンが表示されます。git URLでインストールしたパッケージはその場で更新され、ディスクからインストールしたコピーは手動で更新してください。

出品者は有効なPlusメンバーシップで、Atelierの商品編集画面の各ファイルの **Unity download** を有効にできます。購入者にPlusは不要です。購入オプションに含まれないファイルや外部リンクはUnityからダウンロードできません。以前のバージョンで取得したファイルは元のフォルダーに残ります。

### 出品者向け: アセットからライセンスを確認する

パッケージに含まれるエディター専用アセンブリ `OnAirDeck.License` をエディターコードから呼び出すと、ネットワークなしでライセンスを確認できます。`OnAirDeckLicense.Check("<商品ID>")` は購入者の `onairdeck.license` を探し、OnAirDeckのRSA署名を内蔵の公開鍵で検証し、マネージャーにサインイン中のアカウントと一致するか、期限切れでないかを確認します。結果の `IsValid` と `Status`（`NotSignedIn`、`LicenseMissing`、`InvalidSignature`、`Expired`、`UserMismatch` など）、`Message`（英語の案内文）を使ってください。結果は30秒間キャッシュされるため `OnGUI` から呼んでも問題ありません。エディターコードにアセンブリ定義がある場合は参照に `OnAirDeck.License` を追加してください。サンプルコードは英語セクションにあります。クライアント側の確認なのでアカウント間のコピーは防げますが、アセンブリを逆コンパイルして書き換える相手までは防げません。

## Test a local copy (development)

In any Unity project: **Window ▸ Package Manager ▸ + ▸ Add package from disk…** and select this folder's `package.json`. Changes to the files are picked up when Unity recompiles.

To compile-check without opening Unity, run `dotnet build` in `Tools~/CompileCheck` (instructions inside the `.csproj`).

## Release verification

- [x] Browser sign-in and purchase listing tested by the owner in Unity 2021.3.18f1.
- [x] `test_sell` image downloaded by the owner with a buyer account.
- [x] Button labels checked by the owner at the minimum window width; `miku2018.jpg` downloaded again into `Assets/StudioRaming_Onairdeck/test_sell/` (2026-10-08).
- [x] Verify ZIP extraction and request cancellation/failure cache cleanup with generated fixtures in the real Unity 2021.3.18f1 Editor.
- [x] Import two generated `.unitypackage` files in Unity 2021.3.18f1, including a script that causes a domain reload; verify the second import completes and cached packages are removed.
- [x] Sign-in edge cases verified headlessly against the real listener and PKCE code: Deny, state mismatch, missing code, timeout, Cancel, favicon requests and port release (24-check suite, also in CI).
- [x] Editor runner in Unity 2021.3.18f1 (2026-10-08): **13/13 passed**, including expired/malformed/valid saved sign-ins, rejected token (401) and sign-out on a throwaway key, with the real sign-in confirmed unchanged.
- [x] **Deny** clicked by the owner on the real consent page; the window showed the expected message (2026-10-08).
- [ ] Install the package **from the git URL** in a fresh Unity 2021.3.18f1 project (every test so far used "Add package from disk").
- [ ] Later: verify 2022.3 and Unity 6 before raising the supported range.
- [x] Repository made public (2026-10-08).
- [ ] Choose and push the release tag.

Once a release is tagged, install a fixed version with `https://github.com/StudioRaming/Onairdeck-Unity-Manager.git#<release-tag>` rather than following a moving branch.

Repeatable download checks: [Tools~/Verification/README.md](Tools~/Verification/README.md). Filesystem tests compile the production extraction/copy code; the Editor runner exercises native requests and package imports using unique local fixtures without changing purchases.
