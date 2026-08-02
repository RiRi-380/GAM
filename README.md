# GAM (Gmod Addon Manager)

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![Avalonia UI](https://img.shields.io/badge/Avalonia-11.3.14-purple)](https://avaloniaui.net/)

![GAM Screenshot](docs/images/screenshot.png)

GAM は Garry's Mod のアドオンを「アセット（プロファイル）」単位で管理する Windows アプリです。
ソフト無効化（`addonnomount.txt`）を中心に、アドオンの整理・切り替えを素早く行えます。

## 特徴

- 複数のアセットを同時に重ねて、遊び方ごとのアドオン構成を整理
- アセット単位の有効・無効・除外（除外はすべての構成に共通）
- Subscribe AssetでSteamの現在の購読全体をON/OFF/すべて除外
- 現在の実状態と、GAMが次に適用する希望状態・理由を分けて表示
- 最近購読、名前、容量、Workshop更新日時による並び替え
- よく使うアセットをお気に入りとして上部へ固定
- サムネイル、タグ、サイズ、メモの表示
- セッション内の変更履歴を最大50件Undo（取り消し）
- アセット画像の設定とトリミング
- アセットのメンバー構成だけをバージョン保存・復元
- フィルタ/検索で目的のアドオンを素早く探せる

## 状態の仕組み

- 現行リリースは **ソフト無効化のみ** です。
- Garry's Mod の `garrysmod/cfg/addonnomount.txt` を更新して無効化します。
- Custom Assetはそれぞれ有効・無効・除外のいずれか一つです。
- Subscribe AssetのOFFは中立で、有効なCustom Assetは引き続きAddonを有効にできます。「すべて除外」は全購読Addonを強制的にOFFにし、Custom Assetでは上書きできません。
- 無効なAssetは最終状態に影響せず、除外Assetに含まれるAddonは常にOFFになります。
- 複数の有効Assetは同時に利用できます。どれか一つだけを選ぶプリセット方式ではありません。
- GMod実行中の変更は希望状態だけを保存し、GMod終了後に最新構成を一度だけ適用します。
- GMod側で手動変更した状態は警告なしで読み取り、次にGAMで明示的な操作をしたときにGAMの最新構成を再適用します。
- ジャンクション/ハードリンク、ローカルAddon管理は正式版の操作対象ではありません。

詳しい状態計算と移行規則は `docs/STATE_MODEL.md` を参照してください。

## 使い方（クイックスタート）

1. 起動してアセットを作成
2. 購読済みAddon一覧から必要なAddonを追加
3. 使いたいアセットを有効にし、不要なグループは除外にする
4. 必要に応じてお気に入り、メモ、画像、バージョンを設定

Steamの購読・購読解除はSteamクライアント側で行います。GAMは自動購読、Workshop Collection共有、`.gam` / `.gamdisable`の製品UIを提供しません。

## 動作環境
- Windows 10/11 (64-bit)
- Setup/Portable配布物はself-containedのため、.NET Runtimeの別途導入は不要
- 起動前の一時展開を避けるため、配布物は複数ファイル構成です。Portable版はZIPをフォルダーへ展開し、同梱ファイルを保ったまま `GmodAddonManager.UI.exe` を起動してください
- Setup版はVisual C++ 2015-2022 x64を必要に応じて導入（Portable版では別途必要な場合があります）

## インストール
- [GitHub Releases](https://github.com/RiRi-380/GAM/releases) からSetup版またはPortable版を入手できます
- 例: `GAM-Setup-X.Y.Z.exe` / `GAM-Portable-X.Y.Z.zip`

## データ保存場所

- GAM構成: `%APPDATA%\GmodAddonManager\config.json`
- UI設定: `%APPDATA%\GmodAddonManager\settings.json`
- ログ: `%APPDATA%\GmodAddonManager\logs\`
- 更新チェック記録: `%APPDATA%\GmodAddonManager\last_update_check.txt`

## 更新チェック
- GitHub Releases を参照して更新を検出します（デフォルトは `RiRi-380/GAM`）。
- 更新元リポジトリは環境変数で変更できます。
- 詳細は `docs/UPDATE_CHECK.md` を参照してください。

## 注意事項

- アドオンの購読管理は Steam クライアントで行います。
- Steamのダウンロードが完了していないAddonは、完了後にGAMを更新してください。
- Steam管理下に残る空フォルダーはGAMの一覧には表示せず、GAMから物理削除しません。
- Steamworks DLL (`steam_api64.dll`, `steam_appid.txt`) は同梱しません。

## 開発
開発・ソースからのビルドには、`global.json`に対応する.NET 10 SDKが必要です。

```powershell
dotnet restore
dotnet build src/GmodAddonManager.UI/GmodAddonManager.UI.csproj -c Debug
dotnet test
dotnet run --project src/GmodAddonManager.UI/GmodAddonManager.UI.csproj
```

## ビルド（リリース用）
```powershell
./build-release.ps1 -Version vX.Y.Z
```

## 構成
```
GAM/
  src/GmodAddonManager.Core/   # Core logic
  src/GmodAddonManager.UI/     # Avalonia UI
  installer/                   # Inno Setup
  scripts/                     # Release helpers
  docs/                        # Docs
```

## ライセンス
GNU General Public License v3.0 - see `LICENSE`

## コントリビュート
Issue / Pull Request を歓迎します。
