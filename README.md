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
- 複数のAssetやAsset Groupを設定上限まで階層化し、サブツリーをまとめて状態変更
- カードの`GMod: 有効/無効`で現在の実状態を示し、通常Assetと食い違う場合は別の有効化元や反映待ちを明示
- 購読日時、名前、容量、Workshop更新日時による並び替え
- 固定System Asset・お気に入り・通常の3帯を保った手動並び替え（AssetとAsset Groupは同じ帯に混在）
- `GMod Disabled Addons`をSubscribe Assetの操作から折りたたみ、表示設定を保持
- サムネイル、タグ、サイズ、メモの表示
- セッション内の変更履歴を最大50件Undo（取り消し）
- アセット画像の設定とトリミング
- 試験設定をONにした場合だけ、通常の固定Assetのメンバー構成を履歴へ保存・復元
- TypeまたはTagを1つ指定し、購読内容に自動追従するSmart Asset
- 単一Asset用`.gam` v3と、複数Asset / 階層Asset Groupを1ファイルで渡せる`.gam` v4（旧v1～v3も読込可能）
- フィルタ/検索で目的のアドオンを素早く探せる

## 状態の仕組み

- 現行リリースは **ソフト無効化のみ** です。
- Garry's Mod の `garrysmod/cfg/addonnomount.txt` を更新して無効化します。
- Custom Assetはそれぞれ有効・無効・除外のいずれか一つです。
- Asset Groupは整理用の階層コンテナです。最大入れ子深度は1～10で設定でき、初期値1ではルートGroup内に子Groupを1段作れます。Groupの状態変更はすべての子孫Assetへ実際に反映され、子孫の状態が異なる場合だけGroupをMixed表示します。
- Group内で新規作成したAssetは、子が一様ならその状態を、空またはMixedならGroupが最後に指定された状態を引き継ぎます。
- Group削除時は、直下のAsset / 子Groupを親コンテナへ戻すか、サブツリーごと削除するかを選べます。どちらも一つのUndo操作です。
- Subscribe AssetのOFFは中立で、有効なCustom Assetは引き続きAddonを有効にできます。「すべて除外」は全購読Addonを強制的にOFFにし、Custom Assetでは上書きできません。
- `GMod Disabled Addons`は初期OFFです。GMod側で無効化されたAddonを記録しますが、ユーザーが同Assetを有効または除外へ変更するまでは最終状態に影響しません。
- 無効なAssetは最終状態に影響せず、除外Assetに含まれるAddonは常にOFFになります。
- 複数の有効Assetは同時に利用できます。どれか一つだけを選ぶプリセット方式ではありません。
- GMod実行中の変更は希望状態だけを保存し、GMod終了後に最新構成を一度だけ適用します。
- GMod側で手動変更した状態は警告なしで読み取り、次にGAMで明示的な操作をしたときにGAMの最新構成を再適用します。
- ジャンクション/ハードリンクによる管理は正式版の操作対象ではありません。
- v1のHard modeを使用していた環境では、v2の通常読込より前に、`.addon-manager` 配下へ移動されていたAddon本体をSteam/GModの標準位置へ自動復旧します。v1が作成したと確認できるジャンクション／ハードリンクだけを取り除き、OFFだったAddonは `addonnomount.txt` へ引き継ぎます。GModが実行中、配置が競合、状態ファイルが不正など、安全に復旧できない場合は上書きせず起動を中止します。復旧はjournalへ記録され、中断後も次回起動で検証しながら再開します。空の管理フォルダーと0 byteのcache残骸はAddonとして復元しません。
- ローカルAddon機能は初期OFFです。試験設定をONにした場合だけ読み取り専用で一覧表示でき、GAMは実体を移動・無効化・削除せず、Assetのメンバーにも加えません。

詳しい状態計算と移行規則は [`docs/STATE_MODEL.md`](docs/STATE_MODEL.md) を参照してください。

## 使い方（クイックスタート）

1. 起動して通常Asset、Type / Tag Smart Asset、またはAsset Groupを作成
2. 購読済みAddon一覧から必要なAddonを追加
3. 必要ならAssetをGroupへまとめ、AssetまたはGroupを有効・無効・除外にする
4. 必要に応じてお気に入り、手動順序、メモ、画像、試験設定の履歴を設定

Steamの購読・購読解除はSteamクライアント側で行います。`.gam`はAsset名・状態・Workshop IDまたはSmartルールを受け渡しますが、Steamの購読状態は変更しません。単一Assetはv3、Groupまたは複数選択は階層・混在順序を保つv4 Bundleとして、どちらも同じ`.gam`拡張子で保存します。メモと画像の同梱は別々の全体スイッチで、どちらも初期OFFです。画像を含める場合は各画像を検証・圧縮したPNGへ再処理します。Workshop Collection共有と`.gamdisable`の製品UIは提供しません。AIや外部ツールからファイルを生成する場合は、[`docs/GAM_FILE_FORMAT.md`](docs/GAM_FILE_FORMAT.md) の厳密な形式仕様を参照してください。

## 動作環境
- Windows 10/11 (64-bit)
- Setup/Portable配布物はself-containedのため、.NET Runtimeの別途導入は不要
- 起動前の一時展開を避けるため、配布物は複数ファイル構成です。Portable版はZIPをフォルダーへ展開し、同梱ファイルを保ったまま `GmodAddonManager.UI.exe` を起動してください

## インストール
- [GitHub Releases](https://github.com/RiRi-380/GAM/releases) からSetup版またはPortable版を入手できます
- 例: `GAM-Setup-X.Y.Z.exe` / `GAM-Portable-X.Y.Z.zip`
- **Setup版v1から移行する場合:** アンインストールは不要です。GAM内の更新通知から進めても、v2のSetupを直接起動しても、同じGAMとして既存のインストール先へ上書き更新します。旧全ユーザー版では必要なUACが表示され、旧ユーザー単位版では不要な昇格を行いません。`%APPDATA%\GmodAddonManager`、GMod設定、Workshopデータ、Steam購読状態はSetupから削除・移動しません。未登録のPortable／コピーは自動検出できないため、v2移行後に旧v1の実行ファイルを起動しないでください
- Setup版はWindowsの「インストールされているアプリ」からアンインストールできます。アンインストールしても、再導入時に引き継げるよう `%APPDATA%\GmodAddonManager` のAsset構成・設定・ログは保持します
- **旧private v2.x（2.0.0～2.2.0）を導入済みのテスター:** 今回はそれらを `v2.0.0` へ統合してReleaseを再作成するため、同一versionまたはdowngradeとなり自動更新では差替えを検出できません。Setup版は新しいReleaseからSetupを手動取得し、旧版をアンインストールせず、そのまま実行して上書きしてください。Portable版はGAMを終了して新ZIPを展開し、旧Portableフォルダーを手動で置き換えてください。どちらも `%APPDATA%\GmodAddonManager` の構成は保持されます

## データ保存場所

不具合を問い合わせるときは、**設定 → ログ → 診断情報を作成…** から内容を確認し、テキストに保存できます。バージョン・OS、アセットや反映保留の件数、状態ファイルの読み取り結果、最近のエラー分類が含まれます。アセット名・メモ・アドオンID・個人のパス・認証情報・ログ本文は含めず、自動送信もしません。購読情報は前回取得時のもので、ゲーム内での読み込み成功を保証する診断ではありません。

- GAM構成: `%APPDATA%\GmodAddonManager\config.json`
- UI設定: `%APPDATA%\GmodAddonManager\settings.json`
- サムネイルキャッシュ: `%APPDATA%\GmodAddonManager\icons\`
- ログ: `%APPDATA%\GmodAddonManager\logs\`
- 更新チェック記録: `%APPDATA%\GmodAddonManager\last_update_check.txt`
- v1 Hard-layout復旧journal: `%APPDATA%\GmodAddonManager\legacy-hard-layout-recovery.json`

## 更新チェック
- GitHub Releases を参照して更新を検出します（デフォルトは `RiRi-380/GAM`）。
- Setup版は検証済みのSetupを起動して更新します。Portable版はPortable ZIPだけを取得し、エクスプローラーで選択表示します。Portable版は自動で別のSetup版をインストールしないため、GAMを終了してZIPを展開し、現在のPortableフォルダーを手動で置き換えてください。
- 公開Releaseはそのまま参照できます。非公開Releaseには読取権限のあるGitHub tokenが必要です。更新元リポジトリとtokenは環境変数で変更できます。
- 詳細は [`docs/UPDATE_CHECK.md`](docs/UPDATE_CHECK.md) を参照してください。

## 注意事項

- アドオンの購読管理は Steam クライアントで行います。
- Steamのダウンロードが完了していないAddonは、完了後にGAMを更新してください。
- 「購読日時」はSteam上の正確な購読時刻ではなく、GAMが新規購読として初めて確認した時刻です。初回起動時に既に購読済みだったAddonは日時不明になります。
- Steam管理下に残る空フォルダーはGAMの一覧には表示せず、GAMから物理削除しません。
- Steamworks DLL (`steam_api64.dll`, `steam_appid.txt`) は同梱しません。

### 外部入力の技術的安全上限

共有ファイルやAddonメタデータによる過大なメモリ・CPU使用を防ぐため、通常利用より十分大きい技術的上限を設けています。これは「32 MiBまで」といった製品上の共有容量制限ではありません。

- Single Asset `.gam`: ファイル64 MiB、Workshop ID 5,000,000件まで
- Bundle `.gam`: archive全体640 MiB、ZIP central directory 8 MiB、展開後manifest 64 MiB、Asset/Group各100,000件、topology参照1,000,000件、全membership合計5,000,000 IDまで
- Bundle画像: 4,096枚、1枚あたり入力4 MiB、入力画像合計512 MiB、正規化後画像合計512 MiBまで。各画像には寸法・pixel数・デコードサイズの上限もあります
- `addon.json`: 1 MiB、JSON深度32、tag 1,024件、type/tag値は各512 UTF-16コード単位まで
- GMAメタデータ走査: entry 100,000件、entry path 4,096 bytes、全解析fallbackを通じたpath metadata合計16 MiB、header文字列4,096 bytes、内包 `addon.json` 1 MiBまで

上限を超える `.gam` はインポートを拒否し、上限を超える・不正なAddon分類メタデータは分類情報として採用しません。`.gam` の正確な制約は [`docs/GAM_FILE_FORMAT.md`](docs/GAM_FILE_FORMAT.md) を参照してください。

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
GNU General Public License v3.0 - see [LICENSE](LICENSE). 製品名と著作権表示は [NOTICE](NOTICE) を参照してください。

## コントリビュート
Issue / Pull Request を歓迎します。
