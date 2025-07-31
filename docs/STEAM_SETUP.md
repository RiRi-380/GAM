# Steam Workshop API セットアップガイド

GAMでSteam Workshop画像を表示するための設定方法について説明します。

## 問題の原因

GAMがWorkshop APIを使用するには、Garry's Mod（App ID: 4000）として起動する必要があります。
そうでない場合、Workshop APIのコールバックが受信されず、画像が表示されません。

## 解決方法

### 方法1: launch_gam_with_steam.bat を使用（推奨）

1. `launch_gam_with_steam.bat` を実行
2. Enterキーを押して直接起動を選択
3. GAMが App ID 4000 で起動し、Workshop画像が表示されます

### 方法2: Steamに非Steamゲームとして追加

1. Steamクライアントを開く
2. ライブラリの左下「ゲームを追加」→「非Steamゲームを追加」
3. 「参照」をクリックして `C:\Program Files\GmodAddonManager\GmodAddonManager.UI.exe` を選択
4. 「選択したプログラムを追加」をクリック
5. ライブラリで「GmodAddonManager.UI」を右クリック→「プロパティ」
6. 「起動オプション」に `+app_id 4000` を入力
7. プロパティを閉じて、Steamから起動

### 方法3: 環境変数を設定して起動

コマンドプロンプトで：
```batch
set SteamAppId=4000
set SteamGameId=4000
"C:\Program Files\GmodAddonManager\GmodAddonManager.UI.exe"
```

## 確認方法

GAMが正しく起動されているか確認するには：

1. コンソール出力を確認
2. 以下のメッセージが表示されていることを確認：
   - `[SteamworksManager] ✓ Running with correct App ID 4000 - Workshop features should work!`
   - `[SteamworksManager] Current running App ID: 4000`

3. エラーメッセージが表示されている場合：
   - `[SteamworksManager] Running with App ID [別の番号] instead of 4000`
   - この場合、上記の方法で再起動してください

## トラブルシューティング

### 画像がまだ表示されない場合

1. **Steamクライアントが起動していることを確認**
   - Steamが起動していないとWorkshop APIは動作しません

2. **Garry's Modを所有していることを確認**
   - Steam アカウントでGarry's Modを所有している必要があります

3. **steam_api64.dll が存在することを確認**
   - `C:\Program Files\GmodAddonManager\steam_api64.dll` が存在するか確認

4. **ログファイルを確認**
   - `%AppData%\GmodAddonManager\logs\steamworks_manager.log` を確認
   - エラーメッセージがないか確認

5. **ファイアウォール/アンチウイルスの確認**
   - GAMがSteamと通信できるように許可されているか確認

### 開発者向け情報

GAMは内部でSteamworks.NETを使用してWorkshop APIにアクセスしています。
正しいApp IDで起動されていない場合、`SteamUGC.SendQueryUGCRequest` のコールバックが
受信されず、画像URLを取得できません。

gmpublisherと同じ実装方式を採用していますが、.NET環境での制限により
一部の挙動が異なる場合があります。

## 関連ファイル

- `launch_gam_with_steam.bat` - 簡単起動スクリプト
- `src/GmodAddonManager.Core/Services/SteamworksManager.cs` - Steam API実装
- `steam_appid.txt` - App ID設定ファイル（4000）