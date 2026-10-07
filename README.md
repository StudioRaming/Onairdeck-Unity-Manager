# OnAirDeck Unity Manager

A Unity Editor tool for OnAirDeck buyers:

- sign in and sign out through the browser,
- see your OnAirDeck purchase history,
- download purchased assets straight into the open project.

> Status: 0.2.1 development version. Sign-in, purchase listing and an image download have been tested in Unity 2021.3.18f1. The release checks below remain open before the first public release.

## Requirements

Targets Unity 2019.4 through Unity 6. Interactive testing currently covers 2021.3.18f1; the other versions still need verification.

## Install

In Unity, open **Window ▸ Package Manager ▸ + ▸ Add package from git URL…** and enter:

```
https://github.com/StudioRaming/Onairdeck-Unity-Manager.git
```

The repository is private during development, so this only works for accounts with access until the first public release.

## Use

1. Open **Window ▸ OnAirDeck Unity Manager** and click **Sign in with browser**. Approve the request on onairdeck.com. The sign-in is saved per computer, so it applies to every Unity project, and lasts 30 days unless you sign out.
2. Your purchases are listed with the files the seller made available for Unity. Items the seller has not enabled for Unity say "Not available in Unity".
3. Click **Download**. The first download of an item makes the purchase non-refundable (the same rule as the website), so Unity asks you to confirm.
   - `.unitypackage` files open Unity's normal import dialog and use the asset paths stored in the package.
   - `.zip` files are extracted, and other files are copied, into `Assets/StudioRaming_Onairdeck/<Product>/`.
4. **Update available** appears when the seller replaced a file after your last download.

Downloads made before 0.2.1 stay in their existing folders. Download again to place loose files or ZIP contents in the new folder. The manager does not move existing assets or their `.meta` files.

Sellers with active OnAirDeck Plus enable **Unity download** for each uploaded file in the Atelier asset editor. Buyers do not need Plus. Only files enabled for Unity and included in the purchased option are downloadable; external links are not downloaded by the manager.

## 한국어

### 설치

Unity에서 **Window ▸ Package Manager ▸ + ▸ Add package from git URL…**을 선택하고 아래 주소를 입력하세요.

```
https://github.com/StudioRaming/Onairdeck-Unity-Manager.git
```

개발 중에는 비공개 저장소에 접근할 수 있는 계정만 설치할 수 있습니다. 로컬 테스트는 **Add package from disk…**에서 이 저장소의 `package.json`을 선택하세요.

### 사용

1. **Window ▸ OnAirDeck Unity Manager ▸ Sign in with browser**를 누르고 onairdeck.com에서 로그인을 승인하세요. 로그인은 컴퓨터의 모든 Unity 프로젝트에서 공유되며, 로그아웃하지 않으면 30일 동안 유지됩니다.
2. 구매 목록에서 **Download** 또는 **Download again**을 누르세요. 최초 다운로드 시 환불이 불가능해진다는 확인 창이 표시됩니다.
3. 이미지 등 일반 파일과 ZIP 압축 해제 결과는 `Assets/StudioRaming_Onairdeck/<상품명>/`에 저장됩니다. `.unitypackage`는 Unity 가져오기 창을 열며 패키지에 저장된 경로를 사용합니다.
4. 판매자가 파일을 변경하면 **Update available**이 표시됩니다. **Refresh**로 목록을 갱신하세요.

판매자는 활성 Plus 멤버십이 있어야 Atelier 상품 편집기의 파일별 **Unity download**를 켤 수 있습니다. 구매자에게는 Plus가 필요하지 않습니다. 구매 옵션에 포함되지 않은 파일과 외부 링크는 Unity에서 다운로드할 수 없습니다. 이전 버전으로 받은 파일은 기존 폴더에 남습니다.

## 日本語

### インストール

Unityで **Window ▸ Package Manager ▸ + ▸ Add package from git URL…** を選び、次のURLを入力してください。

```
https://github.com/StudioRaming/Onairdeck-Unity-Manager.git
```

開発中は非公開リポジトリにアクセスできるアカウントのみインストールできます。ローカルテストでは **Add package from disk…** から、このリポジトリの `package.json` を選択してください。

### 使い方

1. **Window ▸ OnAirDeck Unity Manager ▸ Sign in with browser** を押し、onairdeck.comでサインインを承認してください。サインインは同じコンピューターの全Unityプロジェクトで共有され、サインアウトしなければ30日間有効です。
2. 購入一覧の **Download** または **Download again** を押してください。初回ダウンロード時は、購入が返金対象外になることを確認するダイアログが表示されます。
3. 画像などの通常ファイルとZIPの展開結果は `Assets/StudioRaming_Onairdeck/<商品名>/` に保存されます。`.unitypackage` はUnityのインポート画面を開き、パッケージに保存されたパスを使用します。
4. 出品者がファイルを変更すると **Update available** が表示されます。**Refresh** で一覧を更新してください。

出品者は有効なPlusメンバーシップで、Atelierの商品編集画面の各ファイルの **Unity download** を有効にできます。購入者にPlusは不要です。購入オプションに含まれないファイルや外部リンクはUnityからダウンロードできません。以前のバージョンで取得したファイルは元のフォルダーに残ります。

## Test a local copy (development)

In any Unity project: **Window ▸ Package Manager ▸ + ▸ Add package from disk…** and select this folder's `package.json`. Changes to the files are picked up when Unity recompiles.

To compile-check without opening Unity, run `dotnet build` in `Tools~/CompileCheck` (instructions inside the `.csproj`).

## Release verification

- [x] Browser sign-in and purchase listing tested by the owner in Unity 2021.3.18f1.
- [x] `test_sell` image downloaded by the owner with the `darudayu123@gmail.com` account.
- [ ] Check the 0.2.1 button labels at the minimum window width and download the image again to the new folder.
- [ ] Download a ZIP and check extraction, cancellation and cache cleanup.
- [ ] Import multiple `.unitypackage` files, including one with scripts, and check that the remaining imports survive script reloads.
- [ ] Test Deny, timeout, sign-out and expired-session handling in Unity.
- [ ] Verify git-URL installation and compilation in Unity 2019.4, 2022.3 and Unity 6.
- [ ] Review and merge the package PR, choose the release tag, and confirm repository visibility before public release.

Once a release is tagged, install a fixed version with `https://github.com/StudioRaming/Onairdeck-Unity-Manager.git#<release-tag>` rather than following a moving branch.
