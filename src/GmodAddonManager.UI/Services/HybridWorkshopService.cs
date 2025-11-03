using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GmodAddonManager.Core.Services;
using Avalonia.Media.Imaging;

namespace GmodAddonManager.UI.Services
{
    /// <summary>
    /// Steamworks SDK（高速）とWeb API（互換性）を組み合わせたハイブリッドサービス
    /// gmpublisherのような高速性とWeb APIの互換性を両立
    /// </summary>
    public class HybridWorkshopService
    {
        private readonly SteamworksManager? _steamworks;
        private readonly SteamWorkshopService _webApi;
        
        public bool IsSteamworksAvailable => _steamworks?.IsInitialized ?? false;
        
        public HybridWorkshopService(
            SteamworksManager? steamworks,
            SteamWorkshopService webApi)
        {
            _steamworks = steamworks;
            _webApi = webApi ?? throw new ArgumentNullException(nameof(webApi));
        }
        
        /// <summary>
        /// アドオン詳細を取得（Steamworks優先、Web APIフォールバック）
        /// </summary>
        public async Task<WorkshopItemDetails?> GetWorkshopDetailsAsync(string workshopId)
        {
            
            // Steamworks SDK経由で高速取得を試みる
            if (_steamworks?.IsInitialized == true)
            {
                try
                {
                    var steamItem = await _steamworks.GetWorkshopItemAsync(workshopId);
                    if (steamItem != null)
                    {
                        return ConvertToWorkshopItemDetails(steamItem);
                    }
                }
                catch (Exception ex)
                {
                }
            }
            
            // Web APIにフォールバック
            return await _webApi.GetWorkshopDetailsAsync(workshopId);
        }
        
        /// <summary>
        /// 複数アドオンをバッチ取得（最大50件、Steamworks優先）
        /// </summary>
        public async Task<List<WorkshopItemDetails>> GetWorkshopDetailsBatchAsync(List<string> workshopIds)
        {
            if (workshopIds == null || workshopIds.Count == 0)
                return new List<WorkshopItemDetails>();
            
            var results = new List<WorkshopItemDetails>();
            
            // Steamworks SDK経由で高速バッチ取得
            if (_steamworks?.IsInitialized == true)
            {
                try
                {
                    var steamItems = await _steamworks.GetWorkshopItemsBatchAsync(workshopIds);
                    foreach (var item in steamItems)
                    {
                        var details = ConvertToWorkshopItemDetails(item);
                        if (details != null)
                            results.Add(details);
                    }
                }
                catch (Exception ex)
                {
                }
            }
            
            // Web APIで残りを取得
            var processedIds = new HashSet<string>(results.Select(r => r.Title).Where(id => id != null));
            var remainingIds = workshopIds.Where(id => !processedIds.Contains(id)).ToList();
            
            if (remainingIds.Count > 0)
            {
                foreach (var id in remainingIds)
                {
                    try
                    {
                        var details = await _webApi.GetWorkshopDetailsAsync(id);
                        if (details != null)
                            results.Add(details);
                    }
                    catch
                    {
                        // Skip failed items
                    }
                }
            }
            
            return results;
        }
        
        /// <summary>
        /// サブスクライブ済みアドオンIDを取得（Steamworks優先）
        /// </summary>
        public async Task<List<string>> GetSubscribedItemsAsync()
        {
            // Steamworks SDK経由で即座に取得
            if (_steamworks.IsInitialized)
            {
                try
                {
                    var items = _steamworks.GetSubscribedItems();
                    if (items.Count > 0)
                        return items;
                }
                catch (Exception ex)
                {
                }
            }
            
            // キャッシュファイルから読み込み（Web API不要）
            return await Task.Run(() => SteamWorkshopCacheReader.GetSubscribedAddonIds());
        }
        
        /// <summary>
        /// アドオンのインストール状態を確認（Steamworks優先）
        /// </summary>
        public bool IsItemInstalled(string workshopId)
        {
            if (_steamworks.IsInitialized)
            {
                try
                {
                    return _steamworks.IsItemInstalled(workshopId);
                }
                catch
                {
                    // フォールバック処理へ
                }
            }
            
            // ファイルシステムで確認（簡易チェック）
            var addonIds = SteamWorkshopCacheReader.GetSubscribedAddonIds();
            return addonIds.Contains(workshopId);
        }
        
        /// <summary>
        /// アドオンのインストールパスを取得（Steamworks優先）
        /// </summary>
        public string? GetItemInstallPath(string workshopId)
        {
            if (_steamworks.IsInitialized)
            {
                try
                {
                    var path = _steamworks.GetItemInstallPath(workshopId);
                    if (!string.IsNullOrEmpty(path))
                        return path;
                }
                catch
                {
                    // フォールバック処理へ
                }
            }
            
            // 標準パスから推測
            var steamPath = SteamWorkshopCacheReader.GetSteamPath();
            if (!string.IsNullOrEmpty(steamPath))
            {
                var workshopPath = System.IO.Path.Combine(steamPath, "steamapps", "workshop", "content", "4000", workshopId);
                if (System.IO.Directory.Exists(workshopPath))
                    return workshopPath;
            }
            
            return null;
        }
        
        
        /// <summary>
        /// Steamworks形式からWorkshopItemDetails形式に変換
        /// </summary>
        private WorkshopItemDetails? ConvertToWorkshopItemDetails(SteamworksManager.WorkshopItemInfo steamItem)
        {
            if (steamItem == null)
                return null;
            
            return new WorkshopItemDetails
            {
                Title = steamItem.Title,
                Description = steamItem.Description,
                PreviewUrl = steamItem.PreviewUrl, // CDN直リンク！
                TimeCreated = (long)steamItem.TimeUpdated, // 作成日時は取得できないので更新日時を使用
                TimeUpdated = (long)steamItem.TimeUpdated,
                Creator = steamItem.Author
            };
        }
        
    }
}