# GAM State Model

GAMはSteamの購読状態、GAM上の希望状態、GModに現在適用されている実状態を別々に扱います。

## Truth sources

1. **Subscription / availability**
   SteamのWorkshop metadataを、現在購読中のID集合の真実源として扱います。インストール済みであることだけを購読中とはみなしません。Subscribe Asset詳細は全購読IDを表示し、payloadを読めないIDは利用不可として区別します。メインのカード一覧は利用可能なpayloadだけを表示し、「利用可能 / 購読中」の両方の件数を示します。
2. **Desired state**
   Subscribe Asset、Custom Asset、および固定の`GMod Disabled Addons` AssetからGAMが計算します。
3. **Actual state**
   `garrysmod/cfg/addonnomount.txt`を読み、GModに現在適用されているON/OFFを判断します。Addonカードの`GMod: 有効/無効`はこの値であり、選択中Assetだけの状態ではありません。通常AssetがDisabledでも別のEnabled Assetが有効化元なら、その旨を短い補助表示と理由tooltipで示します。Subscribe Assetでは冗長な補助表示を省き、理由tooltipだけに有効化元を残します。
4. **GMod-side attribution**
   最後に正常に受理したactualを、対象`addonnomount.txt`のpathとともに保持します。GAM自身の書込みは事前journalと成功後baselineで区別し、次回読込み時にゲーム内操作として誤取込みしません。

## Desired-state calculation

現在購読中のAddonについて、希望ONは次の規則で決まります。

```text
ON = 適用されるExcluded Assetが一つもない
     かつ Subscribe ON、Enabled Custom Asset、
          またはEnabled GMod Disabled Addonsのいずれかが有効化元になる
```

- Subscribe Assetは固定で、ON / OFF / すべて除外のいずれか一つです。
- Subscribe ONは現在購読中の全Addonを有効化元にします。
- Subscribe OFFは中立で、Enabled Custom Assetによる有効化を妨げません。
- Subscribeの「すべて除外」は現在購読中の全Addonへ動的に適用され、他のAssetより優先してOFFにします。Custom Assetの状態自体は変更しません。
- Custom AssetはEnabled / Disabled / Excludedのいずれか一つです。
- 複数のEnabled Assetを同時に重ねられます。
- Disabled Assetは計算に寄与しません。
- Excluded Assetは常に優先してOFFにします。
- Smart AssetもCustom Assetの一種で、TypeまたはTagの単一ルールを持ちます。ルールに一致する現在購読中のWorkshop IDを起動時と手動更新時に同期し、その具体的なID一覧を通常の状態計算へ渡します。
- Smart Assetのメンバーは手動編集できません。条件を外れたことが確認できたAddonは外し、Type/Tag情報を一時的に取得できないAddonは既存メンバーのまま維持します。
- Smart Assetの自動メンバー変更はVersionやUndoを増やさず、Smart AssetではメンバーVersion管理を使用しません。
- `GMod Disabled Addons`は固定System Assetですが、通常のAssetと同じくEnabled / Disabled / Excludedを選べます。初期値はDisabled（OFF）です。
- Enabledはメンバーの有効化元、Disabledは中立、Excludedは優先OFFとして計算へ参加します。
- この固定Assetの名称、メンバー、画像、お気に入り、Version、削除はユーザー操作では変更できません。状態だけを変更できます。
- Addon単位の状態はAsset内に保存しません。AssetはメンバーID一覧だけを持ちます。
- toolbarの「すべて無効」はSubscribeとEnabled Custom AssetをDisabledへ変更する別操作です。Subscribeの「すべて除外」は他のAsset状態を保持したまま全件を拒否します。

## Asset Groups and manual ordering

- Asset Groupは状態計算へ新しい優先順位を加えるものではなく、Custom Assetと子Groupを整理・一括操作するための単一親ツリーです。最大入れ子深度は設定で1～10を選べ、初期値1ではroot Group内に子Groupを1段作れます。一つのAssetまたはGroupが所属できる親Groupは最大一つです。
- Asset membershipの真実源は各Assetの`ParentGroupId`、子Group membershipの真実源は各Groupの`ParentGroupId`です。親側に子ID一覧を重複保存しません。Subscribe Assetと`GMod Disabled Addons`はGroupへ入れられません。
- GroupでEnabled / Disabled / Excludedを選ぶと、その時点の全子孫Assetへ同じ状態をmaterializeし、操作対象Groupの`DefaultChildState`も更新します。最終desired stateは子孫Assetの状態から通常どおり計算し、Group自体を別のresolver層にはしません。
- 子孫Assetの状態が異なる場合のMixedは表示用の派生状態で、構成には保存しません。空のGroupは`DefaultChildState`を表示します。
- Group内で新しく作成するAssetは、既存の子孫Assetが一様ならその現在状態を引き継ぎます。空またはMixedの場合は、Groupで最後に一括指定した`DefaultChildState`を引き継ぎます。既存AssetやGroupを別のGroupへ移すだけでは、その子孫Assetの状態を暗黙に書き換えません。
- Group削除時は、直下のAsset / 子Groupを状態不変のまま親containerへ戻す安全な既定動作と、Groupのサブツリーをまとめて削除する動作を明示選択します。後者も一回の保存・runtime reconcile・Undoとしてatomicに扱い、Steam購読やAddon本体は削除しません。
- rootと各Group内の表示順はfavorite / normalの帯を保ち、通常AssetとAsset Groupを分断せず同じ手動順序へ並べます。rootだけは固定System Asset帯がその前にあります。
- カードを直接dragする並び替えは同じcontainer・同じ帯の中だけで行い、固定System Assetや帯境界は越えません。favoriteの切替は対象を対応する帯へ移します。

## GMod Disabled Addons

- IDは`gmod-disabled-system-asset`、表示名は`GMod Disabled Addons`です。論理上は0件でも常に存在し、削除しません。画面ではSubscribe Assetの直下を初期表示とし、Subscribe Assetの収納操作でカードだけを折りたためます。
- 折りたたみ状態はUI設定として保持します。表示を隠しても、このSystem Assetの状態、メンバー、desired-state計算は変わりません。
- 状態は0件でも3択を表示し、GAMによる状態適用ではメンバーを増減しません。
- 現在購読中のAddonだけをメンバーにできます。購読解除されたIDは固定Assetと観測baselineの対象外にしますが、`addonnomount.txt`の古いIDやWorkshop本体は削除しません。
- validなactualが前回受理時のONからOFFへ変わった場合、GMod側の無効化として追加します。OFFからONへ変わった場合は削除します。
- GAMがAsset計算の結果をON/OFFとして適用した場合は、成功したGAM書込みとしてbaselineを進めるため、その変化だけでは固定Assetのメンバーを増減しません。
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
- schema 3からschema 4、schema 4からschema 5、schema 5からschema 6、schema 6からschema 7への移行では、購読履歴、`GMod Disabled Addons`、GAM/GModの観測baseline、および保留中の書込みjournalを維持します。schema 6はAsset Groupと手動順序を追加し、旧表示順を初回だけ各帯の順序へ正規化します。schema 7はAsset Groupの入れ子と階層上限を追加し、既存Groupの親子関係と順序を維持したまま初期上限1を設定します。新しいschemaを理解しない旧版はprofileを開かずfail-closedします。
- 旧構成の一様なCustom Assetは一つの全体状態へ移行します。
- 旧構成内でAddonごとの状態が混在していたAssetは、メンバーを維持したままDisabledにし、確認対象として残します。
- 移行処理だけではGModのactualを書き換えません。

## Path discovery and recovery

- 旧設定にpath記録がなくても、標準のGMod / Workshop場所を正常に検出できる場合は、復旧画面を出さずread-onlyで起動します。
- 記録済みpathが消失・変更した場合、または既存inventoryがあるのにWorkshop場所を読み取れない場合だけ、理由を示して確認します。
- targetが存在しないJunctionなど、列挙できないWorkshop場所は有効な候補として扱いません。
- path確認・保存だけではdesired stateを`addonnomount.txt`へ適用しません。状態適用はAsset状態やメンバーの明示操作に限定します。

## Versions and reset

- 通常の固定Custom Assetの履歴は試験設定で、初期値はOFFです。ONの場合も、その時点のメンバーID一覧だけを保存します。Smart AssetとAsset Groupには履歴を作りません。
- 復元はメンバーだけを変更し、Asset全体状態やSteam購読は変更しません。
- Versionの削除・履歴クリアは現在のメンバーを変更しません。
- 「GAMを初期化」はCustom Asset、Asset Group、お気に入り、履歴、共通除外、`GMod Disabled Addons`のメンバーを消し、同Assetを初期値のDisabled（OFF）、SubscribeをONへ戻します。Steam購読とWorkshop本体は削除しません。
- GMod実行中に初期化した場合も、初期化前actualを観測済みとしてから適用を保留するため、消した固定Assetのメンバーが終了時に再取込みされることはありません。

## `.gam` Assetファイル

- 旧`.gam` v1は、Workshop ID一覧を持つEnabledの通常Assetとして読み込めます。
- 単一Asset形式の`.gam` v2は読込互換用です。共有対象が単一のleaf Assetだけなら、現在はメモにも対応した単一Asset v3で書き出します。
- Bundle形式の`.gam` v3は、1段Groupの旧形式として読み込めます。現在の書き出しは`.gam` v4で、一つ以上の共有対象（Custom Assetまたは階層Asset Group）を同じBundleへ保存します。Groupを選ぶと完全なサブツリーを自動で含め、同じ子孫を明示選択してもBundle内では重複させません。空のGroupや、GroupとrootのAssetが共存する選択も保存できます。
- v4 BundleはBundle内だけのlocal IDでAsset / Groupの単一親ツリーと各コンテナの混在順序を表し、Groupの`DefaultChildState`も保存します。取込み時は全Asset / Groupへ新しい構成IDを割り当て、名前が既存Asset / Groupと衝突する場合はsuffixを付け、階層と順序を復元します。必要階層が現在の設定より深い場合はpreviewで現在値と必要値を示し、確認後に上限とBundleを同じ保存単位で取り込みます。
- 通常AssetはWorkshop ID一覧、Smart Assetは単一ルールと参考用の書き出し時ID一覧を保存します。Enabled / Disabled / Excludedも各Assetへ保存します。Smartのsnapshotは取込先のmembership authorityにはせず、現在の購読集合とType / Tag情報からルールを再評価します。
- Asset ID、お気に入り、履歴、ローカルパス、GMod実状態は保存しません。メモは共有時の初期OFFスイッチをONにした場合だけ保存します。
- 画像を含める全体スイッチは書き出し時の初期値がOFFです。ONの場合だけ各Asset / Groupの画像本体を検証し、最大512 x 512のPNGへ正規化・圧縮して埋め込みます。ローカル画像パスは保存せず、Addon IDやAsset数を画像容量制限のために切り捨てません。
- 取込みは必ず新しいCustom Asset / Asset Groupを作り、Bundle全体を一つのUndo操作として記録します。現在未購読の固定Workshop IDは、そのAsset内に不足参照として保持します。
- preview、書き出し、取込みのいずれもSteamの購読・購読解除やWorkshop manifestの変更を行いません。
- 未知の将来形式、不正なID、ローカルAddon ID、system ID、壊れたBundle整合性は安全のため拒否します。v1～v4はすべて同じ`.gam`拡張子を使用します。

## Experimental local Addons

- ローカルAddonの検出は試験設定で、初期値はOFFです。ONの場合だけGModのローカルAddonを別種として一覧へ読み取り専用表示します。
- ローカルAddonはSteamの購読集合に含めず、Asset / Smart Asset / Asset Groupのメンバー、`.gam`のWorkshop ID、desired-state計算の入力にも加えません。
- GAMはローカルAddonの実体を移動、無効化、削除しません。この試験表示をOFFへ戻してもファイルには触れません。
