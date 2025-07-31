# Gmod Addon Manager (GAM)

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)
[![.NET](https://img.shields.io/badge/.NET-6.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/6.0)
[![Avalonia UI](https://img.shields.io/badge/Avalonia-11.0.7-purple)](https://avaloniaui.net/)

![GAM Screenshot](docs/images/screenshot.png)

Garry's Mod のアドオンを効率的に管理するための Windows アプリケーションです。

## ⚠️ 重要な注意事項

**Garry's Mod の実行中に本ソフトを実行しないでください。**
アドオンの管理操作中にゲームが実行されていると、予期しない動作やエラーが発生する可能性があります。

## 主な機能

### 🎯 アセットベース管理
- アドオンをグループ（アセット）に分けて整理
- アセット単位での一括有効/無効切り替え
- カスタムアセットの作成・削除・管理

### ⚡ 高速な有効/無効切り替え
- ジャンクション/ハードリンクを使用した瞬時の切り替え
- Steamの再ダウンロード問題を回避する独自実装
- GMODプロセス監視による安全な変更管理

### 🖼️ Workshop連携機能
- **高速画像表示**: Steamworks SDKを使用した高速な画像取得
- **アドオン情報表示**: タイトル、作者、説明、ファイルサイズなどを表示
- **コレクションインポート**: WorkshopコレクションのURLから一括でアドオンを追加
- **自動サブスクライブ**: 未サブスクライブのアドオンを自動でサブスクライブ
- **自動App ID設定**: 起動時に自動的にGarry's Mod（App ID 4000）として動作

### 🔄 便利な管理機能
- **Undo機能**: 最大50件の操作履歴から復元可能
- **バッチインポート**: 新規アドオンの一括追加
- **Steamワークショップ連携**: ワークショップアドオンの自動検出
- **プロセス監視**: Garry's Mod実行中は変更を保留し、終了後に自動適用
- **バッチ処理最適化**: 大量のアドオン操作でも高速処理

### 🌐 多言語対応
- 日本語・英語のUI切り替え
- 設定から簡単に言語変更可能

### 🔄 自動アップデート
- アプリ起動時に新バージョンを自動チェック
- ワンクリックでアップデート可能

## システム要件

### 必須要件
- **OS**: Windows 10/11（64ビット）
- **ランタイム**: [.NET 6.0 Runtime](https://dotnet.microsoft.com/download/dotnet/6.0)
- **Visual C++**: [Visual C++ 再頒布可能パッケージ](https://aka.ms/vs/17/release/vc_redist.x64.exe)（インストーラー版では自動インストール）
- **権限**: 管理者権限（ジャンクション作成のため）
- **その他**: 
  - Steam がインストールされていること
  - Garry's Mod を所有していること（Workshop機能使用時）

### 推奨環境
- 十分なディスク空き容量
- SSD（高速な読み書きのため）

## インストール

### インストーラー版（推奨）
1. [Releases](https://github.com/RiRi-380/GAM/releases) から最新の `GAM-Setup-vX.X.X.exe` をダウンロード
2. インストーラーを実行
3. 指示に従ってインストール

### ポータブル版
1. [Releases](https://github.com/RiRi-380/GAM/releases) から最新の `GAM-Portable-vX.X.X.zip` をダウンロード
2. 任意のフォルダに解凍
3. `GmodAddonManager.UI.exe` を管理者として実行

## 使い方

### 初回起動
1. アプリケーションを管理者として起動
2. 初期セットアップが自動的に開始（数分かかる場合があります）
3. 既存のアドオンが自動的に検出・整理されます
4. Workshop画像が自動的に表示されます（Steamworks SDK経由）

### 基本的な使い方
1. **アセットの作成**: 右上の「+」ボタンから新規アセットを作成
2. **アドオンの追加**: アセットを選択して「アドオンを追加」
   - 個別追加: Workshop URLを入力
   - コレクション追加: コレクションURLを入力して一括インポート
3. **有効/無効の切り替え**: アドオンを選択して状態を変更
4. **バッチ操作**: 複数選択して一括で操作可能

### アセットの種類
- **カスタムアセット**: ユーザーが自由に作成・管理
- **Subscribe Asset**: Steamでサブスクライブしたアドオン
- **Junction Asset**: 無効化されたアドオンの保管場所

## トラブルシューティング

### よくある問題

#### 管理者権限エラー
- ジャンクション作成時に管理者権限が必要です
- エラーが表示された場合は、アプリケーションを管理者として再起動してください

#### アドオンが表示されない
1. Steamのワークショップフォルダを確認
2. 「更新」ボタンをクリック
3. それでも表示されない場合は、設定から「マネージャーをリセット」

#### Workshop機能が動作しない
- `launch_gam_with_steam.bat`を使用して起動してください
- または、GAMを非Steamゲームとして追加し、起動オプションに`+app_id 4000`を設定

#### エラーログの確認
- 設定→ログ設定→「ログフォルダを開く」
- エラーの詳細はログファイルに記録されています
- 設定→「コンソールを表示」でリアルタイムログを確認可能

### データの場所
- **設定ファイル**: `%APPDATA%\GmodAddonManager\`
- **管理データ**: `[Steamフォルダ]\steamapps\workshop\content\4000\.addon-manager\`
- **ログファイル**: `%APPDATA%\GmodAddonManager\logs\`
- **Steam App ID**: 起動時に`steam_appid.txt`（4000）が自動生成

## 開発者向け情報

### 技術スタック
- **Framework**: .NET 6.0
- **UI**: Avalonia UI 11.0.7
- **Reactive**: ReactiveUI
- **JSON**: Newtonsoft.Json
- **Steam連携**: Steamworks.NET 15.0.1
- **バージョン**: 1.0.0

### プロジェクト構成
```
GAM/
├── src/
│   ├── GmodAddonManager.Core/      # ビジネスロジック
│   └── GmodAddonManager.UI/        # UIレイヤー
├── installer/                      # Inno Setupインストーラー
├── docs/                           # ドキュメント
└── .github/workflows/              # GitHub Actions CI/CD
```

### ビルド方法
```powershell
# 依存関係の復元
dotnet restore

# ビルド
dotnet build -c Release

# リリースビルド（インストーラー付き）
.\build-release.ps1
```

### 必要なファイル
- `steam_api64.dll` - 自動的にビルドに含まれます
- `steam_appid.txt` - 起動時に自動生成されます（内容: 4000）
- **重要**: Steamworks機能を使用する場合は、`launch_gam_with_steam.bat`を使用して起動するか、非Steamゲームとして追加してください

## ライセンス

GNU General Public License v3.0 - 詳細は [LICENSE](LICENSE) ファイルを参照してください。

## 貢献

Issue の報告や Pull Request を歓迎します！

## 作者

- [RiRi-380](https://github.com/RiRi-380)

## v1.0.0 の主な改善点

- **Steamworks.NET統合**: 高速なWorkshopアクセスとサブスクライブ機能
- **コレクションインポート**: Workshopコレクションからの一括インポート
- **パフォーマンス最適化**: 大量アドオンのバッチ処理を最適化
- **UI改善**: ダイアログフローとレスポンスの改善
- **安定性向上**: エラーハンドリングとプロセス監視の強化

## 謝辞

このプロジェクトは以下のオープンソースプロジェクトを使用しています：
- [Avalonia UI](https://avaloniaui.net/)
- [ReactiveUI](https://reactiveui.net/)
- [Newtonsoft.Json](https://www.newtonsoft.com/json)
- [Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET)

### Special Thanks
- [gmpublisher](https://github.com/WilliamVenner/gmpublisher) - Workshop API呼び出しの実装を参考にさせていただきました

---

**注意**: このソフトウェアは Garry's Mod や Valve Corporation と提携していません。