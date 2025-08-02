# アドオン無効化問題の修正実装

## 実装した修正内容

### 1. Steam起動チェック機能
**ファイル**: `src/GmodAddonManager.Core/Services/SteamProcessChecker.cs`
- Steamプロセスの起動状態を検出
- Garry's Modの起動状態も検出
- ユーザーフレンドリーなステータスメッセージを提供

### 2. スタブファイル方式の実装
**ファイル**: `src/GmodAddonManager.Core/Services/AddonManager.cs`

#### DisableAddon メソッドの改善
```csharp
public void DisableAddon(string addonId)
{
    // Steam起動チェックを追加
    if (SteamProcessChecker.IsSteamRunningViaAPI())
    {
        errorHandler.HandleWarning(
            "Steam is running. Disabled addons may be re-downloaded...",
            "DisableAddon"
        );
    }
    
    // 既存の無効化処理...
    
    // スタブディレクトリを作成
    CreateDisabledStub(workshopPath, addonId);
}
```

#### CreateDisabledStub メソッド
- 無効化したアドオンの場所に最小限のスタブファイルを作成
- `.gam_disabled` マーカーファイル
- 最小限の `addon.json` ファイル
- Steamの再ダウンロードを防ぐ

#### RemoveDisabledStub メソッド
- アドオンを有効化する際にスタブを削除
- GAMのスタブであることを確認してから削除

### 3. UI警告の実装
**ファイル**: `src/GmodAddonManager.UI/ViewModels/AddonGridViewModel.cs`

```csharp
// Steam起動中に無効化する場合の警告
if (SteamProcessChecker.IsSteamRunningViaAPI())
{
    var result = await dialog.ShowConfirmAsync(
        L.Get("Warning.SteamRunningTitle"),
        L.Get("Warning.SteamRunningDisable")
    );
    
    if (!result)
        return;
}
```

### 4. 多言語対応
**ファイル**: 
- `src/GmodAddonManager.UI/Resources/ja-JP.json`
- `src/GmodAddonManager.UI/Resources/en-US.json`

日英両言語で警告メッセージを追加。

## 動作の流れ

1. **アドオン無効化時**:
   - Steam起動状態をチェック
   - 起動中の場合は警告を表示
   - ユーザーが続行を選択したら無効化処理
   - ファイル/ジャンクションを削除後、スタブディレクトリを作成

2. **アドオン有効化時**:
   - スタブディレクトリがあれば削除
   - 通常の有効化処理を実行

## 期待される効果

1. **即時効果**:
   - ユーザーにSteam起動中のリスクを警告
   - 問題を事前に防ぐ

2. **スタブファイル効果**:
   - Steamが「空のディレクトリ」を見つけても再ダウンロードしない
   - 最小限のaddon.jsonでSteamを満足させる

## テスト手順

1. **Steam終了時のテスト**:
   - Steamを完全に終了
   - アドオンを無効化
   - Garry's Modを起動して確認

2. **Steam起動中のテスト**:
   - Steamを起動したまま
   - アドオンを無効化（警告が表示される）
   - 続行してスタブが作成されることを確認

3. **スタブファイルの効果確認**:
   - スタブが存在する状態でGarry's Modを起動
   - 再ダウンロードが発生しないことを確認

## 今後の改善案

1. **ACFファイル編集**（リスクあり）:
   - `appworkshop_4000.acf`を直接編集
   - Steamのマニフェストからアドオンを削除

2. **Steam API統合**（複雑）:
   - 正式なAPIでサブスクライブ解除
   - 最も確実だが実装が複雑

3. **バックグラウンド監視**:
   - Steamがファイルを再作成したら検出
   - 自動的に再無効化

## ビルド結果

✅ ビルド成功（警告はあるがエラーなし）

## 実装ファイル一覧

1. `src/GmodAddonManager.Core/Services/SteamProcessChecker.cs` - 新規作成
2. `src/GmodAddonManager.Core/Services/AddonManager.cs` - 修正
3. `src/GmodAddonManager.Core/Services/JunctionService.cs` - using追加
4. `src/GmodAddonManager.UI/ViewModels/AddonGridViewModel.cs` - 修正
5. `src/GmodAddonManager.UI/Resources/ja-JP.json` - 修正
6. `src/GmodAddonManager.UI/Resources/en-US.json` - 修正