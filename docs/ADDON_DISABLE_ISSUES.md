# GAM Addon Disable Issues Analysis

## Root Cause: Steam Workshop Manifest Desynchronization

### Primary Issue
GAM only performs filesystem-level operations (removing junctions, hard links) when disabling addons, but does NOT modify Steam's `appworkshop_4000.acf` manifest file.

**Result**: Steam still considers the addon as subscribed and re-downloads it when it detects "missing" files.

## Identified Failure Patterns

### 1. Steam Manifest File Issue (Primary)
- **Problem**: `appworkshop_4000.acf` still lists disabled addons
- **Location**: `{SteamPath}/steamapps/workshop/appworkshop_4000.acf`
- **Impact**: Steam auto-downloads "missing" subscribed addons
- **Solution Options**:
  - Modify ACF file when disabling addons (risky - Steam may overwrite)
  - Use Steam Workshop API to properly unsubscribe
  - Create "placeholder" files to prevent re-download

### 2. Junction vs Directory Detection
- **Problem**: Steam may create real directories after GAM removes junctions
- **Current Handling**: JunctionService.cs handles this by backing up existing directories
- **Risk**: Race condition between GAM operations and Steam file monitoring

### 3. GMA vs Directory Addon Handling
- **GMA Addons**: 
  - Stored in `/garrysmod/cache/`
  - GAM removes hard links from cache
  - Steam may regenerate `.cache` files
- **Directory Addons**:
  - Stored in `/workshop/content/4000/`
  - GAM uses junctions to `.addon-manager/`
  - More complex to manage

### 4. Steam File Monitoring
- **Problem**: Steam actively monitors workshop directories
- **Impact**: May instantly recreate deleted/moved files
- **Timing**: Operations during Steam runtime more likely to fail

### 5. Permission/Ownership Issues
- **Admin Rights**: Junctions require admin privileges
- **File Ownership**: Steam-created files may have different ownership
- **UAC**: Different behavior with/without elevation

### 6. Steam Cloud Sync
- **Problem**: Steam Cloud may restore addon subscriptions
- **Impact**: Disabled addons re-enabled after Steam restart
- **Workaround**: Disable Steam Cloud for Garry's Mod

### 7. Multiple Steam Library Locations
- **Problem**: Workshop content can be on different drives
- **Impact**: Hard links fail across drives (GAM falls back to file move)
- **Current Handling**: `AreSameDrive()` check in AddonManager.cs

### 8. Concurrent Access
- **Problem**: Steam and GAM accessing same files
- **Impact**: File lock errors, incomplete operations
- **Example**: "The process cannot access the file because it is being used by another process"

## Recommended Solutions

### Short-term (Safe)
1. **Add Steam Running Check**:
   ```csharp
   if (SteamAPI.IsSteamRunning()) {
       // Warn user to close Steam before disabling addons
   }
   ```

2. **Create Placeholder Files**:
   - Keep minimal stub files to prevent Steam re-download
   - Empty directories with `.gam_disabled` marker

3. **Improved Error Messages**:
   - Clearly indicate when Steam is interfering
   - Suggest closing Steam/Gmod before operations

### Long-term (Requires Testing)
1. **ACF File Modification**:
   - Parse and modify `appworkshop_4000.acf`
   - Remove entries for disabled addons
   - Risk: Steam may overwrite changes

2. **Steam Workshop API Integration**:
   - Use proper Steam API to unsubscribe
   - Requires user authentication
   - Most reliable but complex

3. **File System Watcher**:
   - Monitor for Steam recreating files
   - Re-apply disable operations if needed
   - Performance overhead

## Current Code Issues Found

1. **DisableGmaAddon**: Only removes files from cache, doesn't prevent Steam from re-downloading
2. **RemoveWorkshopAddonStructure**: Deletes directory but Steam recreates it
3. **No ACF file handling**: Core issue - manifest remains unchanged

## Testing Recommendations

1. Test with Steam completely closed
2. Test with different Steam library locations
3. Test with Steam Cloud enabled/disabled
4. Test on systems with/without admin rights
5. Monitor `appworkshop_4000.acf` changes during operations