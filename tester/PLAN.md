# テスター構築計画（TAS型ラボベンチ）

目的: GAM の BL（削除→再DL相当）と LM（リンク方式）を完全自動・非UIで比較するハーネスを C:\project\GAM\tester 配下に用意し、理論性能（時間・書き込み量・再同期有無）を再現性高く取得する。

## スコープ
- 対象: Core 層（AddonManager 直呼び）と「BL模倣ファイル操作」の2経路。UIは触れない。
- 環境: テンポラリ Workshop ルートと .addon-manager を生成する疑似環境（Steam/GMod 本番は使用しない）。
- 出力: 実行結果 CSV（時間・ステップ数・Undo 回数・成功判定）、必要に応じて IO 記録（外部ツールトリガー）。

## 成果物
- `tester/datasets/` ダミーアドオン生成スクリプトとデータセット定義（ID・サイズ・グループA/B）。
- `tester/scenarios/` タスク定義（JSON/YAML）: actions 列と expected_enabled を持つ。
- `tester/runner/` 
  - `GamRunner.LM`（AddonManager 直呼び）: シナリオ実行・計測・CSV出力。
  - `GamRunner.BL`（BL模倣）: フォルダ/ファイル操作で ON/OFF を模倣し、同じ計測項目を出力。
- `tester/run_benchmark.ps1` / `tester/run_benchmark.sh`: データセット生成→BL/LM n回実行→CSV集約。
- README（使い方と制約: TAS的ラボ計測であり、UI/人操作/Steamサーバ挙動は含まない）。

## フォルダ構成案
- `tester/`
  - `datasets/` … 生成スクリプト・定義ファイル
  - `scenarios/` … タスク定義
  - `runner/` … LM/BL 用コンソールプロジェクト or スクリプト
  - `results/` … 実行ログ/CSV（一時）
  - `run_benchmark.*` … 実行用スクリプト

## シナリオ仕様（例）
- actions: `create_asset`, `add_to_asset`, `enable_asset`, `disable_asset`, `undo`, `sleep_ms`, `set_disable_mode` 等。
- expected_enabled: 最終的に有効であるべき ID 配列（判定に使用）。
- condition: `BL` or `LM` を指定して同一シナリオを両経路で実行。

## 計測項目
- `elapsed_ms`: シナリオ開始〜終了の Stopwatch。
- `steps`: 実行した actions 数。
- `undo_calls`: `undo` action の回数。
- `success`: expected_enabled との一致フラグ。
- （オプション）IO: 外部ツール開始/終了トリガーで取得した書き込み量を別CSVに保存。

## 実行フロー（run_benchmark）
1. テンポラリの Workshop ルートと .addon-manager を作成。
2. データセット定義に従いダミーアドオンを生成（ID・サイズ・A/B グループ）。
3. 指定シナリオ群を BL/LM で n 回ずつ実行し、`results/runs.csv` に追記。
4. 必要に応じ IO トレースを開始/停止。
5. 後処理（集約・グラフ化は任意、ここではCSVまで）。

## 留意点・制約
- ラボ/TAS計測: UI・人操作・Steamサーバ挙動は含まず、実運用体感とは乖離する。論文ではその旨を明記。
- Windows/WSL 両対応を目指すが、ハードリンク/ジャンクションの動作は Windows 前提。WSL/非WindowsではBL模倣のみ有効にするフォールバックを検討。
- ファイルサイズはダミー生成（ランダムバイト）で IO負荷を再現。

## 次のステップ（実装手順）
1. `tester/runner` に LM/BL 用コンソール（dotnet）プロジェクトを追加し、シナリオ実行と計測・CSV出力を実装。（済）
2. `tester/datasets` にダミー生成スクリプトとサンプルデータセット（A/B 50件など）を用意。（済）
3. `tester/scenarios` に T1/T2/T3 相当のサンプルシナリオを作成。（進行中）
4. `run_benchmark` スクリプトで一括実行・結果保存を実装。（済）
5. README を整備（使い方・前提・制約）。SteamCMD モードの案内を追記。（済）
