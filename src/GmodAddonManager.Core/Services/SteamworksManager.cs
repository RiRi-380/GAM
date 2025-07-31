using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Steamworks;
using GmodAddonManager.Core.Models;

namespace GmodAddonManager.Core.Services
{
    /// <summary>
    /// Steamworks SDK経由でSteam IPCを使用して高速にWorkshop情報を取得
    /// gmpublisherと同じ方式でレート制限を回避し、10-30倍高速化
    /// </summary>
    public class SteamworksManager : IDisposable
    {
        private bool _initialized;
        private readonly SemaphoreSlim _callbackSemaphore = new(1, 1);
        private CancellationTokenSource _callbackCancellation = new();
        private Task? _callbackTask;
        
        // CallResultを保持するためのリスト（ガベージコレクションを防ぐ）
        private readonly List<object> _activeCallResults = new();
        
        // Garry's Mod App ID
        private const uint GMOD_APP_ID = 4000;
        
        
        public bool IsInitialized => _initialized;
        
        public class WorkshopItemInfo
        {
            public string Id { get; set; } = "";
            public string Title { get; set; } = "";
            public string PreviewUrl { get; set; } = ""; // CDN直リンク！
            public string Description { get; set; } = "";
            public ulong FileSize { get; set; }
            public ulong TimeUpdated { get; set; }
            public string Author { get; set; } = "";
            public float Score { get; set; }
            public uint Subscriptions { get; set; }
        }
        
        public bool Initialize()
        {
            // ログファイルパスを最初に定義
            var logPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GmodAddonManager", "logs", "steamworks_debug.log"
            );
            
            // 最初の最初にログを出力（ファイルに直接書き込み）
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(logPath));
                System.IO.File.AppendAllText(logPath, $"\n========================================\n");
                System.IO.File.AppendAllText(logPath, $"[{DateTime.Now}] Initialize() method called\n");
                System.IO.File.AppendAllText(logPath, $"[{DateTime.Now}] Thread ID: {System.Threading.Thread.CurrentThread.ManagedThreadId}\n");
                System.IO.File.AppendAllText(logPath, $"[{DateTime.Now}] AppDomain: {AppDomain.CurrentDomain.FriendlyName}\n");
            }
            catch (Exception ex)
            {
                try
                {
                    System.IO.File.AppendAllText(logPath, $"[{DateTime.Now}] Log write error: {ex.Message}\n");
                }
                catch 
                {
                    // Ignore logging errors - non-critical
                }
            }
            
            // === Initializing Steamworks SDK ===
            
            // steam_appid.txtを作成（App ID 4000 = Garry's Mod）
            var appIdFile = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "steam_appid.txt");
            if (!System.IO.File.Exists(appIdFile))
            {
                // Creating steam_appid.txt with GMOD App ID (4000)
                System.IO.File.WriteAllText(appIdFile, "4000");
            }
            else
            {
                var content = System.IO.File.ReadAllText(appIdFile).Trim();
                if (content != "4000")
                {
                    // WARNING: steam_appid.txt contains incorrect App ID, overwriting with 4000
                    System.IO.File.WriteAllText(appIdFile, "4000");
                }
                else
                {
                    // steam_appid.txt correctly contains '4000'
                }
            }
            
            // 環境変数も設定
            Environment.SetEnvironmentVariable("SteamAppId", "4000");
            Environment.SetEnvironmentVariable("SteamGameId", "4000");
            // Set environment variables SteamAppId and SteamGameId to 4000
            
            // DLLの存在確認
            var dllPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "steam_api64.dll");
            if (!System.IO.File.Exists(dllPath))
            {
                // ERROR: steam_api64.dll not found - Steamworks SDK will not be available
                return false;
            }
            else
            {
                var fileInfo = new System.IO.FileInfo(dllPath);
            }
            
            // RestartAppIfNecessaryでApp ID 4000で再起動を試みる
            // 注意: Steamから起動している場合は、RestartAppIfNecessaryが誤動作することがあるため
            // 環境変数またはsteam_appid.txtでApp IDが設定されている場合はスキップ
            bool skipRestart = false;
            
            // 環境変数をチェック
            var envAppId = Environment.GetEnvironmentVariable("SteamAppId");
            if (envAppId == "4000")
            {
                // SteamAppId environment variable is already set to 4000
                skipRestart = true;
            }
            
            if (!skipRestart)
            {
                try
                {
                    if (SteamAPI.RestartAppIfNecessary(new AppId_t(4000)))
                    {
                        // RestartAppIfNecessary returned true - restarting through Steam
                        // アプリケーションを終了（Steamが再起動してくれる）
                        Environment.Exit(0);
                        return false;
                    }
                    else
                    {
                        // RestartAppIfNecessary returned false - already running correctly
                    }
                }
                catch (Exception ex)
                {
                    // RestartAppIfNecessary failed, continuing with direct initialization
                }
            }
            
            int retryCount = 0;
            const int maxRetries = 3; // リトライ回数を減らす
            
            while (retryCount < maxRetries)
            {
                try
                {
                    // Initialization attempt
                    
                    // Steam APIを初期化
                    _initialized = SteamAPI.Init();
                    
                    if (_initialized)
                    {
                        // App IDを確認
                        var appId = SteamUtils.GetAppID();
                        // === Steamworks initialized ===
                        // Current App ID: {appId.m_AppId}
                        // Expected App ID: 4000 (Garry's Mod)
                        
                        // ファイルにも記録
                        try
                        {
                            System.IO.File.AppendAllText(logPath, $"[{DateTime.Now}] SteamAPI.Init() returned true\n");
                            System.IO.File.AppendAllText(logPath, $"[{DateTime.Now}] Current App ID: {appId.m_AppId}\n");
                            // Hash Steam ID for privacy - only log first 4 chars of the hash
                            var steamId = SteamUser.GetSteamID();
                            var hashedId = System.Security.Cryptography.SHA256.Create()
                                .ComputeHash(System.Text.Encoding.UTF8.GetBytes(steamId.ToString()))
                                .Take(2)
                                .Select(b => b.ToString("x2"))
                                .Aggregate((a, b) => a + b);
                            System.IO.File.AppendAllText(logPath, $"[{DateTime.Now}] Steam User (hashed): {hashedId}...\n");
                            System.IO.File.AppendAllText(logPath, $"[{DateTime.Now}] Is Steam Running: {SteamAPI.IsSteamRunning()}\n");
                        }
                        catch 
                        {
                            // Ignore logging errors - non-critical
                        }
                        
                        // 警告: App ID 4000でない場合
                        if (appId.m_AppId != 4000)
                        {
                            // ERROR: Wrong App ID - Workshop features will not work properly
                            
                            try
                            {
                                System.IO.File.AppendAllText(logPath, $"[{DateTime.Now}] *** ERROR: WRONG APP ID: {appId.m_AppId} ***\n");
                            }
                            catch 
                            {
                                // Ignore logging errors - non-critical
                            }
                        }
                        else
                        {
                            // ✓ Running with correct App ID 4000 - Workshop features enabled!
                            try
                            {
                                System.IO.File.AppendAllText(logPath, $"[{DateTime.Now}] ✓ Running with correct App ID 4000\n");
                            }
                            catch 
                            {
                                // Ignore logging errors - non-critical
                            }
                        }
                        
                        // コールバック処理を開始
                        StartCallbackThread();
                        
                        // Rich Presenceを設定（gmpublisherと同じ）
                        // これによりSteamで「Garry's Modをプレイ中」と表示される
                        try
                        {
                            bool displaySet = SteamFriends.SetRichPresence("steam_display", "#Status_Generic");
                            bool genericSet = SteamFriends.SetRichPresence("generic", "In Gmod Addon Manager");
                            
                            // 現在のRich Presenceを確認
                            var currentDisplay = SteamFriends.GetFriendRichPresence(SteamUser.GetSteamID(), "steam_display");
                            var currentGeneric = SteamFriends.GetFriendRichPresence(SteamUser.GetSteamID(), "generic");
                        }
                        catch (Exception ex)
                        {
                        }
                        
                        // ✓ Steamworks initialized successfully
                        return true;
                    }
                    else
                    {
                        // SteamAPI.Init returned false - Steam client may not be running
                    }
                }
                catch (DllNotFoundException ex)
                {
                    // DLL not found - Make sure Visual C++ Redistributables are installed
                    return false; // DLLが見つからない場合はリトライしない
                }
                catch (Exception ex)
                {
                    // Initialization error
                }
                
                retryCount++;
                if (retryCount < maxRetries)
                {
                    // Retrying in 1 second...
                    System.Threading.Thread.Sleep(1000); // 1秒待つ
                }
            }
            
            // Failed to initialize after maximum retries
            _initialized = false;
            return false;
        }
        
        private void StartCallbackThread()
        {
            // gmpublisherと同じwatchdogスタイルの実装
            _callbackTask = Task.Run(async () =>
            {
                
                while (!_callbackCancellation.Token.IsCancellationRequested)
                {
                    try
                    {
                        // gmpublisherのようにセマフォを使わずに直接RunCallbacks
                        await _callbackSemaphore.WaitAsync(_callbackCancellation.Token);
                        try
                        {
                            SteamAPI.RunCallbacks();
                        }
                        finally
                        {
                            _callbackSemaphore.Release();
                        }
                        
                        // 50ms待機（gmpublisherと同じ）
                        await Task.Delay(50, _callbackCancellation.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // 正常なキャンセル
                        break;
                    }
                    catch (Exception ex)
                    {
                        // DLLエラーやSteam切断の場合は終了
                        if (ex is DllNotFoundException || ex.Message.Contains("Steam"))
                        {
                            _initialized = false;
                            break;
                        }
                        
                        // それ以外のエラーは継続
                        await Task.Delay(50, _callbackCancellation.Token);
                    }
                }
            }, _callbackCancellation.Token);
        }
        
        /// <summary>
        /// 高速バッチ取得 - 最大50件を一括取得（Web APIより10倍以上高速）
        /// </summary>
        public async Task<List<WorkshopItemInfo>> GetWorkshopItemsBatchAsync(List<string> workshopIds)
        {
            return await GetWorkshopItemsBatchAsync(workshopIds, 0, 50);
        }
        
        /// <summary>
        /// 高速バッチ取得（ページング対応版）
        /// </summary>
        public async Task<List<WorkshopItemInfo>> GetWorkshopItemsBatchAsync(List<string> workshopIds, int offset, int limit)
        {
            
            if (!_initialized)
            {
                return new List<WorkshopItemInfo>();
            }
            if (workshopIds.Count == 0)
                return new List<WorkshopItemInfo>();
            
            // offsetとlimitの調整
            limit = Math.Min(limit, 50); // Steam APIの制限
            var actualIds = workshopIds.Skip(offset).Take(limit).ToList();
            if (actualIds.Count == 0)
                return new List<WorkshopItemInfo>();
            
            var results = new List<WorkshopItemInfo>();
            var tcs = new TaskCompletionSource<bool>();
            
            try
            {
            
            // PublishedFileId_t配列に変換
            var fileIds = new PublishedFileId_t[actualIds.Count];
            for (int i = 0; i < fileIds.Length; i++)
            {
                if (ulong.TryParse(actualIds[i], out var id))
                {
                    fileIds[i] = new PublishedFileId_t(id);
                }
                else
                {
                }
            }
            
            // UGCクエリを作成
            var query = SteamUGC.CreateQueryUGCDetailsRequest(fileIds, (uint)fileIds.Length);
            
            // 詳細情報を要求
            SteamUGC.SetReturnLongDescription(query, true);
            SteamUGC.SetReturnMetadata(query, true);
            SteamUGC.SetReturnChildren(query, true);
            SteamUGC.SetReturnAdditionalPreviews(query, true);
            SteamUGC.SetReturnTotalOnly(query, false);
            SteamUGC.SetReturnPlaytimeStats(query, 0);
            SteamUGC.SetReturnKeyValueTags(query, true);
            
            // デバッグ：現在のApp IDを確認
            var currentAppId = SteamUtils.GetAppID();
            
            // 非同期クエリ送信
            var apiCall = SteamUGC.SendQueryUGCRequest(query);
            
            // コールバックをセットアップ（ガベージコレクションを防ぐため保持）
            CallResult<SteamUGCQueryCompleted_t>? callResult = null;
            callResult = CallResult<SteamUGCQueryCompleted_t>.Create((result, failure) =>
            {
                
                if (!failure && result.m_eResult == EResult.k_EResultOK)
                {
                    
                    // 結果を処理
                    for (uint i = 0; i < result.m_unNumResultsReturned; i++)
                    {
                        if (SteamUGC.GetQueryUGCResult(query, i, out var details))
                        {
                            var item = new WorkshopItemInfo
                            {
                                Id = details.m_nPublishedFileId.ToString(),
                                Title = details.m_rgchTitle,
                                Description = details.m_rgchDescription,
                                FileSize = (ulong)details.m_nFileSize,
                                TimeUpdated = (ulong)details.m_rtimeUpdated,
                                Score = details.m_flScore,
                                Subscriptions = 0 // 後でSteam APIから取得
                            };
                            
                            // 作者名を取得
                            item.Author = SteamFriends.GetFriendPersonaName(new CSteamID(details.m_ulSteamIDOwner));
                            
                            // プレビューURL（CDN直リンク）を取得 - これが最速の秘密！
                            if (SteamUGC.GetQueryUGCPreviewURL(query, i, out var previewUrl, 1024))
                            {
                                item.PreviewUrl = previewUrl; // https://steamusercontent-a.akamaihd.net/...
                                
                                // URLが空でないか確認
                                if (string.IsNullOrEmpty(previewUrl))
                                {
                                }
                            }
                            else
                            {
                            }
                            
                            results.Add(item);
                        }
                    }
                }
                else
                {
                    // Query failed - check if running with App ID 4000
                }
                
                // クエリハンドルを解放
                SteamUGC.ReleaseQueryUGCRequest(query);
                
                // CallResultをクリーンアップ
                if (callResult != null)
                    _activeCallResults.Remove(callResult);
                
                tcs.SetResult(true);
            });
            
            // CallResultを保持（ガベージコレクションを防ぐ）
            _activeCallResults.Add(callResult);
            
            callResult.Set(apiCall);
            
            // コールバックスレッドが既に動いているので、それに任せる
            var startTime = DateTime.Now;
            var timeout = TimeSpan.FromSeconds(10);
            
            // コールバックスレッドがRunCallbacksを呼ぶのを待つだけ
            while (!tcs.Task.IsCompleted && DateTime.Now - startTime < timeout)
            {
                await Task.Delay(50);
            }
            
            if (!tcs.Task.IsCompleted)
            {
                // Query timeout - callback was not received
            }
            else
            {
                await tcs.Task; // 結果を待つ
            }
            
            return results;
            }
            catch (Exception ex)
            {
                return new List<WorkshopItemInfo>();
            }
        }
        
        /// <summary>
        /// 単一アイテムの詳細取得（超高速）
        /// </summary>
        public async Task<WorkshopItemInfo?> GetWorkshopItemAsync(string workshopId)
        {
            var results = await GetWorkshopItemsBatchAsync(new List<string> { workshopId });
            return results.Count > 0 ? results[0] : null;
        }
        
        /// <summary>
        /// サブスクライブ済みアイテムのID一覧を取得
        /// </summary>
        public List<string> GetSubscribedItems()
        {
            var items = new List<string>();
            
            if (!_initialized)
                return items;
            
            var count = SteamUGC.GetNumSubscribedItems();
            if (count == 0)
                return items;
            
            var fileIds = new PublishedFileId_t[count];
            var actualCount = SteamUGC.GetSubscribedItems(fileIds, count);
            
            for (uint i = 0; i < actualCount; i++)
            {
                items.Add(fileIds[i].m_PublishedFileId.ToString());
            }
            
            return items;
        }
        
        /// <summary>
        /// アイテムのダウンロード状態を確認
        /// </summary>
        public bool IsItemInstalled(string workshopId)
        {
            if (!_initialized || !ulong.TryParse(workshopId, out var id))
                return false;
            
            var fileId = new PublishedFileId_t(id);
            return (SteamUGC.GetItemState(fileId) & (uint)EItemState.k_EItemStateInstalled) != 0;
        }
        
        /// <summary>
        /// アイテムのインストールパスを取得
        /// </summary>
        public string? GetItemInstallPath(string workshopId)
        {
            if (!_initialized || !ulong.TryParse(workshopId, out var id))
                return null;
            
            var fileId = new PublishedFileId_t(id);
            if (SteamUGC.GetItemInstallInfo(fileId, out var size, out var folder, 1024, out var timestamp))
            {
                return folder;
            }
            
            return null;
        }
        
        /// <summary>
        /// コレクション情報を取得（コレクションに含まれるアドオン一覧を含む）
        /// </summary>
        public async Task<CollectionInfo?> GetCollectionInfoAsync(string collectionId)
        {
            
            if (!_initialized || !ulong.TryParse(collectionId, out var id))
                return null;
            
            var tcs = new TaskCompletionSource<CollectionInfo?>();
            var result = new CollectionInfo { Id = collectionId };
            
            try
            {
                // まずコレクション自体の情報を取得
                var fileIds = new PublishedFileId_t[] { new PublishedFileId_t(id) };
                var query = SteamUGC.CreateQueryUGCDetailsRequest(fileIds, 1);
                
                // コレクションの子アイテム（含まれるアドオン）も取得するよう設定
                SteamUGC.SetReturnChildren(query, true);
                SteamUGC.SetReturnLongDescription(query, true);
                SteamUGC.SetReturnMetadata(query, true);
                
                var apiCall = SteamUGC.SendQueryUGCRequest(query);
                
                CallResult<SteamUGCQueryCompleted_t>? callResult = null;
                callResult = CallResult<SteamUGCQueryCompleted_t>.Create((queryResult, failure) =>
                {
                    if (!failure && queryResult.m_eResult == EResult.k_EResultOK && queryResult.m_unNumResultsReturned > 0)
                    {
                        if (SteamUGC.GetQueryUGCResult(query, 0, out var details))
                        {
                            // コレクションかどうかを確認（EWorkshopFileType.k_EWorkshopFileTypeCollection）
                            if (details.m_eFileType == EWorkshopFileType.k_EWorkshopFileTypeCollection)
                            {
                                result.Title = details.m_rgchTitle;
                                result.Description = details.m_rgchDescription;
                                
                                // プレビューURL取得
                                if (SteamUGC.GetQueryUGCPreviewURL(query, 0, out var previewUrl, 1024))
                                {
                                    result.PreviewUrl = previewUrl;
                                }
                                
                                // コレクションに含まれるアドオンIDを取得
                                var childCount = (uint)details.m_unNumChildren;
                                if (childCount > 0)
                                {
                                    var childIds = new PublishedFileId_t[childCount];
                                    if (SteamUGC.GetQueryUGCChildren(query, 0, childIds, childCount))
                                    {
                                        result.AddonIds = childIds.Select(x => x.m_PublishedFileId.ToString()).ToList();
                                    }
                                }
                                
                                tcs.SetResult(result);
                            }
                            else
                            {
                                tcs.SetResult(null);
                            }
                        }
                    }
                    else
                    {
                        tcs.SetResult(null);
                    }
                    
                    SteamUGC.ReleaseQueryUGCRequest(query);
                    if (callResult != null)
                        _activeCallResults.Remove(callResult);
                });
                
                _activeCallResults.Add(callResult);
                callResult.Set(apiCall);
                
                // コールバック待機
                var startTime = DateTime.Now;
                var timeout = TimeSpan.FromSeconds(10);
                
                // コールバックスレッドがRunCallbacksを呼ぶのを待つだけ
                while (!tcs.Task.IsCompleted && DateTime.Now - startTime < timeout)
                {
                    await Task.Delay(50);
                }
                
                return await tcs.Task;
            }
            catch (Exception ex)
            {
                return null;
            }
        }
        
        /// <summary>
        /// アドオンの状態を確認
        /// </summary>
        public uint GetItemState(string workshopId)
        {
            if (!_initialized || !ulong.TryParse(workshopId, out var id))
                return 0;
            
            var fileId = new PublishedFileId_t(id);
            return SteamUGC.GetItemState(fileId);
        }
        
        /// <summary>
        /// Steam URLスキーム経由でサブスクライブ（フォールバック用）
        /// </summary>
        public void SubscribeViaUrlScheme(string workshopId)
        {
            try
            {
                var url = $"steam://url/CommunityFilePage/{workshopId}";
                // Opening Steam URL
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                // Failed to open Steam URL
            }
        }
        
        /// <summary>
        /// アドオンをサブスクライブ
        /// </summary>
        public async Task<bool> SubscribeItemAsync(string workshopId)
        {
            // SubscribeItemAsync called
            
            if (!_initialized)
            {
                // ERROR: Not initialized
                return false;
            }
            
            if (!ulong.TryParse(workshopId, out var id))
            {
                // ERROR: Invalid workshop ID format
                return false;
            }
            
            // 現在のApp IDを確認
            var currentAppId = SteamUtils.GetAppID();
            // Current App ID should be 4000 for Garry's Mod
            
            // App IDが4000でない場合は警告を出すが続行
            if (currentAppId.m_AppId != 4000)
            {
                // WARNING: Not running with App ID 4000
            }
            
            var tcs = new TaskCompletionSource<bool>();
            var fileId = new PublishedFileId_t(id);
            
            // サブスクライブ前の状態を確認
            var stateBefore = SteamUGC.GetItemState(fileId);
            // Check item state before subscribe
            
            // 既にサブスクライブ済みの場合
            if ((stateBefore & (uint)EItemState.k_EItemStateSubscribed) != 0)
            {
                // Item is already subscribed
                return true;
            }
            
            // Calling SteamUGC.SubscribeItem
            var apiCall = SteamUGC.SubscribeItem(fileId);
            // API call started
            
            // APIコール直後に即座に状態をチェック（一部のケースでは即座に反映される）
            await Task.Delay(100); // 短い待機
            var immediateState = SteamUGC.GetItemState(fileId);
            if ((immediateState & (uint)EItemState.k_EItemStateSubscribed) != 0)
            {
                // ✓ Item immediately subscribed!
                return true;
            }
            
            // 構造体のマーシャリング問題を回避するため、ポーリングベースのアプローチを使用
            // RemoteStorageSubscribePublishedFileResult_tのコールバックが信頼できないため
            CallResult<RemoteStorageSubscribePublishedFileResult_t>? callResult = null;
            callResult = CallResult<RemoteStorageSubscribePublishedFileResult_t>.Create((result, failure) =>
            {
                // Subscribe callback received
                
                // Check struct fields
                var receivedId = result.m_nPublishedFileId.m_PublishedFileId;
                
                // 構造体のマーシャリング問題を検出
                if ((int)result.m_eResult == (int)id || receivedId == 576460752303423489)
                {
                    // WARNING: Struct marshalling issue detected - known issue with Steamworks.NET 20.2.0
                    // Falling back to polling-based approach
                    
                    // コールバックを無視して、ポーリングベースで成功を判定
                    tcs.SetResult(false); // 構造体問題の場合はfalseを返す
                }
                else
                {
                    // 通常の処理
                    // エラーコードの詳細を先に出力
                    if (result.m_eResult != EResult.k_EResultOK)
                    {
                        string errorDetail = result.m_eResult switch
                        {
                            EResult.k_EResultInsufficientPrivilege => "Insufficient privilege (user doesn't own the game?)",
                            EResult.k_EResultTimeout => "Request timed out",
                            EResult.k_EResultNotLoggedOn => "Not logged on to Steam",
                            EResult.k_EResultServiceUnavailable => "Steam service unavailable",
                            EResult.k_EResultInvalidParam => "Invalid parameter",
                            EResult.k_EResultAccessDenied => "Access denied",
                            EResult.k_EResultFail => "Generic failure",
                            EResult.k_EResultBusy => "Steam is busy",
                            EResult.k_EResultDuplicateRequest => "Duplicate request",
                            EResult.k_EResultAlreadyOwned => "Already subscribed",
                            EResult.k_EResultNotModified => "Not modified",
                            _ => $"Unknown error code: {(int)result.m_eResult}"
                        };
                        // Error detail: {errorDetail}
                    }
                    
                    // 成功判定
                    if (!failure && result.m_eResult == EResult.k_EResultOK)
                    {
                        // ✓ Successfully subscribed
                        tcs.SetResult(true);
                    }
                    else
                    {
                        // ERROR: Failed to subscribe
                        tcs.SetResult(false);
                    }
                }
                
                if (callResult != null)
                    _activeCallResults.Remove(callResult);
            });
            
            _activeCallResults.Add(callResult);
            callResult.Set(apiCall);
            
            // コールバック待機 - コールバックスレッドに任せる
            var startTime = DateTime.Now;
            var timeout = TimeSpan.FromSeconds(30); // タイムアウトを30秒に増やす
            // Waiting for subscribe callback
            
            // コールバックスレッドがRunCallbacksを呼ぶのを待つだけ
            while (!tcs.Task.IsCompleted && DateTime.Now - startTime < timeout)
            {
                await Task.Delay(100); // 100msごとにチェック
            }
            
            if (!tcs.Task.IsCompleted)
            {
                // ERROR: Subscribe request timed out
                return false;
            }
            
            var callbackResult = await tcs.Task;
            
            // コールバックの結果に関わらず、ポーリングベースで状態を確認
            // Performing polling-based verification
            
            // 最大10秒間、1秒ごとに状態をチェック
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(1000); // 1秒待機
                
                var stateAfter = SteamUGC.GetItemState(fileId);
                // Console.WriteLine($"[SteamworksManager] Polling {i+1}/10 - Item state: 0x{stateAfter:X8}");
                
                if ((stateAfter & (uint)EItemState.k_EItemStateSubscribed) != 0)
                {
                    // Console.WriteLine($"[SteamworksManager] ✓ Item {workshopId} is now subscribed!");
                    // Console.WriteLine($"[SteamworksManager] Is downloading: {(stateAfter & (uint)EItemState.k_EItemStateDownloading) != 0}");
                    // Console.WriteLine($"[SteamworksManager] Is installed: {(stateAfter & (uint)EItemState.k_EItemStateInstalled) != 0}");
                    // Console.WriteLine($"[SteamworksManager] SubscribeItemAsync completed successfully (via polling)");
                    return true;
                }
            }
            
            // ポーリングでも確認できなかった場合
            // Console.WriteLine($"[SteamworksManager] *** WARNING: Could not verify subscription status for {workshopId}");
            // Console.WriteLine($"[SteamworksManager] The subscribe request may have failed or is still processing.");
            // Console.WriteLine($"[SteamworksManager] SubscribeItemAsync completed with result: {callbackResult}");
            return callbackResult;
        }
        
        /// <summary>
        /// 複数のアドオンを一括サブスクライブ
        /// </summary>
        public async Task<Dictionary<string, bool>> SubscribeItemsBatchAsync(List<string> workshopIds, IProgress<(int current, int total)>? progress = null)
        {
            var results = new Dictionary<string, bool>();
            int current = 0;
            int total = workshopIds.Count;
            
            foreach (var workshopId in workshopIds)
            {
                progress?.Report((current, total));
                
                var success = await SubscribeItemAsync(workshopId);
                results[workshopId] = success;
                
                current++;
                
                // レート制限を避けるため少し待機
                await Task.Delay(100);
            }
            
            progress?.Report((total, total));
            return results;
        }
        
        /// <summary>
        /// アドオンのサブスクライブを解除
        /// </summary>
        public async Task<bool> UnsubscribeItemAsync(string workshopId)
        {
            // Console.WriteLine($"[SteamworksManager] UnsubscribeItemAsync called for {workshopId}");
            
            if (!_initialized)
            {
                // Console.WriteLine($"[SteamworksManager] ERROR: Not initialized");
                return false;
            }
            
            if (!ulong.TryParse(workshopId, out var id))
            {
                // Console.WriteLine($"[SteamworksManager] ERROR: Invalid workshop ID format: {workshopId}");
                return false;
            }
            
            var tcs = new TaskCompletionSource<bool>();
            var fileId = new PublishedFileId_t(id);
            
            // サブスクライブ解除前の状態を確認
            var stateBefore = SteamUGC.GetItemState(fileId);
            // Console.WriteLine($"[SteamworksManager] Item state before unsubscribe: 0x{stateBefore:X8}");
            
            // 既にサブスクライブ解除済みの場合
            if ((stateBefore & (uint)EItemState.k_EItemStateSubscribed) == 0)
            {
                // Console.WriteLine($"[SteamworksManager] Item {workshopId} is already unsubscribed");
                return true;
            }
            
            // Console.WriteLine($"[SteamworksManager] Calling SteamUGC.UnsubscribeItem");
            var apiCall = SteamUGC.UnsubscribeItem(fileId);
            // Console.WriteLine($"[SteamworksManager] API call started");
            
            // APIコール直後に即座に状態をチェック
            await Task.Delay(100);
            var immediateState = SteamUGC.GetItemState(fileId);
            if ((immediateState & (uint)EItemState.k_EItemStateSubscribed) == 0)
            {
                // Console.WriteLine($"[SteamworksManager] ✓ Item immediately unsubscribed!");
                return true;
            }
            
            // コールバック設定
            CallResult<RemoteStorageUnsubscribePublishedFileResult_t>? callResult = null;
            callResult = CallResult<RemoteStorageUnsubscribePublishedFileResult_t>.Create((result, failure) =>
            {
                // Console.WriteLine($"[SteamworksManager] Unsubscribe callback received");
                // Console.WriteLine($"[SteamworksManager] Failure: {failure}, Result: {result.m_eResult}");
                // Console.WriteLine($"[SteamworksManager] Published File ID in result: {result.m_nPublishedFileId}");
                
                // 構造体のマーシャリング問題を検出
                var receivedId = result.m_nPublishedFileId.m_PublishedFileId;
                if ((int)result.m_eResult == (int)id || receivedId == 576460752303423489)
                {
                    // Console.WriteLine($"[SteamworksManager] WARNING: Struct marshalling issue detected");
                    tcs.SetResult(false);
                }
                else
                {
                    if (!failure && result.m_eResult == EResult.k_EResultOK)
                    {
                        // Console.WriteLine($"[SteamworksManager] ✓ Successfully unsubscribed via callback");
                        tcs.SetResult(true);
                    }
                    else
                    {
                        // Console.WriteLine($"[SteamworksManager] ERROR: Failed to unsubscribe via callback");
                        tcs.SetResult(false);
                    }
                }
                
                if (callResult != null)
                    _activeCallResults.Remove(callResult);
            });
            
            _activeCallResults.Add(callResult);
            callResult.Set(apiCall);
            
            // コールバック待機
            var startTime = DateTime.Now;
            var timeout = TimeSpan.FromSeconds(30);
            // Console.WriteLine($"[SteamworksManager] Waiting for unsubscribe callback");
            
            while (!tcs.Task.IsCompleted && DateTime.Now - startTime < timeout)
            {
                await Task.Delay(100);
            }
            
            if (!tcs.Task.IsCompleted)
            {
                // Console.WriteLine($"[SteamworksManager] ERROR: Unsubscribe request timed out");
                return false;
            }
            
            var callbackResult = await tcs.Task;
            
            // コールバックの結果に関わらず、ポーリングベースで状態を確認
            // Console.WriteLine($"[SteamworksManager] Performing polling-based verification");
            
            // 最大10秒間、1秒ごとに状態をチェック
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(1000);
                
                var stateAfter = SteamUGC.GetItemState(fileId);
                // Console.WriteLine($"[SteamworksManager] Polling {i+1}/10 - Item state: 0x{stateAfter:X8}");
                
                if ((stateAfter & (uint)EItemState.k_EItemStateSubscribed) == 0)
                {
                    // Console.WriteLine($"[SteamworksManager] ✓ Item {workshopId} is now unsubscribed!");
                    // Console.WriteLine($"[SteamworksManager] UnsubscribeItemAsync completed successfully (via polling)");
                    return true;
                }
            }
            
            // Console.WriteLine($"[SteamworksManager] *** WARNING: Could not verify unsubscription status for {workshopId}");
            // Console.WriteLine($"[SteamworksManager] UnsubscribeItemAsync completed with result: {callbackResult}");
            return callbackResult;
        }
        
        public class CollectionInfo
        {
            public string Id { get; set; } = "";
            public string Title { get; set; } = "";
            public string Description { get; set; } = "";
            public string PreviewUrl { get; set; } = "";
            public List<string> AddonIds { get; set; } = new();
        }
        
        /// <summary>
        /// コレクションを作成
        /// </summary>
        public async Task<string?> CreateCollectionAsync(string title, string description = "")
        {
            if (!_initialized)
                return null;
                
            var tcs = new TaskCompletionSource<string?>();
            
            // コレクションタイプでアイテムを作成
            var apiCall = SteamUGC.CreateItem(new AppId_t(4000), EWorkshopFileType.k_EWorkshopFileTypeCollection);
            
            CallResult<CreateItemResult_t>? callResult = null;
            callResult = CallResult<CreateItemResult_t>.Create((result, failure) =>
            {
                if (!failure && result.m_eResult == EResult.k_EResultOK)
                {
                    var collectionId = result.m_nPublishedFileId.ToString();
                    // タイトルと説明を設定
                    var updateHandle = SteamUGC.StartItemUpdate(new AppId_t(4000), result.m_nPublishedFileId);
                    SteamUGC.SetItemTitle(updateHandle, title);
                    SteamUGC.SetItemDescription(updateHandle, description);
                    SteamUGC.SetItemVisibility(updateHandle, ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPublic);
                    
                    // 更新を送信
                    var updateApiCall = SteamUGC.SubmitItemUpdate(updateHandle, "Initial creation");
                    
                    CallResult<SubmitItemUpdateResult_t>? updateCallResult = null;
                    updateCallResult = CallResult<SubmitItemUpdateResult_t>.Create((updateResult, updateFailure) =>
                    {
                        if (!updateFailure && updateResult.m_eResult == EResult.k_EResultOK)
                        {
                            tcs.SetResult(collectionId);
                        }
                        else
                        {
                            tcs.SetResult(null);
                        }
                        
                        if (updateCallResult != null)
                            _activeCallResults.Remove(updateCallResult);
                    });
                    
                    _activeCallResults.Add(updateCallResult);
                    updateCallResult.Set(updateApiCall);
                }
                else
                {
                    tcs.SetResult(null);
                }
                
                if (callResult != null)
                    _activeCallResults.Remove(callResult);
            });
            
            _activeCallResults.Add(callResult);
            callResult.Set(apiCall);
            
            // タイムアウト設定
            var startTime = DateTime.Now;
            var timeout = TimeSpan.FromSeconds(30);
            
            while (!tcs.Task.IsCompleted && DateTime.Now - startTime < timeout)
            {
                await Task.Delay(100);
            }
            
            return tcs.Task.IsCompleted ? await tcs.Task : null;
        }
        
        /// <summary>
        /// コレクションにアドオンを追加/更新
        /// </summary>
        public async Task<bool> UpdateCollectionAsync(string collectionId, List<string> addonIds, string? updateNote = null, IProgress<(int current, int total)>? progress = null, CancellationToken cancellationToken = default, bool isInitialCreation = false)
        {
            if (!_initialized || !ulong.TryParse(collectionId, out var id))
                return false;
            
            var publishedFileId = new PublishedFileId_t(id);
            
            // 初回作成時以外は、既存のアイテムを全て削除してから追加
            if (!isInitialCreation)
            {
                // 既存のコレクション情報を取得
                var collectionInfo = await GetCollectionInfoAsync(collectionId);
                if (collectionInfo != null && collectionInfo.AddonIds.Count > 0)
                {
                    var tcsRemove = new TaskCompletionSource<bool>();
                    
                    // 更新ハンドルを作成
                    var removeHandle = SteamUGC.StartItemUpdate(new AppId_t(4000), publishedFileId);
                    
                    // 既存のアイテムを全て削除
                    foreach (var existingId in collectionInfo.AddonIds)
                    {
                        if (ulong.TryParse(existingId, out var existingFileId))
                        {
                            SteamUGC.RemoveDependency(publishedFileId, new PublishedFileId_t(existingFileId));
                        }
                    }
                    
                    // 削除を送信
                    var removeApiCall = SteamUGC.SubmitItemUpdate(removeHandle, "Clearing collection for update");
                    
                    CallResult<SubmitItemUpdateResult_t>? removeCallResult = null;
                    removeCallResult = CallResult<SubmitItemUpdateResult_t>.Create((result, failure) =>
                    {
                        tcsRemove.SetResult(!failure && result.m_eResult == EResult.k_EResultOK);
                        
                        if (removeCallResult != null)
                            _activeCallResults.Remove(removeCallResult);
                    });
                    
                    _activeCallResults.Add(removeCallResult);
                    removeCallResult.Set(removeApiCall);
                    
                    // 削除完了を待つ
                    var startTime = DateTime.Now;
                    var timeout = TimeSpan.FromSeconds(30);
                    
                    while (!tcsRemove.Task.IsCompleted && DateTime.Now - startTime < timeout)
                    {
                        await Task.Delay(100);
                    }
                    
                    if (!tcsRemove.Task.IsCompleted || !await tcsRemove.Task)
                        return false;
                        
                    // 削除後少し待機
                    await Task.Delay(2000);
                }
            }
            
            // 新しいアイテムを追加
            var tcs = new TaskCompletionSource<bool>();
            var updateHandle = SteamUGC.StartItemUpdate(new AppId_t(4000), publishedFileId);
            
            // 全てのアドオンを追加
            int totalProcessed = 0;
            foreach (var addonId in addonIds)
            {
                if (cancellationToken.IsCancellationRequested)
                    return false;
                    
                if (ulong.TryParse(addonId, out var addonFileId))
                {
                    SteamUGC.AddDependency(publishedFileId, new PublishedFileId_t(addonFileId));
                }
                totalProcessed++;
                
                // 進捗報告
                progress?.Report((totalProcessed, addonIds.Count));
                
                // 10個ごとに少し待機（APIレート制限対策）
                if (totalProcessed % 10 == 0)
                {
                    await Task.Delay(50);
                }
            }
            
            // 1回だけ更新を送信
            var apiCall = SteamUGC.SubmitItemUpdate(updateHandle, updateNote ?? "Updated collection contents");
            
            CallResult<SubmitItemUpdateResult_t>? callResult = null;
            callResult = CallResult<SubmitItemUpdateResult_t>.Create((result, failure) =>
            {
                tcs.SetResult(!failure && result.m_eResult == EResult.k_EResultOK);
                
                if (callResult != null)
                    _activeCallResults.Remove(callResult);
            });
            
            _activeCallResults.Add(callResult);
            callResult.Set(apiCall);
            
            // タイムアウト設定
            var startTime2 = DateTime.Now;
            var timeout2 = TimeSpan.FromSeconds(60); // 大量のアドオンがある場合のために60秒に延長
            
            while (!tcs.Task.IsCompleted && DateTime.Now - startTime2 < timeout2)
            {
                await Task.Delay(100);
            }
            
            return tcs.Task.IsCompleted ? await tcs.Task : false;
        }
        
        /// <summary>
        /// コレクションが存在するか確認
        /// </summary>
        public async Task<bool> CheckCollectionExistsAsync(string collectionId)
        {
            var collectionInfo = await GetCollectionInfoAsync(collectionId);
            return collectionInfo != null;
        }
        
        /// <summary>
        /// ワークショップページをブラウザで開く
        /// </summary>
        public void OpenWorkshopPage(string url)
        {
            try
            {
                var processInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(processInfo);
            }
            catch (Exception ex)
            {
                // Console.WriteLine($"[SteamworksManager] Failed to open workshop page: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 複数のアドオンのメタデータとサムネイルを取得
        /// </summary>
        public async Task<Dictionary<string, WorkshopItemInfo>> FetchMetadataForAddonsAsync(
            List<string> addonIds, 
            IProgress<(int current, int total)>? progress = null)
        {
            var results = new Dictionary<string, WorkshopItemInfo>();
            
            if (!_initialized || addonIds.Count == 0)
                return results;
            
            // バッチサイズ（Steam APIの制限）
            const int batchSize = 50;
            var batches = new List<List<string>>();
            
            for (int i = 0; i < addonIds.Count; i += batchSize)
            {
                batches.Add(addonIds.Skip(i).Take(batchSize).ToList());
            }
            
            int processedCount = 0;
            
            foreach (var batch in batches)
            {
                var itemInfos = await GetWorkshopItemsBatchAsync(batch);
                
                foreach (var info in itemInfos)
                {
                    results[info.Id] = info;
                    
                    // メタデータは呼び出し元でAddonManagerに保存する
                    // サムネイル画像のダウンロードも呼び出し元で行う
                }
                
                processedCount += batch.Count;
                progress?.Report((processedCount, addonIds.Count));
                
                // レート制限回避のため少し待機
                if (batches.IndexOf(batch) < batches.Count - 1)
                {
                    await Task.Delay(100);
                }
            }
            
            return results;
        }
        
        public void Dispose()
        {
            _callbackCancellation?.Cancel();
            _callbackTask?.Wait(1000);
            
            // CallResultをクリア
            _activeCallResults.Clear();
            
            if (_initialized)
            {
                SteamAPI.Shutdown();
                _initialized = false;
            }
            
            _callbackSemaphore?.Dispose();
            _callbackCancellation?.Dispose();
        }
    }
}