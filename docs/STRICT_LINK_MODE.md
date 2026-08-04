# Strict Link Mode（アーカイブ）

GAM v2.0.0 の正式な動作方式は、Garry's Mod の `addonnomount.txt` を使う soft-only 方式です。

この文書が以前説明していた junction、hard link、copy fallback および Strict Link Mode は、v1 の実験的な実装に属します。v2.0.0 の製品UIと通常の起動経路では提供しません。

- `GAM_STRICT_LINK_MODE` など、過去の実験用環境変数はサポート対象外です。設定せずに使用してください。
- 古い手順で `.addon-manager` 配下やWorkshopファイルを手動操作しないでください。
- v1 が残した正確な旧管理配置は、v2.0.0 の初回起動時に安全性を検証したうえで自動復旧します。競合や改変を検出した場合は、推測で上書きせず起動を中止します。

現行仕様と移行手順は [README](../README.md) を参照してください。
