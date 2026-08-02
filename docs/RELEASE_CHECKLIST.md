# OSS Release Checklist

このチェックリストは、OSS 公開とバイナリ配布の最終確認用です。

## 事前確認
- [ ] リポジトリ内に成果物が残っていない（例: publish/, dist/, GAM-Portable-*.zip, *.log）。
- [ ] Steamworks 関連ファイルが含まれていない（steam_api64.dll / steam_appid.txt）。
- [ ] README.md が UTF-8 として正しく表示される。
- [ ] LICENSE がルートに存在する。
- [ ] THIRD-PARTY-NOTICES.txt と Microsoft .NET のlicense/noticeがルートに存在する。
- [ ] docs/dependencies.md が最新（NuGet 依存が全て記載）。
- [ ] 更新チェックの既定リポジトリが正しい（UpdateService の DefaultGithubRepo）。
- [ ] 公開対象ブランチが想定通り（例: main）。

## ビルド前提
- [ ] Inno Setup 6 がインストール済み（インストーラを作る場合）。
- [ ] `redist/VC_redist.x64.exe` が用意されている（インストーラ同梱用）。

## バージョンとメタデータ
- [ ] Directory.Build.props の Version / FileVersion / AssemblyVersion を更新（必要なら）。
- [ ] CHANGELOG/Release notes を用意（GitHub の自動生成 or 手動）。
- [ ] `docs/releases/vX.Y.Z.md` にschema移行・downgrade注意・主要変更を記載する。
- [ ] タグ名は vX.Y.Z 形式。

## ビルド（ローカル）
- [ ] 依存復元: `dotnet restore`
- [ ] Debug ビルド: `dotnet build src/GmodAddonManager.UI/GmodAddonManager.UI.csproj -c Debug`
- [ ] Release ビルド: `dotnet build src/GmodAddonManager.UI/GmodAddonManager.UI.csproj -c Release`
- [ ] リリース生成: `./build-release.ps1 -Version vX.Y.Z`
  - [ ] 非対話で実行する場合は `-RunMode skip` を使用（対話実行なら最後のプロンプトで `n` を選択）。

## 配布物の内容確認
- [ ] ZIP に LICENSE が含まれている。
- [ ] ZIP に THIRD-PARTY-NOTICES.txt、MICROSOFT-DOTNET-LIBRARY-LICENSE.txt、MICROSOFT-DOTNET-THIRD-PARTY-NOTICES.txt、DISTRIBUTION-LICENSES.txt が含まれている。
- [ ] `scripts/verify-release-notices.ps1` がproduction UIのexact package inventoryとpublish内容を検証して成功する。
- [ ] publish/ZIP/インストーラはself-containedの複数ファイル構成で、`GmodAddonManager.UI.exe` と同階層のDLLを欠落させていない。
- [ ] Release publishで `PublishSingleFile=false` を指定し、`IncludeNativeLibrariesForSelfExtract` を使用していない（起動前の `%TEMP%\.net` 展開を避ける）。
- [ ] インストーラにGAM GPLとMicrosoft .NET Library Licenseを含むDISTRIBUTION-LICENSES画面が表示される。
- [ ] ZIP/インストーラに steam_api64.dll / steam_appid.txt が含まれていない。
- [ ] GmodAddonManager.UI.exe が起動する（必要なら管理者実行）。

## GitHub Actions / Release
- [ ] `.github/workflows/release.yml` が全license/noticeをpublishへコピーして検証している。
- [ ] Actions が成功している。
- [ ] Release の assets に ZIP と EXE が揃っている。

## 公開後の確認
- [ ] README のリンク（Releases / ドキュメント / 画像）が 404 にならない。
- [ ] 新規クローンで `dotnet build` が通る。
- [ ] アプリの更新チェックが GitHub Releases を参照できる。

## トラブルが出た場合
- [ ] `build-release.ps1` と `installer/setup.iss` の設定を確認。
- [ ] `docs/STEAMWORKS_INTEGRATION.md` に反する配布物がないか再確認。
