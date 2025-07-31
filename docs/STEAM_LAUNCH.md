# GAMをGarry's Modとして表示する方法

## 問題
- Steamworks.NETは現在のプロセスのApp IDのみ使用可能
- gmpublisher (Rust)のような任意のApp ID指定は不可能

## 解決策

### 方法1: Steamショートカットを作成
1. Steamライブラリを開く
2. 「ゲームを追加」→「非Steamゲームを追加」
3. GAM.exeを選択
4. 追加されたGAMを右クリック→プロパティ
5. 起動オプションに追加: `+app_id 4000`

### 方法2: Garry's Modから起動
1. Garry's Modのプロパティを開く
2. 起動オプションに追加: `-applaunch 0 "C:\path\to\GAM.exe"`

### 方法3: Steam URLプロトコル
```
steam://run/4000//C:\path\to\GAM.exe
```

## 技術的制限
- Steamworks.NETは`SteamAPI.Init()`で現在のプロセスIDを使用
- `RestartAppIfNecessary`は実際にアプリを再起動する（UX悪化）
- steamworks-rsの`Client::init_app(appId)`相当の機能なし