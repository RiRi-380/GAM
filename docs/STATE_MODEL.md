# GAM State Model

GAMはSteamの購読状態、GAM上の希望状態、GModに現在適用されている実状態を別々に扱います。

## Truth sources

1. **Subscription / availability**
   SteamのWorkshop metadataを、現在購読中のID集合の真実源として扱います。インストール済みであることだけを購読中とはみなしません。Subscribe Asset詳細は全購読IDを表示し、payloadを読めないIDは利用不可として区別します。メインのカード一覧は利用可能なpayloadだけを表示し、「利用可能 / 購読中」の両方の件数を示します。
2. **Desired state**
   Subscribe Asset、Custom Asset、および固定の`GMod Disabled Addons` AssetからGAMが計算します。
3. **Actual state**
   `garrysmod/cfg/addonnomount.txt`を読み、GModに現在適用されているON/OFFを判断します。
4. **GMod-side attribution**
   最後に正常に受理したactualを、対象`addonnomount.txt`のpathとともに保持します。GAM自身の書込みは事前journalと成功後baselineで区別し、次回読込み時にゲーム内操作として誤取込みしません。

## Desired-state calculation

現在購読中のAddonについて、希望ONは次の規則で決まります。

```text
ON = Subscribeが「すべて除外」ではない
     かつ (SubscribeがON または Enabled Custom Assetのいずれかに所属)
     かつ Excluded Custom Assetのどれにも所属しない
     かつ GMod Disabled Addonsに所属しない
```

- Subscribe Assetは固定で、ON / OFF / すべて除外のいずれか一つです。
- Subscribe ONは現在購読中の全Addonを有効化元にします。
- Subscribe OFFは中立で、Enabled Custom Assetによる有効化を妨げません。
- Subscribeの「すべて除外」は現在購読中の全Addonへ動的に適用され、他のAssetより優先してOFFにします。Custom Assetの状態自体は変更しません。
- Custom AssetはEnabled / Disabled / Excludedのいずれか一つです。
- 複数のEnabled Assetを同時に重ねられます。
- Disabled Assetは計算に寄与しません。
- Excluded Assetは常に優先してOFFにします。
- `GMod Disabled Addons`は固定System Assetで、常にExcludedとして計算へ参加します。
- この固定Assetの名称、状態、メンバー、画像、お気に入り、Version、削除はユーザー操作では変更できません。
- Addon単位の状態はAsset内に保存しません。AssetはメンバーID一覧だけを持ちます。
- toolbarの「すべて無効」はSubscribeとEnabled Custom AssetをDisabledへ変更する別操作です。Subscribeの「すべて除外」は他のAsset状態を保持したまま全件を拒否します。

## GMod Disabled Addons

- IDは`gmod-disabled-system-asset`、表示名は`GMod Disabled Addons`です。Subscribe Assetの直下に常時表示し、0件でも消しません。
- 現在購読中のAddonだけをメンバーにできます。購読解除されたIDは固定Assetと観測baselineの対象外にしますが、`addonnomount.txt`の古いIDやWorkshop本体は削除しません。
- validなactualが前回受理時のONからOFFへ変わった場合、GMod側の無効化として追加します。OFFからONへ変わった場合は削除します。
- GAMがSubscribe OFF、Custom Excluded、または有効元なしを適用してOFFにした場合は、成功したGAM書込みとしてbaselineを進めるため追加しません。
- 他のExcluded Assetと重なっていても、GMod側でONへ戻された事実だけを反映して固定Assetから外します。他のExcluded Assetはそのまま残るため、次回の明示的なGAM適用では引き続きOFFになります。
- baselineに存在しない新規・再購読IDは、最初の観測を基準として受理します。購読解除中に残った古いOFFを新しいゲーム内操作とはみなしません。

## Runtime boundary

- 起動、画面復帰、手動更新、GMod終了時にはvalidなactualを読み、固定Assetと観測baselineを同じ設定保存で更新します。この観測処理だけではGModへ書き戻しません。
- GAMでAsset状態やメンバーを明示的に変更したとき、現在購読中の管理対象へ最新desiredを適用します。
- GAMが認識していない`addonnomount.txt`のIDは保持します。
- GMod実行中はdesiredだけを保存します。終了時はゲーム内変更を先に固定Assetへ反映し、その後に最新desiredのfull reconcileを一度だけ行います。
- GAM書込みは、target・書込み前状態・対象pathを先にatomic保存してから実行します。異常終了後は、actualがtargetならGAM成功、書込み前状態なら未実行として回復します。どちらでもない場合は現在のGMod状態を優先し、曖昧な自動上書きを行いません。
- `addonnomount.txt`が読めない、形式不正、Steam購読集合が非authoritative、または対象pathが変わった場合は、異なるtruth source同士の差分をゲーム内操作とみなしません。

## First run and migration

- 新規profileでは既存`addonnomount.txt`を確認なしで読み、「現在購読中かつ無効」のIDを固定Assetへ入れます。
- 未購読の古いIDは取り込まず、元の`addonnomount.txt`からも削除しません。
- schema 2の未編集な旧Asset「GModで無効化されていたAddon」は固定Assetへ変換します。名前、状態、画像、Versionなどが編集されていて判別が曖昧なAssetはCustom Assetのまま残します。
- schema 2から最初に読み込むactualでは、旧desiredがONなのにactualがOFFのIDだけを追加候補にします。旧GAMのSubscribe OFFやCustom ExcludedによるOFFは混ぜません。
- schema 3からschema 4への移行では、購読履歴、`GMod Disabled Addons`、GAM/GModの観測baseline、および保留中の書込みjournalを維持します。schema 4を理解しない旧版はprofileを開かずfail-closedします。
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
- 「GAMを初期化」はCustom Asset、お気に入り、Version、共通除外、`GMod Disabled Addons`のメンバーを消してSubscribeをONへ戻します。Steam購読とWorkshop本体は削除しません。
- GMod実行中に初期化した場合も、初期化前actualを観測済みとしてから適用を保留するため、消した固定Assetのメンバーが終了時に再取込みされることはありません。
