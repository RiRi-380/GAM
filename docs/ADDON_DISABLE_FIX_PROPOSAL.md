# Addon Disable Fix Proposal

## Problem Summary
When disabling addons on other PCs, Steam automatically re-downloads them because:
1. GAM only removes files/junctions but doesn't modify Steam's manifest
2. Steam's `appworkshop_4000.acf` still lists the addon as subscribed
3. On next launch, Steam sees "missing" files and re-downloads

## Immediate Fix (Safe)

### 1. Add Steam Process Check Warning
```csharp
// In AddonManager.cs - DisableAddon method
public void DisableAddon(string addonId)
{
    // Add this check at the beginning
    if (!SteamProcessChecker.IsSafeToModifyAddons(out string warning))
    {
        errorHandler.HandleWarning(warning, "DisableAddon");
        // Could throw exception to prevent operation
        // throw new InvalidOperationException(warning);
    }
    
    // Existing disable logic...
}
```

### 2. Create Stub Files Instead of Complete Removal
Instead of completely removing directories, leave minimal stub files:

```csharp
private void DisableAddonSafe(string addonId)
{
    string workshopAddonPath = Path.Combine(workshopPath, addonId);
    
    // Instead of removing completely, create a minimal stub
    if (Directory.Exists(workshopAddonPath))
    {
        // Move actual content to managed folder
        MoveAddonContent(workshopAddonPath, managedPath);
        
        // Leave a stub directory with marker file
        Directory.CreateDirectory(workshopAddonPath);
        File.WriteAllText(
            Path.Combine(workshopAddonPath, ".gam_disabled"), 
            $"Disabled by GAM at {DateTime.Now}"
        );
    }
}
```

## Advanced Fix (Requires Testing)

### 1. ACF File Modification
Create a service to modify Steam's manifest:

```csharp
public class SteamManifestManager
{
    public void RemoveAddonFromManifest(string addonId)
    {
        string acfPath = GetAcfFilePath();
        if (!File.Exists(acfPath)) return;
        
        // Parse VDF format
        var content = File.ReadAllText(acfPath);
        
        // Remove addon entry from WorkshopItemsInstalled section
        // This is risky - Steam may overwrite our changes
        
        // Backup original
        File.Copy(acfPath, acfPath + ".bak", true);
        
        // Modify and save
        // ... VDF parsing and modification logic
    }
}
```

### 2. Steam Workshop API Unsubscribe
Use official Steam API to unsubscribe (most reliable):

```csharp
public async Task UnsubscribeAddon(string addonId)
{
    if (!ulong.TryParse(addonId, out ulong workshopId))
        return;
    
    var call = SteamUGC.UnsubscribeItem(new PublishedFileId_t(workshopId));
    var result = await call.GetAwaiter();
    
    if (result.m_eResult == EResult.k_EResultOK)
    {
        // Successfully unsubscribed
        // Now safe to remove files
    }
}
```

## Recommended Implementation Order

1. **Phase 1** (Immediate): Add Steam process checking and warnings
2. **Phase 2** (Short-term): Implement stub file approach
3. **Phase 3** (Long-term): Test ACF modification or API unsubscribe

## UI Changes Needed

Add a warning dialog when Steam is running:
```
⚠️ Steam is currently running

Disabling addons while Steam is running may cause them to be 
automatically re-downloaded when you start Garry's Mod.

For best results:
1. Close Garry's Mod
2. Close Steam completely
3. Disable addons in GAM
4. Restart Steam

[Continue Anyway] [Cancel]
```

## Testing Plan

1. Test with Steam closed completely
2. Test with Steam running but Gmod closed  
3. Test with both Steam and Gmod running
4. Monitor `appworkshop_4000.acf` changes
5. Check if stub files prevent re-download