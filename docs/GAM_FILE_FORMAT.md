# GAM `.gam` ファイル形式仕様

この文書は、AIや外部ツールからGAMへ取り込める `.gam` ファイルを生成するための仕様書です。
GAMの現行実装（2026-08-03時点）を真実源としており、新規作成では次の2形式だけを使用してください。

- 単一Assetを渡す: **Single Asset v3**（UTF-8 JSON）
- 複数Asset、Asset Group、入れ子Groupを渡す: **Bundle v4**（ZIP）

> [!IMPORTANT]
> `.gam` のインポートはSteam Workshopの購読・購読解除を行いません。
> Fixed Assetに未購読のWorkshop IDがあれば、不足中の参照として保持します。
> 一方、取り込んだAssetの状態はGAMの有効・無効計算へ参加するため、GMod側のAddon状態には影響し得ます。

## 1. まず選ぶ形式

| 作りたいもの | 使用形式 | ファイルの実体 | `format` | `version` |
|---|---|---|---|---:|
| 1個のAsset | Single Asset v3 | UTF-8 JSON | `gam-asset` | 3 |
| 複数Asset | Bundle v4 | ZIP | `gam-asset-bundle` | 4 |
| Asset Group | Bundle v4 | ZIP | `gam-asset-bundle` | 4 |
| Group内のGroup | Bundle v4 | ZIP | `gam-asset-bundle` | 4 |

新規ファイルではv1、v2、旧Bundle v3を生成しないでください。これらは読み込み互換専用です。

同じ「v3」でも、次の2種類は別物です。

- Single Asset v3: ZIPではないJSON
- 旧Bundle v3: ZIP内にmanifestを持つ旧形式

GAMは先頭2バイトがZIP署名 `PK` ならBundle、それ以外ならSingle Assetとして判定します。

## 2. AI生成時の絶対ルール

1. Workshop IDは、ユーザーから与えられた実在IDだけを使う。AIが推測・創作しない。
2. Workshop URLではなく、URL末尾の数値IDをJSON文字列として入れる。
3. 意図せず現在のGMod状態へ影響させたくない場合は、Assetを `"disabled"` にする。
4. `"excluded"` は中立ではなく、他のEnabled Assetより優先して対象Addonを無効化する強い状態である。
5. 画像が必要と明示されていなければ、画像フィールドを丸ごと省略する。
6. 説明用の独自フィールド、コメント、末尾コンマを追加しない。
7. JSONの値がない場合に `null` を置かず、省略可能フィールドならフィールドごと省略する。
8. 複数AssetまたはGroupは、JSONファイルを `.gam` に改名するだけでは作れない。必ずBundle ZIPを作る。

## 3. 共通データ型

### 3.1 Workshop ID

`addonIds` と `snapshotAddonIds` の各要素には、同じ規則が適用されます。

- JSON数値ではなくJSON文字列
- ASCIIの10進数字だけ
- 1～20文字
- 先頭の `0` は不可
- 数値として1以上、`18446744073709551615` 以下
- 符号、空白、小数点、URL、ローカルAddon IDは不可
- 同じ配列内で重複不可
- 別々のAssetに同じWorkshop IDが入ることは可能

概ね次の正規表現に一致し、さらに64-bit符号なし整数の最大値以下である必要があります。

```regex
^[1-9][0-9]{0,19}$
```

正しい例:

```json
"104607712"
```

誤った例:

```text
104607712                 JSON数値なので不可
"0"                       0は不可
"01"                      先頭0は不可
"+123"                    符号は不可
" 123"                    空白は不可
"https://.../123"         URLは不可
"local-addon-123"         ローカルIDは不可
"18446744073709551616"    上限超過
```

Addon数にGAM独自の固定上限はありません。数万件のIDもcodecで扱えますが、実際にはメモリ、ディスク、処理時間の制約を受けます。

### 3.2 名前

Asset名とGroup名の規則です。

- 必須のJSON文字列
- 前後空白を除去した結果が1文字以上
- 前後空白除去後、最大200 UTF-16コード単位
- 改行、タブ、NULなどの制御文字は禁止
- 日本語を含むUnicode文字は使用可能

Bundle内では、Asset名とGroup名を合わせて大文字・小文字を区別せず一意でなければなりません。
既存のGAM設定内の名前と衝突した場合は、インポート時に ` (2)` などのsuffixが付いた一意名へ調整されます。

> [!NOTE]
> 「200文字」は.NETのUTF-16コード単位で判定されます。絵文字など一部の文字は2単位として数えられます。

### 3.3 メモ

`memo` は任意です。不要ならフィールドごと省略します。

- 最大4096 UTF-16コード単位
- CRLFとCRはLFへ正規化
- LF改行とタブは使用可能
- それ以外の制御文字は禁止
- 空文字または空白だけなら「メモなし」として扱われる
- `null` は不可

### 3.4 Asset状態

| wire値 | 意味 |
|---|---|
| `"enabled"` | 対象Addonを有効化する側へ寄与する |
| `"disabled"` | 状態計算へ寄与しない中立状態 |
| `"excluded"` | 他のEnabled Assetより優先し、対象Addonを無効化する |

値は小文字の完全一致です。

### 3.5 Fixed membership

ID一覧そのものをAssetのメンバーにする形式です。

```json
"membership": {
  "kind": "fixed",
  "addonIds": [
    "104607712",
    "1234567890"
  ]
}
```

- `kind` と `addonIds` は必須
- `addonIds` は空配列でもよい
- `rule` と `snapshotAddonIds` は書いてはいけない
- 未購読IDも不足中の参照として保持される

### 3.6 Smart membership

TypeまたはTagの条件へ一致する、現在購読中のAddonを自動的にメンバーにする形式です。

```json
"membership": {
  "kind": "smart",
  "rule": {
    "kind": "type",
    "value": "Weapon"
  },
  "snapshotAddonIds": [
    "1234567890"
  ]
}
```

- `kind`、`rule`、`snapshotAddonIds` は必須
- `snapshotAddonIds` は0件でも `[]` が必須
- `addonIds` は書いてはいけない
- `snapshotAddonIds` は書き出し時点の参考情報であり、メンバーの真実源ではない
- インポート先では `rule` を現在の購読集合と取得済みメタデータへ再評価する
- 特定IDを必ず入れたい場合はSmartではなくFixedを使う

`rule.kind` は小文字の `"type"` または `"tag"` です。

Typeで使用できる正規値:

```text
Gamemode
Map
Weapon
Vehicle
NPC
Tool
Entity
Effects
Model
ServerContent
```

Tagで使用できる正規値:

```text
Build
Cartoon
Comic
Fun
Movie
Roleplay
Scenic
Realism
Water
```

読み込み時は値の前後空白と大文字・小文字の違いを正規化しますが、AIは上記の正規表記をそのまま使ってください。未対応値や独自値は拒否されます。

## 4. Single Asset v3

### 4.1 ファイルの実体

- 拡張子: `.gam`（大文字・小文字は不問）
- 内容: ZIPではない通常のJSON
- 文字コード: UTF-8
- 推奨: BOMなし、標準JSON、末尾改行あり
- `format`: 正確に `"gam-asset"`
- `version`: JSON整数の `3`
- JSON最大深度: 16

Single Assetはファイル全体を1個のbyte配列へ読み込むため、実装上 `int.MaxValue` bytes以上のファイルは拒否されます。それ未満でも利用可能メモリの実用上限を受けます。極端に大きい共有物にはstreamingで読むBundle v4を使用してください。

フィールド順は問いません。重複キー、未知フィールド、JSON本体後方の別コンテンツは拒否されます。

### 4.2 最小のFixed Asset

以下をUTF-8で `fps.gam` として保存すれば、単一Assetファイルになります。

```json
{
  "format": "gam-asset",
  "version": 3,
  "asset": {
    "name": "FPS用",
    "state": "disabled",
    "membership": {
      "kind": "fixed",
      "addonIds": [
        "104607712",
        "1234567890"
      ]
    }
  }
}
```

この例のIDは構造例です。実際の生成時はユーザーが指定した実在Workshop IDへ置き換えてください。

空のFixed Assetも有効です。

```json
{
  "format": "gam-asset",
  "version": 3,
  "asset": {
    "name": "空のアセット",
    "state": "disabled",
    "membership": {
      "kind": "fixed",
      "addonIds": []
    }
  }
}
```

### 4.3 メモ付きFixed Asset

```json
{
  "format": "gam-asset",
  "version": 3,
  "asset": {
    "name": "撮影用",
    "state": "disabled",
    "membership": {
      "kind": "fixed",
      "addonIds": [
        "104607712",
        "1234567890"
      ]
    },
    "memo": "撮影するときだけ有効にする。\n競合するFPS用Assetは先に無効化する。"
  }
}
```

### 4.4 Smart Asset

```json
{
  "format": "gam-asset",
  "version": 3,
  "asset": {
    "name": "Weapon自動分類",
    "state": "disabled",
    "membership": {
      "kind": "smart",
      "rule": {
        "kind": "type",
        "value": "Weapon"
      },
      "snapshotAddonIds": []
    }
  }
}
```

### 4.5 正確なオブジェクト構造

トップレベル:

| フィールド | 必須 | 型 | 制約 |
|---|---:|---|---|
| `format` | はい | string | `gam-asset` 固定 |
| `version` | はい | integer | `3` 固定 |
| `asset` | はい | object | 下表参照 |
| `image` | いいえ | object | 4.7参照 |

`asset`:

| フィールド | 必須 | 型 | 制約 |
|---|---:|---|---|
| `name` | はい | string | 共通の名前規則 |
| `state` | はい | string | `enabled` / `disabled` / `excluded` |
| `membership` | はい | object | FixedまたはSmart |
| `memo` | いいえ | string | 共通のメモ規則 |

この表にないフィールドは使用できません。画像は `asset.image` ではなく、トップレベルの `image` に置きます。

### 4.6 生成用JSON Schema

JSON Schemaだけでは64-bit上限やUTF-16の厳密な文字数まで表現できないため、この文書の追加制約も必ず守ってください。

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "title": "GAM Single Asset v3",
  "type": "object",
  "additionalProperties": false,
  "required": ["format", "version", "asset"],
  "properties": {
    "format": { "const": "gam-asset" },
    "version": { "const": 3 },
    "asset": {
      "type": "object",
      "additionalProperties": false,
      "required": ["name", "state", "membership"],
      "properties": {
        "name": { "type": "string", "minLength": 1, "maxLength": 200 },
        "state": { "enum": ["enabled", "disabled", "excluded"] },
        "memo": { "type": "string", "maxLength": 4096 },
        "membership": {
          "oneOf": [
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["kind", "addonIds"],
              "properties": {
                "kind": { "const": "fixed" },
                "addonIds": {
                  "type": "array",
                  "uniqueItems": true,
                  "items": {
                    "type": "string",
                    "pattern": "^[1-9][0-9]{0,19}$"
                  }
                }
              }
            },
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["kind", "rule", "snapshotAddonIds"],
              "properties": {
                "kind": { "const": "smart" },
                "rule": {
                  "oneOf": [
                    {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["kind", "value"],
                      "properties": {
                        "kind": { "const": "type" },
                        "value": {
                          "enum": [
                            "Gamemode", "Map", "Weapon", "Vehicle", "NPC",
                            "Tool", "Entity", "Effects", "Model", "ServerContent"
                          ]
                        }
                      }
                    },
                    {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["kind", "value"],
                      "properties": {
                        "kind": { "const": "tag" },
                        "value": {
                          "enum": [
                            "Build", "Cartoon", "Comic", "Fun", "Movie",
                            "Roleplay", "Scenic", "Realism", "Water"
                          ]
                        }
                      }
                    }
                  ]
                },
                "snapshotAddonIds": {
                  "type": "array",
                  "uniqueItems": true,
                  "items": {
                    "type": "string",
                    "pattern": "^[1-9][0-9]{0,19}$"
                  }
                }
              }
            }
          ]
        }
      }
    },
    "image": {
      "type": "object",
      "additionalProperties": false,
      "required": ["mediaType", "sha256", "data"],
      "properties": {
        "mediaType": { "const": "image/png" },
        "sha256": {
          "type": "string",
          "pattern": "^[0-9a-fA-F]{64}$"
        },
        "data": {
          "type": "string",
          "minLength": 1,
          "maxLength": 5592408,
          "contentEncoding": "base64"
        }
      }
    }
  }
}
```

### 4.7 Single Assetの画像

画像は任意です。画像なしならトップレベルの `image` を省略します。

```json
"image": {
  "mediaType": "image/png",
  "sha256": "<PNG生バイト列のSHA-256、64桁16進数>",
  "data": "<同じPNG生バイト列の標準Base64>"
}
```

- `mediaType` は大文字・小文字を含めて正確に `image/png`
- `data` は `data:image/png;base64,` 接頭辞を付けない
- URL-safe Base64、空白、改行、padding省略は不可
- `Convert.ToBase64String(Base64Decode(data))` が元の文字列と完全一致する正規Base64が必要
- `sha256` はBase64文字列ではなく、Base64デコード後のPNG生バイト列に対して計算
- writerは小文字hexで出力。readerの照合はhexの大文字・小文字を区別しない

入力PNGの制限:

- 1画像あたり最大4,194,304 bytes（4 MiB）
- 幅・高さは各1～8192 px
- 最大16,777,216 pixels
- RGBA換算のデコードサイズは最大67,108,864 bytes（64 MiB）
- 破損・途中切れ・デコード不能画像は拒否

GAMは読み込み時に中央を正方形へクロップし、512×512、角丸半径80 pxのPNGへ再処理します。正規化後は最大2,097,152 bytesです。そのため、埋め込んだPNGとGAM内へ保存されるPNGはバイト単位で同じとは限りません。

画像処理はエラー原因になりやすいため、AI生成の初回は画像なしを推奨します。

### 4.8 Single Assetを作る最短手順

1. 4.2または4.4のJSONを作る。
2. 実在Workshop IDだけを文字列で入れる。
3. 画像を使わないなら `image` を追加しない。
4. UTF-8の `任意名.gam` として保存する。ZIP化しない。
5. GAMのインポート確認画面で名前、状態、不足参照を確認してから確定する。

## 5. Bundle v4

### 5.1 ファイルの実体

Bundle v4は、次のエントリだけを持つZIPファイルです。拡張子を `.gam` にします。

```text
manifest.json
manifest.sha256
images/assets/<Asset localId>.png     # 任意
images/groups/<Group localId>.png     # 任意
```

ZIP内のエントリ順と圧縮方式は問いません。DeflateとStoreのどちらでも読み込めます。

禁止されるZIPエントリ:

- 明示的なディレクトリエントリ
- `notes.txt` など仕様外の追加ファイル
- manifestから参照されていない画像
- 大文字・小文字違いを含む同名エントリ
- 絶対パス、ドライブ文字、`..`、`.`、空path segment
- バックスラッシュを含むpath
- 末尾が `/` のpath
- 512文字を超えるエントリ名

### 5.2 manifestのトップレベル

`manifest.json` は**BOMなしUTF-8**のJSONです。Bundle readerはBOMを文字コード署名として除去しないため、UTF-8 BOM付きmanifestは拒否されます。新規作成では次の5フィールドをすべて書きます。

```json
{
  "format": "gam-asset-bundle",
  "version": 4,
  "assets": [],
  "groups": [],
  "rootChildren": []
}
```

| フィールド | 必須 | 型 | 意味 |
|---|---:|---|---|
| `format` | はい | string | `gam-asset-bundle` 固定 |
| `version` | はい | integer | `4` 固定 |
| `assets` | はい | array | 全Assetの定義表 |
| `groups` | はい | array | 全Groupの定義表 |
| `rootChildren` | はい | array | root直下の混在順序 |

AssetとGroupの両方が0件の空Bundleは不可です。空のGroupを1件だけ含むBundleは有効です。

JSONフィールド順は問いませんが、未知・重複フィールドは拒否されます。JSON最大深度は24です。

### 5.3 Bundle内Asset

Fixed Asset:

```json
{
  "localId": "asset-fps",
  "name": "FPS",
  "memo": "FPS向けの固定構成",
  "state": "disabled",
  "membership": {
    "kind": "fixed",
    "addonIds": [
      "104607712"
    ]
  }
}
```

Smart Asset:

```json
{
  "localId": "asset-weapons",
  "name": "Weapons Smart",
  "state": "disabled",
  "membership": {
    "kind": "smart",
    "rule": {
      "kind": "type",
      "value": "Weapon"
    },
    "snapshotAddonIds": []
  }
}
```

| フィールド | 必須 | 型 | 制約 |
|---|---:|---|---|
| `localId` | はい | string | 5.6参照 |
| `name` | はい | string | 共通の名前規則 |
| `memo` | いいえ | string | 共通のメモ規則 |
| `state` | はい | string | 3状態のいずれか |
| `membership` | はい | object | FixedまたはSmart |
| `image` | いいえ | object | 5.8参照 |

### 5.4 Bundle内Group

```json
{
  "localId": "group-main",
  "name": "Main Group",
  "memo": "メイン構成",
  "defaultChildState": "disabled",
  "children": [
    {
      "kind": "asset",
      "localId": "asset-fps"
    },
    {
      "kind": "group",
      "localId": "group-child"
    }
  ]
}
```

| フィールド | 必須 | 型 | 制約 |
|---|---:|---|---|
| `localId` | はい | string | 5.6参照 |
| `name` | はい | string | 共通の名前規則 |
| `memo` | いいえ | string | 共通のメモ規則 |
| `defaultChildState` | はい | string | 3状態のいずれか |
| `children` | はい | array | Asset／Group参照の混在配列 |
| `image` | いいえ | object | 5.8参照 |

Group自身のフィールドは `state` ではなく `defaultChildState` です。
これはGroup内で今後作成するAssetへ継承する既定状態です。インポート済みの各子Assetが持つ `state` を上書きするフィールドではありません。

空Groupは `"children": []` とします。

### 5.5 topologyと表示順

Asset／Group参照は次のどちらかです。

```json
{ "kind": "asset", "localId": "asset-fps" }
```

```json
{ "kind": "group", "localId": "group-main" }
```

`rootChildren` と各Groupの `children` が、階層とAsset／Groupの混在表示順の真実源です。
`assets` と `groups` の配列順は定義表の格納順であり、画面上の階層を決めません。

すべてのAssetとGroupは、次のどちらかへ**ちょうど1回**だけ登場する必要があります。

- `rootChildren`
- いずれか1つのGroupの `children`

次は拒否されます。

- 定義したがtopologyのどこからも参照されない孤立エントリ
- 存在しない `localId` への参照
- `kind` と参照先の種類が一致しない参照
- 同じcontainer内の重複参照
- 同じAsset／Groupを複数の親へ置くこと
- Group自身または祖先を参照する循環
- 上限を超えるGroup階層

深度は次のように数えます。

```text
root直下のGroup = 0
その子Group     = 1
孫Group         = 2
...
最大            = 10
```

最大はroot Groupを含む直列11 Groupです。深度11は拒否されます。

### 5.6 portable `localId`

`localId` はBundle内だけの参照キーです。Workshop IDでもGAM内部IDでもありません。インポート時に新しいGAM内部IDへ置換されます。

- 1～128文字
- 先頭はASCII英数字
- 2文字目以降はASCII英数字、`-`、`_`、`.` のみ
- AssetとGroupを合わせて、大文字・小文字を区別せず一意

安全な例:

```text
asset-fps
asset_001
group.main
g1
```

### 5.7 完全な階層例

以下は画像なしの有効なv4 manifestです。各Workshop IDは構造例なので、実際にはユーザー指定の実在IDへ置き換えてください。

```json
{
  "format": "gam-asset-bundle",
  "version": 4,
  "assets": [
    {
      "localId": "asset-fps",
      "name": "FPS",
      "memo": "FPS向けの固定構成",
      "state": "disabled",
      "membership": {
        "kind": "fixed",
        "addonIds": [
          "104607712"
        ]
      }
    },
    {
      "localId": "asset-weapons",
      "name": "Weapons Smart",
      "state": "disabled",
      "membership": {
        "kind": "smart",
        "rule": {
          "kind": "type",
          "value": "Weapon"
        },
        "snapshotAddonIds": []
      }
    },
    {
      "localId": "asset-loose",
      "name": "Loose Asset",
      "state": "excluded",
      "membership": {
        "kind": "fixed",
        "addonIds": []
      }
    }
  ],
  "groups": [
    {
      "localId": "group-main",
      "name": "Main Group",
      "memo": "メイン構成",
      "defaultChildState": "disabled",
      "children": [
        {
          "kind": "asset",
          "localId": "asset-fps"
        },
        {
          "kind": "group",
          "localId": "group-child"
        }
      ]
    },
    {
      "localId": "group-child",
      "name": "Child Group",
      "defaultChildState": "disabled",
      "children": [
        {
          "kind": "asset",
          "localId": "asset-weapons"
        }
      ]
    }
  ],
  "rootChildren": [
    {
      "kind": "asset",
      "localId": "asset-loose"
    },
    {
      "kind": "group",
      "localId": "group-main"
    }
  ]
}
```

topology:

```text
root
├── Loose Asset
└── Main Group
    ├── FPS
    └── Child Group
        └── Weapons Smart
```

### 5.8 Bundle画像

Bundleでは画像をBase64でmanifestへ埋め込まず、独立したZIPエントリにします。

Asset画像:

```text
images/assets/<localId>.png
```

Group画像:

```text
images/groups/<localId>.png
```

所有者のmanifest定義へ、次のdescriptorを追加します。

```json
"image": {
  "path": "images/assets/asset-fps.png",
  "mediaType": "image/png",
  "sha256": "<画像エントリ生バイト列のSHA-256>"
}
```

- `path` は所有者の種類と `localId` から決まる正確なpathでなければならない
- Asset: `images/assets/<localId>.png`
- Group: `images/groups/<localId>.png`
- `mediaType` は正確に `image/png`
- `sha256` はZIP圧縮後ではなく、画像エントリの展開後生バイト列に対する値
- descriptorと画像エントリは1対1
- 同じ画像エントリの使い回し、未参照画像、参照先なしは不可
- 画像1枚ごとの入力・正規化制限はSingle Assetと同じ

画像総数や非画像データ全体に32 MiBなどの製品上の固定上限はありません。各画像だけが個別の安全上限を受けます。画像はGAMの書き出し時と読み込み時に圧縮・正規化されます。

### 5.9 `manifest.sha256`

`manifest.sha256` は `manifest.json` の**実際の生バイト列**に対するSHA-256です。

- 64 bytesちょうど
- ASCIIの16進数のみ
- 改行なし
- BOMなし
- `SHA256:` などの接頭辞なし
- 大文字hexも照合可能だが、小文字を推奨
- JSONの意味ではなく、空白、インデント、改行、末尾LFを含む最終バイト列をhashする
- `manifest.json` を1文字でも編集したら必ず再計算する
- ZIP圧縮後のバイト列に対するhashではない

`manifest.json` は必ずBOMなしUTF-8にします。GAM公式writerは、インデント付きJSON、末尾LF1個でmanifestを作ります。同じ整形にする必要はありませんが、hash計算後にmanifestを変更してはいけません。

### 5.10 Python標準ライブラリでBundleを作る

次のスクリプトへ5.7の `manifest` を入れれば、有効な画像なしBundleを作れます。

```python
from pathlib import Path
import hashlib
import json
import zipfile

manifest = {
    "format": "gam-asset-bundle",
    "version": 4,
    "assets": [
        {
            "localId": "asset-fps",
            "name": "FPS",
            "state": "disabled",
            "membership": {
                "kind": "fixed",
                "addonIds": ["104607712"],
            },
        }
    ],
    "groups": [],
    "rootChildren": [
        {"kind": "asset", "localId": "asset-fps"},
    ],
}

# BOMなしUTF-8。最終LFを含む、このbytes列そのものをhashする。
manifest_bytes = (
    json.dumps(manifest, ensure_ascii=False, indent=2) + "\n"
).encode("utf-8")
manifest_hash = hashlib.sha256(manifest_bytes).hexdigest().encode("ascii")

output_path = Path("example-bundle.gam")
with zipfile.ZipFile(
    output_path,
    mode="w",
    compression=zipfile.ZIP_DEFLATED,
    compresslevel=9,
    allowZip64=True,
) as archive:
    # 明示的なdirectory entryや追加ファイルは作らない。
    archive.writestr("manifest.json", manifest_bytes)
    archive.writestr(
        "manifest.sha256",
        manifest_hash,
        compress_type=zipfile.ZIP_STORED,
    )

print(output_path.resolve())
```

画像を含める場合は、manifestをbytes化する前にdescriptorを追加します。

```python
image_bytes = Path("fps.png").read_bytes()
image_archive_path = "images/assets/asset-fps.png"

manifest["assets"][0]["image"] = {
    "path": image_archive_path,
    "mediaType": "image/png",
    "sha256": hashlib.sha256(image_bytes).hexdigest(),
}

# この後でmanifest_bytesとmanifest_hashを計算する。
# ZIP作成ブロック内で次も実行する。
archive.writestr(image_archive_path, image_bytes)
```

画像ありBundleの正しい処理順:

```text
画像を確定
→ 各画像のSHA-256を計算
→ descriptorをmanifestへ追加
→ manifestの最終JSON bytesを作る
→ manifest SHA-256を計算
→ ZIPを作る
```

## 6. インポート後の挙動

### 6.1 Steam購読

- `.gam` のプレビューとインポートはSteamの購読・購読解除を行わない
- Fixed Assetの未購読IDは不足中の参照として保持される
- Smart Assetのsnapshotから不足購読一覧を作らない
- `.gam` だけで別PCへAddon本体を配布することはできない

### 6.2 GAM内の作成結果

- Asset／Groupのportable `localId` は保存せず、新しいGAM内部IDを割り当てる
- Single Assetはroot直下のCustom Assetとして作成
- Bundleは `rootChildren` / `children` の階層と混在順を復元
- Bundle内の選択Groupは全サブツリーを含む
- Asset状態、Groupの `defaultChildState`、memo、画像を保存
- Smart Assetは取込先でruleを再評価
- 既存設定との名前衝突は一意名へ調整
- 新規項目はお気に入りOFFで通常領域へ追加
- インポート全体を1回のUndoで戻せる
- Bundleが現在設定より深い場合、確認画面で必要深度を示し、了承後に階層上限を必要値まで引き上げる

### 6.3 GMod状態への影響

Steam購読は変わりませんが、インポート後にはGAMのdesired-state再計算が走ります。
そのため、`enabled` や `excluded` のAssetを取り込むと、GModのAddon有効／無効状態へ反映され得ます。

安全な下書きを配布する場合は、各Assetを `disabled` にしておき、受け取ったユーザーが確認後に切り替えられるようにしてください。

## 7. `.gam` に含まれないもの

次の情報はSingle AssetにもBundleにも保存されません。

- Steamの購読命令、購読解除命令
- Addon本体
- GAMのSystem Asset
- お気に入り状態
- Asset／GroupのGAM内部ID
- 履歴／バージョン履歴
- ローカルファイルpath
- ローカルAddon
- GModの現在観測値や状態理由
- UIの選択状態
- ユーザー設定全体

独自フィールドとして追加しても無視されず、ファイル全体が拒否されます。

## 8. 読み込み互換形式

| 形式 | 実体 | 識別 | version | 読込 | 現在の書出 |
|---|---|---|---:|---:|---:|
| Legacy Single v1 | text | `# GAM Collection Export v1` | 1相当 | 可 | 不可 |
| Single v2 | JSON | `gam-asset` | 2 | 可 | 不可 |
| Single v3 | JSON | `gam-asset` | 3 | 可 | 可 |
| 旧Bundle v3 | ZIP | `gam-asset-bundle` | 3 | 可 | 不可 |
| Bundle v4 | ZIP | `gam-asset-bundle` | 4 | 可 | 可 |

未知の将来versionはbest-effortで解釈せず、安全のため拒否されます。

### 8.1 Legacy Single v1

読み込み専用のテキスト形式です。

```text
# GAM Collection Export v1
# Title: Friends FPS
# Count: 2

104607712
1234567890
```

- 最初の空でない行はheaderと完全一致
- `# Title:` は任意。省略時は `Imported Asset`
- `# Count:` は任意。指定時は重複排除前のID行数と一致必須
- 未知の `#` コメントは無視
- 各raw行は最大4096文字
- 重複IDは最初の出現を残して除去
- EnabledのFixed Assetとして読み込む
- memo、画像、Smart、状態、Groupは表現できない

### 8.2 Single v2

Single v3とほぼ同じJSONですが、`asset.memo` を持てません。画像、Fixed、Smart、3状態は扱えます。新規作成でv2を選ぶ理由はありません。

### 8.3 旧Bundle v3

ZIP構造とchecksumはBundle v4と同系統ですが、manifestに次の制限があります。

- `version` は3
- `rootChildren` は禁止
- memoは禁止
- Groupは `children` ではなく `childAssetLocalIds` を持つ
- Group内はAssetだけ
- Group入れ子不可
- root順序は、どのGroupにも属さないAsset、その後にGroupの順で暗黙生成

v3 Group例:

```json
{
  "localId": "group-legacy",
  "name": "Legacy Group",
  "defaultChildState": "excluded",
  "childAssetLocalIds": [
    "asset-grouped"
  ]
}
```

新規作成ではBundle v4を使ってください。

## 9. AI向けプロンプトテンプレート

### 9.1 単一Fixed Assetを作らせる

この文書全体と、次の依頼をAIへ渡してください。

```text
添付した「GAM .gam ファイル形式仕様」に厳密に従い、
Single Asset v3の.gamファイルを1個作成してください。

Asset名: <名前>
状態: disabled
メモ: <不要なら省略>
Workshop ID:
- <実在ID>
- <実在ID>

要件:
- ZIPではなく、BOMなしUTF-8 JSONの.gamにする。
- formatは"gam-asset"、versionは数値3。
- Fixed membershipを使う。
- IDを推測・追加・置換しない。
- 未知フィールド、コメント、画像を追加しない。
- 作成後、JSONを再parseし、ID重複と必須フィールドを検査する。
```

### 9.2 Bundleを作らせる

```text
添付した「GAM .gam ファイル形式仕様」に厳密に従い、
Bundle v4の.gamファイルを1個作成してください。

構成:
- root Asset「<名前>」: Fixed、disabled、Workshop IDは<一覧>
- Group「<名前>」: defaultChildStateはdisabled
  - Asset「<名前>」: Smart type=Weapon、disabled

要件:
- .gamの実体はZIP。
- manifest.json、manifest.sha256以外の不要entryを入れない。
- manifest.jsonはBOMなしUTF-8で作る。UTF-8 BOMを付けない。
- formatは"gam-asset-bundle"、versionは数値4。
- 全Asset／Groupをtopologyへちょうど1回だけ置く。
- localIdと名前をBundle全体で一意にする。
- Workshop IDを推測・追加・置換しない。
- 画像は入れない。
- manifestの最終UTF-8 bytesへSHA-256を計算し、64文字ASCII、改行なしでmanifest.sha256へ入れる。
- 作成後にZIPを再度開き、entry一覧、JSON、checksum、参照整合性を検査する。
```

## 10. 生成後チェックリスト

### Single Asset v3

- [ ] 拡張子が `.gam`
- [ ] ZIPではなくUTF-8 JSON
- [ ] `format` が `gam-asset`
- [ ] `version` が数値3
- [ ] Asset名、状態、membershipが存在
- [ ] Workshop IDが文字列かつ実在ID
- [ ] 同一配列内に重複IDがない
- [ ] FixedとSmartのフィールドが混在していない
- [ ] 画像なしなら `image` 自体がない
- [ ] 未知フィールド、コメント、末尾コンマがない

### Bundle v4

- [ ] 拡張子が `.gam`
- [ ] 実体がZIP
- [ ] `manifest.json` と `manifest.sha256` が各1個
- [ ] `manifest.json` がBOMなしUTF-8
- [ ] `format` が `gam-asset-bundle`
- [ ] `version` が数値4
- [ ] `manifest.sha256` が64 ASCII bytesで改行なし
- [ ] checksumが最終 `manifest.json` bytesと一致
- [ ] 全 `localId` がBundle全体で一意
- [ ] 全名前がBundle全体で一意
- [ ] 全Asset／Groupがtopologyへちょうど1回登場
- [ ] 参照先の種類と `kind` が一致
- [ ] 孤立、複数親、循環、深度超過がない
- [ ] 仕様外のZIP entryがない
- [ ] 画像descriptorと画像entryが1対1

最後はGAMのインポート確認画面で読み込めることを確認してください。ファイルがparseできることと、意図したGMod状態になることは別なので、名前、Asset状態、Fixed／Smart、不足参照、Group階層も確認します。

## 11. 実装上の真実源

この文書は主に次の現行実装と回帰テストから作成されています。

- `src/GmodAddonManager.Core/Models/GamAssetDocument.cs`
- `src/GmodAddonManager.Core/Models/GamAssetBundleDocument.cs`
- `src/GmodAddonManager.Core/Services/GamAssetDocumentCodec.cs`
- `src/GmodAddonManager.Core/Services/GamAssetBundleCodec.cs`
- `src/GmodAddonManager.Core/Services/GamAssetDocumentImageNormalizer.cs`
- `src/GmodAddonManager.Core/Services/GamAssetFileService.cs`
- `src/GmodAddonManager.Core/Services/AddonClassificationService.cs`
- `src/GmodAddonManager.Core/Services/AddonManager.cs`
- `tests/GmodAddonManager.Core.Tests/GamAssetDocumentCodecTests.cs`
- `tests/GmodAddonManager.Core.Tests/GamAssetBundleCodecTests.cs`
- `tests/GmodAddonManager.Core.Tests/GamAssetFileServiceTests.cs`
- `tests/GmodAddonManager.Core.Tests/GamAssetManagerIntegrationTests.cs`
- `tests/GmodAddonManager.Core.Tests/GamAssetBundleManagerIntegrationTests.cs`

形式versionやcodecを変更した場合は、この文書も同時に更新してください。
