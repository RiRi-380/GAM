# GAM Release Checklist

このチェックリストは、非公開検証Releaseと一般公開Releaseの両方に使用します。開始時と完了時にGitHubリポジトリのvisibilityを確認し、予定外に変更しません。

## 事前確認

- [ ] リポジトリ内に成果物が残っていない（例: publish/, publish-portable/, dist/, GAM-Portable-*.zip, *.log）。
- [ ] Steamworks 関連ファイルが含まれていない（steam_api64.dll / steam_appid.txt）。
- [ ] README.md が UTF-8 として正しく表示される。
- [ ] LICENSE と NOTICE がルートに存在する。
- [ ] THIRD-PARTY-NOTICES.txt と Microsoft .NET のlicense/noticeがルートに存在する。
- [ ] docs/dependencies.md が最新（NuGet 依存が全て記載）。
- [ ] 更新チェックの既定リポジトリが正しい（UpdateService の DefaultGithubRepo）。
- [ ] Release対象ブランチが `main` である。
- [ ] `git status --porcelain` が空で、対象commitが `origin/main` と一致する。
- [ ] GitHubリポジトリのvisibility（private/public）が今回のRelease方針と一致する。

## ビルド前提

- [ ] Inno Setup 6 がインストール済み（インストーラを作る場合）。
- [ ] `global.json` に対応する.NET 10 SDKが使用できる。
- [ ] VC++ Redistributableをダウンロード・同梱・起動する処理がない。

## バージョンとメタデータ

- [ ] Directory.Build.props の Version / FileVersion / AssemblyVersion がRelease versionと一致する。
- [ ] `docs/releases/vX.Y.Z.md` にschema移行・downgrade注意・主要変更を記載する。
- [ ] タグ名はexact stable SemVerの `vX.Y.Z` 形式で、annotated tagとして作成する。

## 検証とビルド（ローカル）

- [ ] lock file固定の依存復元とNuGet脆弱性監査: `dotnet restore GmodAddonManager.sln --locked-mode`
- [ ] Core test: `dotnet test tests/GmodAddonManager.Core.Tests/GmodAddonManager.Core.Tests.csproj -c Release --no-restore`
- [ ] UI/contract test: `dotnet test tests/GmodAddonManager.UI.Tests/GmodAddonManager.UI.Tests.csproj -c Release --no-restore`
- [ ] Releaseビルド: `dotnet build src/GmodAddonManager.UI/GmodAddonManager.UI.csproj -c Release --no-restore -p:WarningsAsErrors=true`
- [ ] 信頼できない入力のresource limit test（`.gam` / `addon.json` / GMA）が成功する。
- [ ] リリース生成: `./build-release.ps1 -Version vX.Y.Z`
  - [ ] 非対話で実行する場合は `-RunMode skip` を使用（対話実行なら最後のpromptで `n` を選択）。

## 配布物の内容確認

- [ ] Portable ZIP とインストーラに LICENSE と NOTICE が含まれている。
- [ ] Portable ZIP とインストーラに THIRD-PARTY-NOTICES.txt、MICROSOFT-DOTNET-LIBRARY-LICENSE.txt、MICROSOFT-DOTNET-THIRD-PARTY-NOTICES.txt、DISTRIBUTION-LICENSES.txt が含まれている。
- [ ] `scripts/verify-release-notices.ps1` がproduction UIのexact package inventoryとpublish内容を検証して成功する。
- [ ] publish/ZIP/インストーラはself-containedの複数ファイル構成で、`GmodAddonManager.UI.exe` と同階層のDLLを欠落させていない。
- [ ] Release publishで `PublishSingleFile=false` を指定し、`IncludeNativeLibrariesForSelfExtract` を使用していない（起動前の `%TEMP%\.net` 展開を避ける）。
- [ ] インストーラにGAM GPLとMicrosoft .NET Library Licenseを含むDISTRIBUTION-LICENSES画面が表示される。
- [ ] ZIP/インストーラに steam_api64.dll / steam_appid.txt が含まれていない。
- [ ] Portable ZIPだけに `.gam-portable.json` があり、installer stagingには含まれない。
- [ ] Portable ZIPとinstaller stagingの両方に `GAM-ReleaseFiles.txt` がある。
- [ ] ZIP/インストーラにVC++ Redistributableが含まれていない。
- [ ] 標準ユーザーで `GmodAddonManager.UI.exe` が起動し、GAM自体がUAC昇格を要求しない。
- [ ] Setupの「GAMを起動する」で1回だけ起動し、アンインストーラーがWindowsの「インストールされているアプリ」から実行できる。
- [ ] アンインストール後も `%APPDATA%\GmodAddonManager` のユーザー構成が保持されることを確認し、完全削除が必要な検証では別途明示的に退避・削除する。

## GitHub Actions / Release

- [ ] release scriptがcleanな `origin/main` だけを受け付け、mainを自動commit/pushしない。
- [ ] `.github/workflows/release.yml` がexact tag、annotated tag、`origin/main`、version、release notesをfail-closedで検証する。
- [ ] workflowのActions参照がfull commit SHAで固定され、権限がjobに必要な最小範囲である。
- [ ] `.github/workflows/release.yml` が全license/noticeをpublishへコピーして検証している。
- [ ] Actions が成功している。
- [ ] Release assetsに `GAM-Portable-X.Y.Z.zip`、`GAM-Setup-X.Y.Z.exe`、`GAM-Setup.exe`、`SHA256SUMS.txt` が揃っている。
- [ ] Release assetのGitHub API `digest` と `SHA256SUMS.txt` が実ファイルのSHA-256と一致する。

## Release作成後の確認

- [ ] README のリンク（Releases / ドキュメント / 画像）が 404 にならない。
- [ ] 新規クローンで `dotnet build` が通る。
- [ ] Setup版の更新チェックがSetup assetを選び、Portable版がPortable ZIPだけを選ぶ。
- [ ] Portable更新は検証済みZIPをエクスプローラーで表示し、Setup版を別にインストールしない。
- [ ] 非公開Releaseでは読取tokenあり／なしの両方を確認し、tokenをログへ出していない。
- [ ] 同じversionを再発行した場合、自動更新では検出されないため、既存testerへ手動再取得を案内する。Setup版は旧版を先にアンインストールしてから再導入し、Portable版は終了後にフォルダーを置き換える。
- [ ] GitHubリポジトリのvisibilityがRelease前と同じである。

## トラブルが出た場合

- [ ] `build-release.ps1` と `installer/setup.iss` の設定を確認。
