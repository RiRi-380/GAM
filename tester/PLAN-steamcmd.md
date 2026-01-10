# SteamCMD モード計画書（TAS+実DLベンチ）

## 目的
- 既存の TAS ラボベンチに、SteamCMD を用いた「実ダウンロード/再DL」を含む BL（削除→再DL模倣）と LM（AddonManager 直呼び）計測パスを追加し、Steam 側の再同期やネットワーク起因のコストを含むデータを取得できるようにする。

## 前提
- SteamCMD が利用可能で、Garry's Mod を所有する個人アカウントでログイン（匿名不可、Steam Guard 対応）。
- 既存のテスター CLI（tester/runner/GamTester）に `--mode steamcmd` などのオプションを追加済み。
- 安全のため、デフォルトでは一時ライブラリ（%TEMP%/gam-tester/<guid>/steamcmd-library）を使用し、本番ライブラリを汚さない。実ライブラリを指定する場合は削除→再DLが走る点を明記。

## 入出力
- 入力:
  - dataset: Workshop ID のリスト（例: `tester/datasets/steam-workshop-ab.json`）
  - scenario: アクション列（例: switch-a-b）
  - SteamCMD 認証情報: `--steam-user`, `--steam-password`, `--steam-guard`(任意)
  - SteamCMD 実行パス: `--steamcmd-path`
  - SteamCMD ライブラリパス（任意）
- 出力:
  - `tester/results/runs.csv`: `elapsed_ms, steps, undo_calls, write_bytes, steam_sync, success, error` など（行増分）
  - workshop_log.txt の増分行数で再同期を近似（ログが無い場合は 0）

## 実行フロー（SteamCMD モード）
1. 一時ワークルート/ライブラリを準備（`--steam-library` 指定がなければ temp を使用）。
2. SteamCMD で dataset の全IDを `workshop_download_item 4000 <id>` で取得。
3. scenario の `initial_enabled_groups` に従い、不要なフォルダを削除（初期有効セットだけを残す）。
4. BL 実行: scenario の enable/disable をファイル削除/再DLで模倣。必要 ID を再度 SteamCMD で download。所要時間を計測。
5. LM 実行: 同じライブラリを AddonManager に向け、リンク/ハードリンクで切替。所要時間を計測。
6. 期待状態と一致するか判定し、CSV へ追記。
7. workshop_log.txt を実行前後でスナップショットし、行増分を steam_sync として記録。

## 懸念・留意点
- Steam Guard の入力が必要な場合は初回だけ手動対応。その後はセッションキャッシュを再利用。
- ネットワーク/Steamサーバの混雑でばらつきが出るため、n 回反復し平均/標準偏差で扱う。
- 実ライブラリ指定時は既存データを削除/再DLするため、必ずバックアップまたは専用ライブラリを使う旨を README に明記。
- `write_bytes` はワークショップディレクトリのサイズ差分ベースで概算（ハードリンクでは増えない）。精度が必要なら PerfMon/WPR 連動を別途検討。

## 今後の拡張余地
- CSV に再同期フラグ/ダウンロードバイト数などを追加（workshop_log の簡易パーサ）。
- IO トレース（PerfMon/WPR）の開始/停止をランナーにフックして自動化。
- SteamCMD ログインエラー/Guard 再要求のリトライ処理を追加。***
