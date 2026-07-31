# GAM State Model

GAMはSteamの購読状態、GAM上の希望状態、GModに現在適用されている実状態を別々に扱います。

## Truth sources

1. **Subscription / availability**
   SteamのWorkshop metadataを、現在購読中のID集合の真実源として扱います。インストール済みであることだけを購読中とはみなしません。Subscribe Asset詳細は全購読IDを表示し、payloadを読めないIDは利用不可として区別します。メインのカード一覧は利用可能なpayloadだけを表示し、「利用可能 / 購読中」の両方の件数を示します。
2. **Desired state**
   Subscribe AssetとCustom Assetの状態・メンバーからGAMが計算します。
3. **Actual state**
   `garrysmod/cfg/addonnomount.txt`を読み、GModに現在適用されているON/OFFを判断します。

## Desired-state calculation

現在購読中のAddonについて、希望ONは次の規則で決まります。

```text
ON = (SubscribeがON または Enabled Custom Assetのいずれかに所属)
     かつ Excluded Custom Assetのどれにも所属しない
```

- Subscribe Assetは固定で、ON/OFFだけを持ちます。
- Custom AssetはEnabled / Disabled / Excludedのいずれか一つです。
- 複数のEnabled Assetを同時に重ねられます。
- Disabled Assetは計算に寄与しません。
- Excluded Assetは常に優先してOFFにします。
- Addon単位の状態はAsset内に保存しません。AssetはメンバーID一覧だけを持ちます。

## Runtime boundary

- 起動、画面復帰、更新ではactualを読み取りますが、自動では書き戻しません。
- GAMでAsset状態やメンバーを明示的に変更したとき、現在購読中の管理対象へ最新desiredを適用します。
- GAMが認識していない`addonnomount.txt`のIDは保持します。
- GMod実行中はdesiredだけを保存し、終了後に最新desiredのfull reconcileを一度だけ行います。

## First run and migration

- 新規profileでは既存`addonnomount.txt`を確認なしで一度だけ読みます。
- 「現在購読中かつ無効」のIDがある場合だけ、Excluded Asset「GModで無効化されていたAddon」を作ります。
- 未購読の古いIDは取り込まず、元の`addonnomount.txt`からも削除しません。
- 旧構成の一様なCustom Assetは一つの全体状態へ移行します。
- 旧構成内でAddonごとの状態が混在していたAssetは、メンバーを維持したままDisabledにし、確認対象として残します。
- 移行処理だけではGModのactualを書き換えません。

## Path discovery and recovery

- 旧設定にpath記録がなくても、標準のGMod / Workshop場所を正常に検出できる場合は、復旧画面を出さずread-onlyで起動します。
- 記録済みpathが消失・変更した場合、または既存inventoryがあるのにWorkshop場所を読み取れない場合だけ、理由を示して確認します。
- targetが存在しないJunctionなど、列挙できないWorkshop場所は有効な候補として扱いません。
- path確認・保存だけではdesired stateを`addonnomount.txt`へ適用しません。状態適用はAsset状態やメンバーの明示操作に限定します。

## Versions and reset

- Asset Versionは、その時点のメンバーID一覧だけを保存します。
- 復元はメンバーだけを変更し、Asset全体状態やSteam購読は変更しません。
- Versionの削除・履歴クリアは現在のメンバーを変更しません。
- 「GAMを初期化」はCustom Asset、お気に入り、Version、共通除外を消してSubscribeをONへ戻します。Steam購読とWorkshop本体は削除しません。
