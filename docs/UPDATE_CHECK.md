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

## 5. 配布形態ごとのリリース資産（重要）

GAMは実行ファイルと同じフォルダーにある `.gam-portable.json` で公式Portable版を判定し、現在の配布形態と同じ種類の更新だけを選びます。

Setup版:

- `.exe`で、ファイル名に `setup` または `installer` を含む
- 公式名: `GAM-Setup-X.Y.Z.exe`
- 更新時はSHA-256検証後にSetupを起動し、GAMを終了する

Portable版:

- `.zip`で、ファイル名に `portable` を含む
- 公式名: `GAM-Portable-X.Y.Z.zip`
- ZIPのrootに `.gam-portable.json` が必要
- 更新時はSHA-256検証済みZIPをダウンロードし、エクスプローラーで選択表示する
- GAMはSetupを起動せず、別のインストール版も作成しない。ユーザーがGAMを終了し、ZIPを展開して現在のPortableフォルダーを手動で置き換える

どちらのassetにも、GitHub Releases APIが返す有効な `sha256:` digestが必要です。異なる配布形態のassetしかないRelease、digestがないasset、HTTPS以外のdownload URLは拒否されます。

## 6. 動作確認手順

**自動チェック（起動後）**
- 起動5秒後に自動チェックが走ります
- 24時間以内にチェック済みの場合はスキップされます
  - 判定ファイル: `%APPDATA%\GmodAddonManager\last_update_check.txt`

**Setup版の確認**

1. 現在より新しい `vX.Y.Z` Releaseに `GAM-Setup-X.Y.Z.exe` と有効なdigestを用意する
2. 更新を実行し、digest照合後にSetupが起動することを確認する
3. 更新後のアプリversionと既存の `%APPDATA%\GmodAddonManager` 構成が維持されることを確認する

**Portable版の確認**

1. 公式Portable ZIPを展開して起動する（markerを手動で削除しない）
2. 現在より新しいReleaseに `GAM-Portable-X.Y.Z.zip` と有効なdigestを用意する
3. 更新を実行し、ZIPがエクスプローラーで選択表示され、Setupが起動しないことを確認する
4. GAMを終了し、新ZIPを別フォルダーへ展開してから、現在のPortableフォルダーを置き換える

## 7. よくある失敗原因

- リポジトリ名が間違っている（404）
- 非公開リポジトリでトークン未設定
- Setup版なのにSetup `.exe` がない、またはPortable版なのにPortable `.zip` がない
- assetのGitHub digestがない、またはダウンロード結果と一致しない
- 非公式なZIPから `.gam-portable.json` だけが欠落している
- リリースタグが `vX.Y.Z` 形式になっていない
- 同じversionを再発行している（更新判定は「現在より新しいversion」だけを対象にする）

---

### ユーザー向けメモ

- 更新元の上書きとトークンはファイルには保存されません（環境変数のみ）。
- ダウンロードはHTTPSに限定し、GitHub Releases APIのSHA-256 digestと照合します。一致しないSetupは実行せず、一致しないPortable ZIPも表示・適用しません。
- 同じversion番号のReleaseを差し替えても自動更新では検出されません。既に同versionを導入した検証環境では、Releaseから手動で再取得してください。
- 今回削除する旧private `v2.0.0`～`v2.2.0` の導入済み環境は、再構成版 `v2.0.0` が同一versionまたはdowngradeになるため、この条件に該当します。Setup版は先にWindowsの「インストールされているアプリ」から旧GAMをアンインストールし、再作成した `v2.0.0` のSetupで再導入してください。Portable版はGAMを終了し、新しいZIPを展開して旧Portableフォルダーを手動で置き換えてください。アンインストールしても `%APPDATA%\GmodAddonManager` の構成は保持されます。
