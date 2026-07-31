# 自動アップデート設定・運用ガイド

このドキュメントは、GAM の GitHub Releases ベース自動アップデートを確実に動作させるための設定手順をまとめたものです。

## 1. 更新元リポジトリの設定

製品版の既定値は `RiRi-380/GAM` です。開発・検証で変更する場合のみ、環境変数を使います。

- `GAM_UPDATE_REPO` に `owner/name` を設定

PowerShell（現在のセッションのみ）:
```powershell
$env:GAM_UPDATE_REPO = "owner/repo"
```

## 2. 非公開リポジトリの場合（必須）

GitHub API にアクセスするためのトークンが必要です。

- 環境変数 `GAM_GITHUB_TOKEN` を設定

PowerShell（現在のセッションのみ）:
```powershell
$env:GAM_GITHUB_TOKEN = "<your-token>"
```

**注意**: トークンはログやスクリーンショットに残さないでください。

## 3. GitHub Enterprise / 独自API URL の場合

GitHub Enterprise などで API URL が異なる場合は、
`GAM_UPDATE_API_URL` を指定できます。

例（releases エンドポイント）:
```powershell
$env:GAM_UPDATE_API_URL = "https://github.example.com/api/v3/repos/OWNER/REPO/releases"
```

※ `.../releases` か `.../releases/latest` のどちらでも動作します。

## 4. プレリリースを対象に含めたい場合

`GAM_UPDATE_INCLUDE_PRERELEASE=1` を設定すると、プレリリースも検出対象になります。

```powershell
$env:GAM_UPDATE_INCLUDE_PRERELEASE = "1"
```

## 5. リリース資産の命名ルール（重要）

アップデート検出には **インストーラの .exe** が必要です。
次の条件に合うファイルがリリースに含まれている必要があります。

- 拡張子が `.exe`
- ファイル名に `setup` または `installer` が含まれる
  - 例: `GAM-Setup-v1.2.3.exe`
- GitHub Releases APIのassetに有効な `sha256:` digestがある

**ZIPのみの場合はアップデート検出に失敗**します。

## 6. 動作確認手順

**自動チェック（起動後）**
- 起動5秒後に自動チェックが走ります
- 24時間以内にチェック済みの場合はスキップされます
  - 判定ファイル: `%APPDATA%\GmodAddonManager\last_update_check.txt`

## 7. よくある失敗原因

- リポジトリ名が間違っている（404）
- 非公開リポジトリでトークン未設定
- リリースにインストーラ `.exe` が無い
- リリースタグが `vX.Y.Z` 形式になっていない

---

### ユーザー向けメモ

- 更新元の上書きとトークンはファイルには保存されません（環境変数のみ）。
- ダウンロード後はGitHub Releases APIのSHA-256 digestと照合し、一致しないインストーラは実行しません。
