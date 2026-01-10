# GAM テスター（TAS型ラボベンチ）

UI を経由せず、AddonManager 直呼び（LM）と BL 模倣（ファイルコピー/削除）のシナリオを完全自動で実行し、時間・ステップ数・成功可否を CSV に記録します。ダミーの Workshop 環境をテンポラリに生成するため、Steam/GMod 本番環境を汚しません。

## ディレクトリ
- `datasets/` … ダミーアドオン定義（ID・グループ・サイズ・形式）
- `scenarios/` … アクション列と期待状態を持つシナリオ定義
- `runner/GamTester/` … CLI ランナー（LM/BL 兼用）
- `results/` … 実行結果 CSV の保存先（自動生成）
- `run_benchmark.ps1`, `run_benchmark.sh` … サンプル実行スクリプト

## 使い方（例）
```powershell
dotnet run --project tester/runner/GamTester/GamTester.csproj -- `
  --dataset tester/datasets/sample-dataset.json `
  --scenario tester/scenarios/switch-a-b.json `
  --condition LM `
  --repeat 3 `
  --results tester/results/runs.csv
```

BL（削除→再DL模倣）で同じシナリオを実行する場合:
```powershell
dotnet run --project tester/runner/GamTester/GamTester.csproj -- `
  --dataset tester/datasets/sample-dataset.json `
  --scenario tester/scenarios/switch-a-b.json `
  --condition BL `
  --repeat 3 `
  --results tester/results/runs.csv
```

### SteamCMDモード（実Workshopダウンロードを含める）
SteamCMDで実IDを落としつつ実行する場合（個人アカウント必要、ガードコードはオプション）。ライブラリは安全のためデフォルトで一時フォルダに作成されます。
```powershell
dotnet run --project tester/runner/GamTester/GamTester.csproj -- `
  --mode steamcmd `
  --steamcmd-path "C:\steamcmd\steamcmd.exe" `
  --steam-user "<your_steam_user>" `
  --steam-password "<your_password>" `
  --steam-guard "<guard_code_if_needed>" `
  --dataset tester/datasets/steam-workshop-ab.json `
  --scenario tester/scenarios/switch-a-b.json `
  --condition BL `
  --repeat 1 `
  --results tester/results/runs.csv
```
オプションで `--steam-library <path>` を指定すると既存ライブラリを使いますが、BLは「削除→再DL」を模倣するため内容を削除する点に注意してください。安全のため専用の SteamCMD ライブラリを推奨します。

### オプション
- `--dataset <path>`: データセット JSON
- `--scenario <path>`: シナリオ JSON
- `--condition LM|BL`: LM=AddonManager 直呼び, BL=ファイル操作模倣（省略時はシナリオの condition または LM）
- `--repeat <N>`: 同じ条件を N 回反復（デフォルト1）
- `--results <csv>`: 出力先（デフォルト `tester/results/runs.csv`）
- `--workroot <path>`: テンポラリワークスペースのルート（省略時は `%TEMP%/gam-tester/<guid>`）
- `--mode local|steamcmd`: local=ダミー環境, steamcmd=実Workshopダウンロード
- `--steamcmd-path`: steamcmd 実行ファイルのパス
- `--steam-user / --steam-password / --steam-guard`: SteamCMDのログイン情報（GMod所有アカウントが必要）
- `--steam-library`: SteamCMDのインストール/ライブラリパス（省略時は一時フォルダ）
- 実行例スクリプト: `run_benchmark.ps1/.sh`（ローカルダミー）, `run_benchmark_steamcmd.ps1/.sh`（SteamCMD; 環境変数 GAM_STEAM_USER/PASSWORD/CMD_PATH/… を使用）
- データセット生成補助: `tester/scripts/generate-dataset-from-acf.ps1`（`appworkshop_4000.acf` から購読IDを抽出してA/Bに振り分け）

## シナリオ仕様（抜粋）
- `actions`: `create_asset`, `add_to_asset`, `add_group_to_asset`, `enable_asset`, `disable_asset`, `enable_addon`, `disable_addon`, `undo`, `sleep_ms`, `set_disable_mode` をサポート。
- `expected_enabled` または `expected_enabled_groups`: ゴール状態。空の場合は成功判定を行わない。
- `initial_enabled_groups`: 初期有効化するグループ（省略時は全グループ）。

## 制約
- LM は AddonManager の動作に依存し、Windows 環境でのジャンクション/ハードリンク権限が必要です。BL はファイルコピー/削除のみで動作します。
- これは「TAS的」ラボ計測用です。UI操作や体感レスポンスは含みません。SteamCMDモードを使うと実ダウンロード/再DLを含められますが、ネットワーク/サーバ状況によるばらつきが出ることを理解してください。
- CSV には `write_bytes`（推定書き込み量: サイズ差分ベース）と `steam_sync`（workshop_log.txt の増分行数）を出力します。workshop_log.txt が無い環境では 0 になります。***
