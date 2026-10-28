# Atelier Manager

A Unity Editor tool for OnAirDeck buyers:

- sign in and sign out through the browser,
- see your OnAirDeck purchase history,
- download purchased assets straight into the open project,
- install and update package add-ons (for example the MingToon URP add-on) together with their exact dependencies.

> Status: 0.1.0 development version (Atelier Manager, formerly OnAirDeck Unity Manager 0.6.1). Sign-in, purchase listing, downloads and the sign-in/session edge cases have been tested in Unity 2021.3.18f1. Download verification tools are in `Tools~/Verification`; the release checks below track the remaining work before public release.

## Requirements

Unity **2019.4 or newer** (`package.json` enforces this minimum; the floor is 2019.4 so DRM add-ons built for 2019.4 can use the Manager). The code is C# 7.3 and keeps pre-2020.2 / pre-2021.1 fallbacks (`UnityWebRequest.result`, `PackageInfo.GetAllRegisteredPackages`, `Client.Resolve`). Compile-checked against 2019.4.31f1 and 2021.3.18f1. Newer editors (2022.3, Unity 6) have not been verified yet.

## Install

In Unity, open **Window ▸ Package Manager ▸ + ▸ Add package from git URL…** and enter:

```
https://github.com/StudioRaming/Onairdeck-Unity-Manager.git
```

Package Manager fetches the package with Git, so Git must be installed. To pin a version, append a tag: `https://github.com/StudioRaming/Onairdeck-Unity-Manager.git#<tag>`.

## Use

1. Open **Window ▸ Atelier Manager** and click **Sign in with browser**. Approve the request on onairdeck.com. The sign-in is saved per computer, so it applies to every Unity project, and lasts 30 days unless you sign out.
2. Your purchases are listed with the files the seller made available for Unity. Items the seller has not enabled for Unity say "Not available in Unity".
3. Click **Download**. The first download of an item makes the purchase non-refundable (the same rule as the website), so Unity asks you to confirm.
   - `.unitypackage` files open Unity's normal import dialog and use the asset paths stored in the package.
   - `.zip` files are extracted, and other files are copied, into `Assets/StudioRaming_Onairdeck/<Product>/`.
4. **Update available** appears when the seller replaced a file after your last download.
5. Each download also saves a signed `onairdeck.license` in the product folder, and while you are signed in a small `OnAirDeck/session.json` marker lives in your local app-data folder (no token inside). Assets that check their license need both; the Manager refreshes the license file silently when it is about to expire.
6. When a newer Manager is published, the window shows **Update**. Packages installed from a git URL update in place; a copy installed from disk must be updated by hand.
7. **Device limit.** A seller can limit a license to a number of computers (MingToon and its URP add-on: 3). A computer is one PC, whatever Unity version or project; the Manager sends a hash of the hardware id and the computer name with each signed-in request, never the id itself. On a computer over the limit, sign-in and the purchase list still work, but download, package install and license files for that license are refused until you release one of your other computers (in the Manager, or on the My Page of onairdeck.com). An OnAirDeck Unity Manager of 0.6.1 or older (the former name) cannot be used with a limited license and asks you to update.

Downloads made before 0.2.1 stay in their existing folders. Download again to place loose files or ZIP contents in the new folder. The manager does not move existing assets or their `.meta` files.

Sellers with active OnAirDeck Plus enable **Unity download** for each uploaded file in the Atelier asset editor. Buyers do not need Plus. Only files enabled for Unity and included in the purchased option are downloadable; external links are not downloaded by the manager.

## Install without Git

Each release also ships two files for people who do not have Git (build them with `python Tools~/Packaging/build_manager_packages.py`, which checks both before it reports success):

- `Atelier-Manager.unitypackage` (always the latest, the same file as the versioned one; link: `releases/latest/download/Atelier-Manager.unitypackage`) and `Atelier-Manager-<version>.unitypackage`: **Assets ▸ Import Package ▸ Custom Package…**. The files land in `Packages/com.studioraming.manager` with the GUIDs of this repository.
- `com.studioraming.manager-<version>.zip`: unzip so that `package.json` sits in `Packages/com.studioraming.manager/`.

A Manager installed this way (an *embedded* package) updates itself: when the release carries a download URL, **Update** downloads the zip, validates it like any package (below) and swaps the folder's contents in place; the old contents go to the backup. A copy that is a git working copy refuses and tells you to use git. Manager installs from a git URL or a registry keep the Package Manager update.

## Packages

When your account holds a license with installable package versions (an OnAirDeck purchase or an approved BOOTH claim give the same right), the window shows a **Packages** section above your purchases:

- per package: the version installed in this project (read from `Packages/<name>/package.json`), the newest available version, **Install** / **Update**, and a picker for older versions;
- a **mismatch banner** when an installed package names exact dependency versions that disagree with what is installed (for example the Creator Companion moved the core package alone). One button installs the add-on version equal to your installed dependency when that version exists, otherwise the newest add-on with the dependency versions it requires. The button text names the versions.

Other packages can open the window on their own row: `OnAirDeck.UnityManager.OnAirDeckPackages.OpenAndFocus("<package name>")` (look the type up with `Type.GetType("OnAirDeck.UnityManager.OnAirDeckPackages, OnAirDeck.UnityManager.Editor")` so your package works when the Manager is absent).

### What an install does

1. Asks OnAirDeck for the zip of the chosen version (Unity asks you to confirm first: for a license bought on onairdeck.com the first download makes that purchase non-refundable; a BOOTH claim is not affected) and downloads it over HTTPS.
2. Validates the zip. Every entry must be a plain relative path: a single `..`, rooted, drive or sanitised name fails the whole package (nothing is skipped). The root `package.json` must exist and its `name` and `version` must equal what OnAirDeck listed. `vpmDependencies` must pin exact versions, written `"X"` or `"=X"`; anything else (`^`, `~`, `>=`, ranges, wildcards, spaces) fails the install. The value is copied into `vpm-manifest.json` exactly as `package.json` spells it.
3. Resolves each dependency that is not installed at exactly the named version from a fixed list of trusted VPM listings, searched in order (`https://studioraming.github.io/mingtoon-distribution/index.json`, then `https://studioraming.github.io/mingtoon-distribution/preview/index.json`, the only one that carries prerelease versions), never from a URL inside a zip. The download must match the listing's SHA-256.
4. Before anything moves, matches the `.meta` GUIDs of every zip against `Assets/` and the embedded folders under `Packages/` (not the folders being replaced). Two assets with one GUID break materials and renderer features, which is what an old add-on `.unitypackage` import inside the core folder would cause. If the colliding files sit inside an installed package that the trusted listing has at its installed version, that package is **reinstalled at the same version in the same transaction** (the stray files leave with the swap and stay in the backup, and the result says so). Any other collision (`Assets/`, a package that cannot be restored from a trusted listing) refuses the install before any change and lists up to 10 colliding paths with the GUID.
5. Runs **one transaction** for the add-on and its dependencies: the zips are unpacked into `Temp/OnAirDeck/stage-<id>/`, a journal is written to `Library/OnAirDeck/install-journal.json`, and then the contents of each existing `Packages/<name>` are moved to `Library/OnAirDeck/Backup/<name>-<oldVersion>-<yyyyMMdd-HHmmss>` and the new contents are moved in. The `Packages/<name>` folder itself stays: Unity watches every embedded package folder and keeps watching it after a rename, so replacing the folder would leave every later asset refresh waiting forever (reproduced on Unity 2021.3). A package that was not installed before arrives as a whole folder. If anything fails the moves are undone in reverse order. If Unity is closed or crashes half-way, the next editor load rolls the incomplete journal back and the window reports it.
6. Files of the old folder that the new package does not contain are **not** carried over (the same as the Creator Companion); the result lists them with the backup path.
7. If `Packages/vpm-manifest.json` exists, the entries it already has for the installed packages (in `dependencies` and `locked`) are set to the new versions. Nothing is added, unknown keys are kept, and the file is backed up to `Library/OnAirDeck/Backup/vpm-manifest-<time>/` first.
8. Calls `Client.Resolve()` and `AssetDatabase.Refresh()` once.

The Manager refuses to install a package that an older version put in a plain folder under `Assets/` (`com.studioraming.mingtoon` in `Assets/StudioRaming/MingToon`): migrate that install first. It never writes MingToon's own installer receipt.

## Feedback / 문의

While you are signed in, the **Feedback / 문의** section at the bottom of the window sends a bug report, a feature request or a question to OnAirDeck. It arrives in OnAirDeck's bug-report list with your account, so the team can reply.

- **Kind**: Bug, Feature request or Question. **Message**: up to 4000 characters. **Error code**: optional, up to 200 characters (paste the code or the first line of the error).
- **Attach recent Console errors (N)**: the last 50 errors, exceptions and failed asserts logged since the last script reload, each with up to 20 stack lines and its time. Your home folder in paths is replaced with `~`, and the attachment stays under 20 KB (the newest entries are kept). OnAirDeck masks e-mail addresses, phone numbers and card numbers in the message, the error code and the log before storing them.
- Sent with every message, shown read-only before you send: the Unity version, the Manager version, the operating system and the installed StudioRaming / OnAirDeck packages (`name@version`).
- At most 5 messages per 10 minutes per account.

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
            if (license.Status == LicenseStatus.NotSignedIn && GUILayout.Button("Open Atelier Manager"))
                EditorApplication.ExecuteMenuItem("Window/Atelier Manager");
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

1. **Window ▸ 아틀리에 매니저 ▸ Sign in with browser**를 누르고 onairdeck.com에서 로그인을 승인하세요. 로그인은 컴퓨터의 모든 Unity 프로젝트에서 공유되며, 로그아웃하지 않으면 30일 동안 유지됩니다.
2. 구매 목록에서 **Download** 또는 **Download again**을 누르세요. 최초 다운로드 시 환불이 불가능해진다는 확인 창이 표시됩니다.
3. 이미지 등 일반 파일과 ZIP 압축 해제 결과는 `Assets/StudioRaming_Onairdeck/<상품명>/`에 저장됩니다. `.unitypackage`는 Unity 가져오기 창을 열며 패키지에 저장된 경로를 사용합니다.
4. 판매자가 파일을 변경하면 **Update available**이 표시됩니다. **Refresh**로 목록을 갱신하세요.
5. 다운로드 시 상품 폴더에 서명된 `onairdeck.license` 파일이 함께 저장되고, 로그인 중에는 로컬 앱데이터 폴더에 `OnAirDeck/session.json` 표식(토큰은 포함되지 않음)이 유지됩니다. 라이선스를 확인하는 에셋은 이 두 파일이 필요하며, 만료가 가까워지면 매니저가 자동으로 갱신합니다.
6. 새 버전의 매니저가 공개되면 창에 **Update** 버튼이 표시됩니다. git URL로 설치한 패키지는 그 자리에서 업데이트되고, 디스크에서 설치한 복사본은 직접 갱신해야 합니다.
7. **기기 제한.** 판매자는 라이선스를 쓸 수 있는 컴퓨터 수를 제한할 수 있습니다(MingToon과 URP 애드온은 3대). 기기는 Unity 버전이나 프로젝트와 무관하게 PC 한 대입니다. 매니저는 로그인된 요청마다 하드웨어 ID의 해시와 컴퓨터 이름만 보내고 ID 자체는 보내지 않습니다. 제한을 넘은 컴퓨터에서도 로그인과 구매 목록은 되지만, 그 라이선스의 다운로드·패키지 설치·라이선스 파일 발급은 다른 컴퓨터를 해제할 때까지 거부됩니다(매니저 또는 onairdeck.com 마이페이지에서 해제). 0.6.1 이하 OnAirDeck Unity Manager(이전 이름)는 제한된 라이선스를 쓸 수 없어 업데이트를 안내합니다.

판매자는 활성 Plus 멤버십이 있어야 Atelier 상품 편집기의 파일별 **Unity download**를 켤 수 있습니다. 구매자에게는 Plus가 필요하지 않습니다. 구매 옵션에 포함되지 않은 파일과 외부 링크는 Unity에서 다운로드할 수 없습니다. 이전 버전으로 받은 파일은 기존 폴더에 남습니다.

### Git 없이 설치와 패키지 애드온

릴리스에는 Git이 없는 환경을 위한 파일이 함께 들어 있습니다. `Atelier-Manager.unitypackage`(항상 최신, 버전이 붙은 파일과 같은 내용)와 `Atelier-Manager-<버전>.unitypackage`는 **Assets ▸ Import Package ▸ Custom Package…**로 가져오고, `com.studioraming.manager-<버전>.zip`은 `Packages/com.studioraming.manager/`에 압축을 풀면 됩니다. 이렇게 설치한 매니저는 릴리스에 다운로드 주소가 있으면 **Update**로 스스로 폴더를 교체합니다.

계정에 패키지 버전이 있는 라이선스(OnAirDeck 구매 또는 승인된 BOOTH 청구)가 있으면 구매 목록 위에 **Packages** 섹션이 표시됩니다. 설치된 버전(`Packages/<이름>/package.json`), 최신 버전, **Install/Update**, 이전 버전 선택기가 있습니다. 설치된 패키지가 요구하는 정확한 의존 버전이 실제 설치된 버전과 다르면(예: VCC가 코어만 올린 경우) 경고와 함께 맞출 버전을 적은 버튼이 표시됩니다.

설치는 하나의 트랜잭션입니다. zip을 검증하고(경로가 하나라도 안전하지 않으면 전체 실패, `package.json`의 이름과 버전이 목록과 같아야 하고, 의존성은 정확한 버전이어야 함) 신뢰하는 VPM 목록에서 의존 패키지를 SHA-256 확인 후 받아, 기존 `Packages/<이름>`을 `Library/OnAirDeck/Backup/<이름>-<이전버전>-<시각>`으로 옮기고 새 폴더를 이동해 넣습니다. 실패하면 역순으로 되돌리고, 에디터가 중간에 꺼지면 다음 로드 때 자동으로 되돌립니다. 새 패키지에 없는 예전 파일은 가져오지 않고 결과에 목록으로 보여줍니다. `Packages/vpm-manifest.json`에 이미 있는 항목의 버전만 갱신하며(백업 후, 항목은 추가하지 않음), 마지막에 `Client.Resolve()`와 `AssetDatabase.Refresh()`를 한 번 호출합니다. `Assets/StudioRaming/MingToon`에 예전 방식으로 설치된 경우에는 먼저 그 설치를 정리하라는 안내와 함께 거부합니다.

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

1. **Window ▸ アトリエマネージャー ▸ Sign in with browser** を押し、onairdeck.comでサインインを承認してください。サインインは同じコンピューターの全Unityプロジェクトで共有され、サインアウトしなければ30日間有効です。
2. 購入一覧の **Download** または **Download again** を押してください。初回ダウンロード時は、購入が返金対象外になることを確認するダイアログが表示されます。
3. 画像などの通常ファイルとZIPの展開結果は `Assets/StudioRaming_Onairdeck/<商品名>/` に保存されます。`.unitypackage` はUnityのインポート画面を開き、パッケージに保存されたパスを使用します。
4. 出品者がファイルを変更すると **Update available** が表示されます。**Refresh** で一覧を更新してください。
5. ダウンロード時に署名付きの `onairdeck.license` が商品フォルダーに保存され、サインイン中はローカルのアプリデータフォルダーに `OnAirDeck/session.json` のマーカー（トークンは含みません）が置かれます。ライセンスを確認するアセットにはこの2つのファイルが必要で、期限が近づくとマネージャーが自動的に更新します。
6. 新しいバージョンのマネージャーが公開されると、ウィンドウに **Update** ボタンが表示されます。git URLでインストールしたパッケージはその場で更新され、ディスクからインストールしたコピーは手動で更新してください。
7. **台数制限。** 出品者はライセンスを使えるコンピューターの台数を制限できます（MingToonとURPアドオンは3台）。1台とは、Unityのバージョンやプロジェクトに関係なくPC 1台です。マネージャーはサインイン中のリクエストごとにハードウェアIDのハッシュとコンピューター名だけを送り、IDそのものは送りません。上限を超えたコンピューターでもサインインと購入一覧は使えますが、そのライセンスのダウンロード、パッケージのインストール、ライセンスファイルの発行は、別のコンピューターを解除するまで拒否されます（マネージャー、またはonairdeck.comのマイページで解除）。0.6.1以前のOnAirDeck Unity Manager（旧名称）は制限付きライセンスを使えず、更新を案内します。

出品者は有効なPlusメンバーシップで、Atelierの商品編集画面の各ファイルの **Unity download** を有効にできます。購入者にPlusは不要です。購入オプションに含まれないファイルや外部リンクはUnityからダウンロードできません。以前のバージョンで取得したファイルは元のフォルダーに残ります。

### 出品者向け: アセットからライセンスを確認する

パッケージに含まれるエディター専用アセンブリ `OnAirDeck.License` をエディターコードから呼び出すと、ネットワークなしでライセンスを確認できます。`OnAirDeckLicense.Check("<商品ID>")` は購入者の `onairdeck.license` を探し、OnAirDeckのRSA署名を内蔵の公開鍵で検証し、マネージャーにサインイン中のアカウントと一致するか、期限切れでないかを確認します。結果の `IsValid` と `Status`（`NotSignedIn`、`LicenseMissing`、`InvalidSignature`、`Expired`、`UserMismatch` など）、`Message`（英語の案内文）を使ってください。結果は30秒間キャッシュされるため `OnGUI` から呼んでも問題ありません。エディターコードにアセンブリ定義がある場合は参照に `OnAirDeck.License` を追加してください。サンプルコードは英語セクションにあります。クライアント側の確認なのでアカウント間のコピーは防げますが、アセンブリを逆コンパイルして書き換える相手までは防げません。

### Gitなしのインストールとパッケージアドオン

リリースにはGitがない環境向けのファイルがあります。`Atelier-Manager.unitypackage`（常に最新、バージョン付きファイルと同一）と `Atelier-Manager-<バージョン>.unitypackage` は **Assets ▸ Import Package ▸ Custom Package…** でインポートし、`com.studioraming.manager-<バージョン>.zip` は `Packages/com.studioraming.manager/` に展開してください。この方法で入れたマネージャーは、リリースにダウンロードURLがあれば **Update** で自分のフォルダーを置き換えます。

アカウントにパッケージ版を持つライセンス（OnAirDeckでの購入または承認済みのBOOTH申請）があると、購入一覧の上に **Packages** セクションが表示されます。インストール済みバージョン、最新バージョン、**Install/Update**、旧バージョンの選択ができます。インストール済みパッケージが要求する依存バージョンと実際のバージョンが違う場合（例: VCCがコアだけを更新した）は、警告と、入れるバージョンを書いたボタンが表示されます。

インストールは1つのトランザクションです。zipを検証し（安全でないパスが1つでもあれば全体が失敗、`package.json` の名前とバージョンが一覧と一致、依存は正確なバージョンのみ）、信頼するVPM一覧から依存パッケージをSHA-256確認のうえ取得し、既存の `Packages/<名前>` を `Library/OnAirDeck/Backup/` に移して新しいフォルダーを移動します。失敗時は逆順で戻し、エディターが途中で終了した場合は次回起動時に自動で戻します。新しいパッケージにない旧ファイルは引き継がず、結果に一覧を表示します。`Packages/vpm-manifest.json` に既にある項目のバージョンだけを更新し（バックアップ後、項目は追加しない）、最後に `Client.Resolve()` と `AssetDatabase.Refresh()` を1回呼びます。`Assets/StudioRaming/MingToon` に旧方式で入っている場合は、先にそのインストールを整理するよう案内して拒否します。

## Test a local copy (development)

In any Unity project: **Window ▸ Package Manager ▸ + ▸ Add package from disk…** and select this folder's `package.json`. Changes to the files are picked up when Unity recompiles.

To compile-check without opening Unity, run `dotnet build` in `Tools~/CompileCheck` (instructions inside the `.csproj`; pass `-p:UnityManaged=` for another installed editor).

To build the Git-free install files: `python Tools~/Packaging/build_manager_packages.py` (`--out`, `--version`). It stops if a file or folder lacks its `.meta`, then reopens both outputs and verifies entries, contents and GUIDs.

## Release verification

- [x] Browser sign-in and purchase listing tested by the owner in Unity 2021.3.18f1.
- [x] `test_sell` image downloaded by the owner with a buyer account.
- [x] Button labels checked by the owner at the minimum window width; `miku2018.jpg` downloaded again into `Assets/StudioRaming_Onairdeck/test_sell/` (2026-10-08).
- [x] Verify ZIP extraction and request cancellation/failure cache cleanup with generated fixtures in the real Unity 2021.3.18f1 Editor.
- [x] Import two generated `.unitypackage` files in Unity 2021.3.18f1, including a script that causes a domain reload; verify the second import completes and cached packages are removed.
- [x] Sign-in edge cases verified headlessly against the real listener and PKCE code: Deny, state mismatch, missing code, timeout, Cancel, favicon requests and port release (24-check suite, also in CI).
- [x] Editor runner in Unity 2021.3.18f1 (2026-10-08): **13/13 passed**, including expired/malformed/valid saved sign-ins, rejected token (401) and sign-out on a throwaway key, with the real sign-in confirmed unchanged.
- [x] **Deny** clicked by the owner on the real consent page; the window showed the expected message (2026-10-08).
- [ ] Package install (0.5.0) in a real Unity 2021.3 and 2022.3 editor: fresh install from the Manager alone, pair update, mismatch repair, interrupted install rolled back on the next load, legacy `Assets/` core refused, domain reload during the swap, `Client.Resolve()` timing, the Packages section layout.
- [ ] Feedback (0.6.0) in a real editor after `plugin-feedback` is deployed: send each kind with and without Console errors, check the row in the admin bug-report tab, the 429 message after 5 sends, and the section at the minimum window size.
- [ ] Import `Atelier-Manager-<version>.unitypackage` into a fresh project and confirm Unity accepts the `Packages/` pathnames.
- [ ] Install the package **from the git URL** in a fresh Unity 2021.3.18f1 project (every test so far used "Add package from disk").
- [ ] Later: verify 2022.3 and Unity 6 before raising the supported range.
- [x] Repository made public (2026-10-08).
- [ ] Choose and push the release tag.

Once a release is tagged, install a fixed version with `https://github.com/StudioRaming/Onairdeck-Unity-Manager.git#<release-tag>` rather than following a moving branch.

Repeatable download checks: [Tools~/Verification/README.md](Tools~/Verification/README.md). Filesystem tests compile the production extraction/copy code; the Editor runner exercises native requests and package imports using unique local fixtures without changing purchases.
