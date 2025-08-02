# GAM アドオン無効化問題 - 詳細分析レポート

## 問題の概要

他のPCでGAMのアドオン無効化機能が正常に動作せず、Steamが無効化されたアドオンを再ダウンロードしてしまう。

## 根本原因

**Steam Workshop マニフェストファイル (`appworkshop_4000.acf`) の非同期**

1. GAMはファイルシステムレベルでのみ操作（ジャンクション/ハードリンク削除）
2. Steamのマニフェストファイルは更新されない
3. Steamは起動時にマニフェストを参照し、「ファイルが欠落」と判断
4. 自動的に「欠落した」アドオンを再ダウンロード

## 発見された失敗パターン

### 1. **マニフェストファイル問題**（主要原因）
- `SteamWorkshopCacheReader.cs`はACFファイルを**読み取るのみ**
- 書き込み機能が実装されていない
- 結果：無効化してもSteamには「サブスクライブ中」として残る

### 2. **ジャンクション処理の問題**
```csharp
// JunctionService.cs line 125-150
// Steamが空のディレクトリを作成した場合の処理あり
if (Directory.GetFileSystemEntries(absoluteJunctionPath).Length == 0)
{
    Directory.Delete(absoluteJunctionPath, false);
}
```
- 既に対策コードはあるが、タイミング問題が残る

### 3. **GMAファイルの処理**
- `DisableGmaAddon`：キャッシュからファイルを削除
- `RemoveWorkshopAddonStructure`：ワークショップ構造を削除
- 問題：Steamが`.cache`ファイルを再生成する可能性

### 4. **プロセス競合**
- Steamが起動中の操作は特に失敗しやすい
- ファイルロックエラーが発生する可能性

## 実装済みの対策

READMEに「Steamの再ダウンロード問題を回避する独自実装」とあるが、現在の実装では不十分：

1. **ハードリンク方式**（GMAファイル用）
2. **ジャンクション方式**（ディレクトリアドオン用）
3. **バックアップ機構**（既存ディレクトリの処理）

## 推奨される解決策

### 即座に実装可能（安全）

#### 1. Steam起動チェック機能の追加
```csharp
// SteamProcessChecker.cs を作成済み
// AddonManager.cs に統合：

public void DisableAddon(string addonId)
{
    // Steam起動チェック
    if (SteamProcessChecker.IsSteamRunningViaAPI())
    {
        errorHandler.HandleWarning(
            "Steam is running. Disabled addons may be re-downloaded. " +
            "For best results, close Steam before disabling addons.",
            "DisableAddon"
        );
        // オプション：処理を中断
        // throw new InvalidOperationException("Please close Steam before disabling addons");
    }
    
    // 既存の処理...
}
```

#### 2. スタブファイル方式
```csharp
private void CreateDisabledStub(string workshopPath, string addonId)
{
    string addonPath = Path.Combine(workshopPath, addonId);
    Directory.CreateDirectory(addonPath);
    
    // マーカーファイルを作成
    File.WriteAllText(
        Path.Combine(addonPath, ".gam_disabled"),
        $"Disabled by GAM at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}"
    );
    
    // 最小限のaddon.jsonを作成（Steamを欺く）
    File.WriteAllText(
        Path.Combine(addonPath, "addon.json"),
        "{\"title\":\"Disabled by GAM\",\"type\":\"tool\",\"tags\":[]}"
    );
}
```

### 中期的解決策（テスト必要）

#### 1. ACFファイル編集
```csharp
public class SteamManifestEditor
{
    public void RemoveAddonFromManifest(string addonId)
    {
        var acfPath = Path.Combine(steamPath, "steamapps/workshop/appworkshop_4000.acf");
        
        // バックアップ作成
        File.Copy(acfPath, $"{acfPath}.gam_backup", true);
        
        // VDFフォーマットの解析と編集
        // WorkshopItemsInstalled セクションから該当IDを削除
        
        // リスク：Steamが変更を上書きする可能性
    }
}
```

#### 2. Steam Workshop API使用
```csharp
// 最も信頼性が高いが、ユーザー認証が必要
var result = await SteamUGC.UnsubscribeItem(new PublishedFileId_t(workshopId));
```

### UIの改善案

```csharp
// 無効化時の警告ダイアログ
if (SteamProcessChecker.IsSteamRunningViaAPI())
{
    var result = await dialogService.ShowWarningAsync(
        "Steam起動中の警告",
        "Steamが起動中です。アドオンを無効化しても、" +
        "Garry's Mod起動時に再ダウンロードされる可能性があります。\n\n" +
        "推奨手順：\n" +
        "1. Garry's Modを終了\n" +
        "2. Steamを完全に終了\n" +
        "3. アドオンを無効化\n" +
        "4. Steamを再起動\n\n" +
        "このまま続行しますか？",
        "続行", "キャンセル"
    );
    
    if (result != DialogResult.Yes) return;
}
```

## テスト手順

1. **Steam完全終了状態**でのテスト
2. **Steam起動中**でのテスト
3. **Gmod起動中**でのテスト
4. `appworkshop_4000.acf`の変更を監視
5. 異なるSteamライブラリ場所でのテスト

## 実装優先度

1. **最優先**：Steam起動チェックと警告表示
2. **高**：スタブファイル方式の実装
3. **中**：ACFファイル編集機能（リスクあり）
4. **低**：Steam API統合（複雑度高）

## 結論

現在の実装は巧妙だが、Steamのマニフェストファイルを考慮していないため、他のPCで問題が発生している。即座に実装可能な解決策（Steam起動チェック＋スタブファイル）で、問題の大部分は解決できるはず。