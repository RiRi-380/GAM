using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.Models;
using ReactiveUI;
using System;
using System.IO;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia;
using System.Collections.ObjectModel;
using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace GmodAddonManager.UI.ViewModels;

public sealed class AddonItemViewModel : ViewModelBase, IDisposable
{
    private readonly WorkshopAddon addon;
    private readonly AddonManager addonManager;
    // private readonly ILogger logger; // Removed logging
    

    private bool isSelected;
    private bool isDetailsLoaded;
    private string? thumbnailPath;
    private long fileSize;
    private DateTime lastModified;
    private string? thumbnailUrl;
    private bool isThumbnailLoading = true;
    private AssetItemViewModel? currentAsset;
    private string? notes;
    private bool isNotesLoaded;
    private bool isFavorite;
    private Bitmap? thumbnailBitmap;
    private ResolvedAddonState? resolvedState;
    private bool? actualEnabled;
    private bool isRuntimeApplyPending;
    private bool isFileSizeCalculated;
    private bool disposed;

    private static readonly IReadOnlyDictionary<string, string> TypeKeyMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["gamemode"] = "AddonType.Gamemode",
            ["map"] = "AddonType.Map",
            ["weapon"] = "AddonType.Weapon",
            ["vehicle"] = "AddonType.Vehicle",
            ["npc"] = "AddonType.NPC",
            ["tool"] = "AddonType.Tool",
            ["entity"] = "AddonType.Entity",
            ["effects"] = "AddonType.Effects",
            ["model"] = "AddonType.Model",
            ["servercontent"] = "AddonType.ServerContent"
        };

    private static readonly IReadOnlyDictionary<string, string> TagKeyMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["build"] = "AddonTag.Build",
            ["cartoon"] = "AddonTag.Cartoon",
            ["comic"] = "AddonTag.Comic",
            ["fun"] = "AddonTag.Fun",
            ["movie"] = "AddonTag.Movie",
            ["realism"] = "AddonTag.Realism",
            ["roleplay"] = "AddonTag.Roleplay",
            ["scenic"] = "AddonTag.Scenic",
            ["water"] = "AddonTag.Water"
        };

    public AddonItemViewModel(
        WorkshopAddon addon,
        AddonManager addonManager,
        object? logger = null) // logger parameter kept for compatibility but not used
    {
        this.addon = addon;
        this.addonManager = addonManager;
        // this.logger = logger; // Removed logging

        // 基本情報の設定
        AddonId = addon.Id;
        title = ResolveDisplayTitle(addon);
        FolderPath = addon.FolderPath;
        
        // ファイル情報の初期化
        if (addon.Size > 0)
        {
            FileSize = addon.Size;
            isFileSizeCalculated = true;
        }

        if (!string.IsNullOrWhiteSpace(FolderPath))
        {
            if (Directory.Exists(FolderPath))
            {
                var dirInfo = new DirectoryInfo(FolderPath);
                LastModified = dirInfo.LastWriteTime;
            }
            else if (File.Exists(FolderPath))
            {
                var fileInfo = new FileInfo(FolderPath);
                LastModified = fileInfo.LastWriteTime;
                if (!isFileSizeCalculated)
                {
                    FileSize = fileInfo.Length;
                    isFileSizeCalculated = true;
                }
            }
        }

        // コマンドの初期化
        LoadDetailsCommand = ReactiveCommand.CreateFromTask(LoadDetailsAsync);
        OpenFolderCommand = ReactiveCommand.Create(OpenFolder);
        LoadThumbnailCommand = ReactiveCommand.CreateFromTask(LoadThumbnailAsync);
        OpenWorkshopCommand = ReactiveCommand.Create(OpenWorkshopPage);
        CopyWorkshopUrlCommand = ReactiveCommand.CreateFromTask(CopyWorkshopUrlAsync);
        SaveNotesCommand = ReactiveCommand.Create(SaveNotes);
        ToggleFavoriteCommand = ReactiveCommand.CreateFromTask(ToggleFavoriteAsync);
        
        // お気に入り状態の初期化

        LocalizationManager.Instance.PropertyChanged += OnLocalizationChanged;
    }

    public string AddonId { get; }
    public string Title 
    { 
        get => title;
        private set => SetAndRaise(ref title, value);
    }
    private string title = "";

    private static string ResolveDisplayTitle(WorkshopAddon addon)
    {
        return ResolveDisplayTitle(addon.Id, addon.Title);
    }

    private static string ResolveDisplayTitle(string addonId, string? candidateTitle)
    {
        return IsConcreteTitle(candidateTitle)
            ? candidateTitle!.Trim()
            : AddonTitleHelper.BuildPlaceholderTitle(addonId);
    }

    private static bool IsConcreteTitle(string? title)
    {
        return !string.IsNullOrWhiteSpace(title) &&
               !AddonTitleHelper.IsPlaceholderTitle(title) &&
               !IsLoadingTitle(title);
    }

    private static bool IsLoadingTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var trimmed = title.Trim();
        return string.Equals(trimmed, L.Get("Common.Loading"), StringComparison.Ordinal) ||
               string.Equals(trimmed, "Loading...", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "読み込み中...", StringComparison.Ordinal);
    }
    
    // タイトルを外部から更新するためのメソッド
    public void UpdateTitle(string? newTitle)
    {
        Title = ResolveDisplayTitle(AddonId, newTitle);
        if (!string.IsNullOrWhiteSpace(newTitle))
        {
            addon.Title = newTitle;
        }

        if (IsConcreteTitle(newTitle))
        {
            addon.NeedsTitleUpdate = false;
        }
    }

    // WorkshopAddonから情報を更新するメソッド
    public void UpdateFromWorkshopAddon(WorkshopAddon workshopAddon)
    {
        var previousThumbnailUrl = addon.ThumbnailUrl;

        // タイトルの更新
        if (!string.IsNullOrEmpty(workshopAddon.Title))
        {
            Title = ResolveDisplayTitle(workshopAddon.Id, workshopAddon.Title);
            addon.Title = workshopAddon.Title;
            addon.NeedsTitleUpdate = !IsConcreteTitle(workshopAddon.Title);
        }
        
        // 内部のaddonオブジェクトを更新
        addon.Size = workshopAddon.Size;
        if (!isFileSizeCalculated && workshopAddon.Size > 0)
        {
            FileSize = workshopAddon.Size;
            isFileSizeCalculated = true;
        }
        addon.LastUpdated = workshopAddon.LastUpdated;
        addon.FirstSeenSubscribedAtUtc = workshopAddon.FirstSeenSubscribedAtUtc;
        addon.WorkshopUpdatedAtUtc = workshopAddon.WorkshopUpdatedAtUtc;
        addon.IsAvailable = workshopAddon.IsAvailable;
        addon.IsDownloadPending = workshopAddon.IsDownloadPending;
        addon.Description = workshopAddon.Description;
        addon.Author = workshopAddon.Author;
        addon.ThumbnailUrl = workshopAddon.ThumbnailUrl;
        addon.Tags = workshopAddon.Tags;
        if (!string.IsNullOrWhiteSpace(workshopAddon.Type))
        {
            addon.Type = workshopAddon.Type;
        }
        
        // プロパティ変更通知
        this.RaisePropertyChanged(nameof(FileSizeText));
        this.RaisePropertyChanged(nameof(LastModifiedText));
        this.RaisePropertyChanged(nameof(TimeCreatedText));
        this.RaisePropertyChanged(nameof(TimeUpdatedText));
        this.RaisePropertyChanged(nameof(Tags));
        this.RaisePropertyChanged(nameof(Type));
        this.RaisePropertyChanged(nameof(TypeDisplay));
        this.RaisePropertyChanged(nameof(TagsDisplay));
        this.RaisePropertyChanged(nameof(IsAvailable));
        this.RaisePropertyChanged(nameof(IsMissing));
        this.RaisePropertyChanged(nameof(CardOpacity));
        
        // サムネイルURLが変更された場合は再読み込み
        if (!string.IsNullOrEmpty(workshopAddon.ThumbnailUrl) &&
            !string.Equals(workshopAddon.ThumbnailUrl, previousThumbnailUrl, StringComparison.Ordinal))
        {
            ThumbnailUrl = workshopAddon.ThumbnailUrl;
            IsThumbnailLoading = true;
            _ = LoadThumbnailAsync();
        }
    }

    public void UpdateTagsAndType(string[]? tags, string? type)
    {
        if (tags != null && tags.Length > 0)
        {
            addon.Tags = tags;
            this.RaisePropertyChanged(nameof(Tags));
            this.RaisePropertyChanged(nameof(TagsDisplay));
        }

        if (!string.IsNullOrWhiteSpace(type))
        {
            addon.Type = type;
            this.RaisePropertyChanged(nameof(Type));
            this.RaisePropertyChanged(nameof(TypeDisplay));
        }
    }

    public string? FolderPath { get; }
    
    public bool IsGmaFile => addon.IsGmaFile;
    public bool IsLocal => addon.IsLocal;
    internal WorkshopAddon SortSource => addon;

    public bool IsAvailable => addon.IsAvailable;

    public bool IsMissing =>
        !addon.IsAvailable &&
        !addon.IsDownloadPending &&
        !addon.IsLocal;

    public double CardOpacity => addon.IsAvailable ? 1.0 : 0.55;

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            SetAndRaise(ref isSelected, value);
            this.RaisePropertyChanged(nameof(BorderColor));
        }
    }

    public bool IsDetailsLoaded
    {
        get => isDetailsLoaded;
        private set => SetAndRaise(ref isDetailsLoaded, value);
    }

    public string? ThumbnailPath
    {
        get => thumbnailPath;
        private set => SetAndRaise(ref thumbnailPath, value);
    }

    public long FileSize
    {
        get => fileSize;
        private set => SetAndRaise(ref fileSize, value);
    }

    public DateTime LastModified
    {
        get => lastModified;
        private set => SetAndRaise(ref lastModified, value);
    }

    public string? ThumbnailUrl
    {
        get => thumbnailUrl;
        private set
        {
            SetAndRaise(ref thumbnailUrl, value);
            
            // URLが設定されたら画像をダウンロード
            if (!string.IsNullOrEmpty(value))
            {
                _ = LoadBitmapFromUrlAsync(value);
            }
        }
    }
    
    public Bitmap? ThumbnailBitmap
    {
        get => thumbnailBitmap;
        private set
        {
            if (ReferenceEquals(thumbnailBitmap, value))
            {
                return;
            }

            thumbnailBitmap?.Dispose();
            SetAndRaise(ref thumbnailBitmap, value);
        }
    }

    public bool IsThumbnailLoading
    {
        get => isThumbnailLoading;
        private set => SetAndRaise(ref isThumbnailLoading, value);
    }

    public string FileSizeText => FormatFileSize(FileSize);
    public string LastModifiedText => LastModified.ToString("yyyy/MM/dd HH:mm");

    public ReactiveCommand<Unit, Unit> LoadDetailsCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> LoadThumbnailCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenWorkshopCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyWorkshopUrlCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveNotesCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleFavoriteCommand { get; }
    
    // Workshop関連プロパティ
    public bool HasWorkshopId => !IsLocal && !string.IsNullOrEmpty(AddonId) && AddonId != "0" && ulong.TryParse(AddonId, out _);
    public string WorkshopUrl => $"https://steamcommunity.com/sharedfiles/filedetails/?id={AddonId}";
    public string? WorkshopId => HasWorkshopId ? AddonId : null;
    
    // 詳細情報プロパティ
    public string? Author => addon.Author;
    public string? Description => addon.Description;
    public string? Type => addon.Type;
    public IReadOnlyList<string>? Tags => addon.Tags;
    public string? TypeDisplay => LocalizeAddonType(Type);
    public IEnumerable<string> TagsDisplay => Tags == null
        ? Array.Empty<string>()
        : Tags.Select(LocalizeAddonTag).ToArray();
    public DateTime? TimeCreated { get; private set; }
    public DateTime? TimeUpdated { get; private set; }
    public string? TimeCreatedText => TimeCreated?.ToString("yyyy/MM/dd HH:mm");
    public string? TimeUpdatedText => TimeUpdated?.ToString("yyyy/MM/dd HH:mm");
    
    // アセット情報
    public ObservableCollection<string> AssetMemberships { get; } = new();
    
    // 技術情報
    public ObservableCollection<string> FileList { get; } = new();
    public ObservableCollection<FileTreeNode> FileTree { get; } = new();
    public string StateText
    {
        get => ActualStateText;
    }
    public string IsGmaFileText => IsGmaFile ? L.Get("Common.Yes") : L.Get("Common.No");
    
    // メモ
    public string? Notes
    {
        get => notes;
        set => SetAndRaise(ref notes, value);
    }
    
    // お気に入り
    public bool IsFavorite
    {
        get => isFavorite;
        set => SetAndRaise(ref isFavorite, value);
    }
    
    // 現在のアセットを設定
    public void SetCurrentAsset(AssetItemViewModel? asset)
    {
        currentAsset = asset;
        
        // 詳細が読み込まれている場合は、アセット情報を更新
        if (IsDetailsLoaded)
        {
            UpdateAssetMemberships();
        }
    }
    
    // アドオンの状態を更新
    public void UpdateAddonState()
    {
        RefreshRuntimeState(
            addonManager.GetResolvedAddonState(AddonId),
            addonManager.GetActualAddonEnabledState(AddonId),
            false);
    }

    public void RefreshRuntimeState(
        ResolvedAddonState? state,
        bool? actualState,
        bool hasQueuedRuntimeApply)
    {
        resolvedState = state;
        actualEnabled = actualState;
        isRuntimeApplyPending =
            hasQueuedRuntimeApply &&
            state?.IsRuntimeTarget == true &&
            (!actualState.HasValue || actualState.Value != state.DesiredEnabled);

        this.RaisePropertyChanged(nameof(BorderColor));
        this.RaisePropertyChanged(nameof(IsExcludedAnywhere));
        this.RaisePropertyChanged(nameof(StateText));
        this.RaisePropertyChanged(nameof(ActualStateText));
        this.RaisePropertyChanged(nameof(DesiredStateText));
        this.RaisePropertyChanged(nameof(StateReasonText));
        this.RaisePropertyChanged(nameof(PendingStateText));
        this.RaisePropertyChanged(nameof(ActualStateBadgeBackground));
        this.RaisePropertyChanged(nameof(CardOpacity));
        this.RaisePropertyChanged(nameof(DisplayAddonState));
        this.RaisePropertyChanged(nameof(IsDisplayOff));
        this.RaisePropertyChanged(nameof(ActualEnabled));
        this.RaisePropertyChanged(nameof(DesiredEnabled));
        this.RaisePropertyChanged(nameof(ResolvedState));
        this.RaisePropertyChanged(nameof(IsRuntimeApplyPending));
    }

    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LocalizationManager.CurrentLanguage) || string.IsNullOrEmpty(e.PropertyName))
        {
            this.RaisePropertyChanged(nameof(StateText));
            this.RaisePropertyChanged(nameof(ActualStateText));
            this.RaisePropertyChanged(nameof(DesiredStateText));
            this.RaisePropertyChanged(nameof(StateReasonText));
            this.RaisePropertyChanged(nameof(PendingStateText));
            this.RaisePropertyChanged(nameof(TypeDisplay));
            this.RaisePropertyChanged(nameof(TagsDisplay));

            if (addon.NeedsTitleUpdate || AddonTitleHelper.IsPlaceholderTitle(Title) || IsLoadingTitle(Title))
            {
                Title = ResolveDisplayTitle(addon);
            }
        }
    }

    private static string LocalizeAddonType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = NormalizeToken(value);
        if (TypeKeyMap.TryGetValue(normalized, out var key))
        {
            var localized = L.Get(key);
            return localized == key ? value : localized;
        }

        return value;
    }

    private static string LocalizeAddonTag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = NormalizeToken(value);
        if (TagKeyMap.TryGetValue(normalized, out var key))
        {
            var localized = L.Get(key);
            return localized == key ? value : localized;
        }

        return value;
    }

    private static string NormalizeToken(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var buffer = new char[trimmed.Length];
        var length = 0;
        foreach (var ch in trimmed)
        {
            if (char.IsWhiteSpace(ch) || ch == '_' || ch == '-' || ch == '/')
            {
                continue;
            }

            buffer[length++] = char.ToLowerInvariant(ch);
        }

        return length == buffer.Length
            ? new string(buffer)
            : new string(buffer, 0, length);
    }
    
    // 枠線の色を決定
    public string BorderColor
    {
        get
        {
            // 選択されている場合は青色
            if (IsSelected)
            {
                return "#4A90E2"; // アクセントカラー（青）
            }
            
            if (actualEnabled == false &&
                resolvedState?.Reason == AddonStateResolutionReason.Excluded)
            {
                return "#F44336"; // 赤
            }
            
            if (actualEnabled == false)
            {
                return "#FF9800"; // オレンジ
            }
            
            return "#303030"; // デフォルトはダークグレー
        }
    }
    
    // どこかのアセットで除外されているかチェック
    public bool IsExcludedAnywhere
    {
        get => resolvedState?.Reason == AddonStateResolutionReason.Excluded;
    }

    public ResolvedAddonState? ResolvedState => resolvedState;

    public bool? ActualEnabled => actualEnabled;

    public bool DesiredEnabled => resolvedState?.DesiredEnabled == true;

    public bool IsRuntimeApplyPending => isRuntimeApplyPending;

    public string ActualStateText => actualEnabled switch
    {
        true => LocalizeState(AddonState.Enabled),
        false => LocalizeState(AddonState.Disabled),
        _ => L.Get("Common.Unknown")
    };

    public string ActualStateBadgeBackground => actualEnabled switch
    {
        true => "#2E7D32",
        false when resolvedState?.Reason == AddonStateResolutionReason.Excluded => "#C62828",
        false => "#EF6C00",
        _ => "#455A64"
    };

    public string DesiredStateText
    {
        get
        {
            if (resolvedState == null || !resolvedState.IsRuntimeTarget)
            {
                return L.Get("Common.Unknown");
            }

            return LocalizeState(
                resolvedState.Reason == AddonStateResolutionReason.Excluded
                    ? AddonState.Excluded
                    : resolvedState.DesiredEnabled
                        ? AddonState.Enabled
                        : AddonState.Disabled);
        }
    }

    public string StateReasonText
    {
        get
        {
            if (resolvedState == null)
            {
                return L.Get("Common.Unknown");
            }

            var japanese = LocalizationManager.Instance.CurrentLanguage
                .StartsWith("ja", StringComparison.OrdinalIgnoreCase);
            return resolvedState.Reason switch
            {
                AddonStateResolutionReason.NotSubscribed =>
                    japanese ? "Steamで購読されていません" : "Not subscribed on Steam",
                AddonStateResolutionReason.Excluded when
                    resolvedState.ExcludedByAssets.Any(source =>
                        source.AssetId == SystemAssetDefinitions.SubscribeId) =>
                    FormatSourceReason(
                        japanese ? "すべて除外" : "All subscribed addons excluded",
                        resolvedState.ExcludedByAssets),
                AddonStateResolutionReason.Excluded =>
                    FormatSourceReason(
                        japanese ? "共通除外" : "Globally excluded",
                        resolvedState.ExcludedByAssets),
                AddonStateResolutionReason.Enabled when
                    resolvedState.EnabledBySubscribe &&
                    resolvedState.EnabledByAssets.Count == 0 =>
                    japanese ? "Subscribeが有効" : "Enabled by Subscribe",
                AddonStateResolutionReason.Enabled when resolvedState.EnabledBySubscribe =>
                    FormatSourceReason(
                        japanese ? "SubscribeとAssetが有効" : "Enabled by Subscribe and assets",
                        resolvedState.EnabledByAssets),
                AddonStateResolutionReason.Enabled =>
                    FormatSourceReason(
                        japanese ? "Assetが有効" : "Enabled by assets",
                        resolvedState.EnabledByAssets),
                _ => japanese ? "有効な構成がありません" : "No enabled source"
            };
        }
    }

    public string PendingStateText
    {
        get
        {
            if (!isRuntimeApplyPending)
            {
                return string.Empty;
            }

            return LocalizationManager.Instance.CurrentLanguage
                .StartsWith("ja", StringComparison.OrdinalIgnoreCase)
                ? "GMod終了後に反映"
                : "Applies after GMod exits";
        }
    }

    public AddonState? DisplayAddonState => actualEnabled switch
    {
        true => AddonState.Enabled,
        false => AddonState.Disabled,
        _ => null
    };

    public bool IsDisplayOff => actualEnabled == false;

    private static string LocalizeState(AddonState state)
    {
        var stateKey = $"AddonState.{state}";
        var localized = L.Get(stateKey);
        return localized == stateKey ? state.ToString() : localized;
    }

    private static string FormatSourceReason(
        string prefix,
        IReadOnlyList<ResolvedAddonStateSource> sources)
    {
        var names = sources
            .Select(source => source.AssetName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return names.Length == 0
            ? prefix
            : $"{prefix}: {string.Join(", ", names)}";
    }

    public Task LoadDetailsBackgroundAsync()
    {
        return LoadDetailsAsyncInternal(false);
    }

    private Task LoadDetailsAsync()
    {
        return LoadDetailsAsyncInternal(true);
    }

    private async Task LoadDetailsAsyncInternal(bool treatAsHot)
    {
        if (disposed || IsDetailsLoaded)
        {
            return;
        }

        try
        {
            if (!isNotesLoaded)
            {
                LoadNotes();
            }

            if (!isFileSizeCalculated)
            {
                CalculateFileSize();
                this.RaisePropertyChanged(nameof(FileSizeText));
            }

            // GMAファイルから詳細情報を読み込み
            if (FolderPath != null)
            {
                var details = await addonManager.ScanAddonAsync(FolderPath);
                if (disposed)
                {
                    return;
                }

                if (details != null)
                {
                    if (!string.IsNullOrWhiteSpace(details.Title))
                    {
                        Title = ResolveDisplayTitle(AddonId, details.Title);
                        addon.Title = details.Title;
                        addon.NeedsTitleUpdate = !IsConcreteTitle(details.Title);
                    }
                    addon.Author = details.Author;
                    addon.Description = details.Description;
                    addon.Type = details.Type;
                    addon.Tags = details.Tags;
                    this.RaisePropertyChanged(nameof(Type));
                    this.RaisePropertyChanged(nameof(Tags));
                    this.RaisePropertyChanged(nameof(TypeDisplay));
                    this.RaisePropertyChanged(nameof(TagsDisplay));

            // タイトルがまだWorkshop形式の場合、HybridWorkshopServiceから取得を試みる
                    var thumbnailFile = Path.Combine(FolderPath, "addon.jpg");
                    if (File.Exists(thumbnailFile))
                    {
                        ThumbnailPath = thumbnailFile;
                    }
                }
            }
            
            // logger.LogError($"Failed to load addon details for {AddonId}", ex); // Removed logging
            if (HasWorkshopId &&
                (addon.NeedsTitleUpdate || AddonTitleHelper.IsPlaceholderTitle(Title)) &&
                ViewModelLocator.HybridWorkshopService != null)
            {
                try
                {
                    var workshopDetails = await ViewModelLocator.HybridWorkshopService.GetWorkshopDetailsAsync(AddonId, treatAsHot);
                    if (disposed)
                    {
                        return;
                    }

                    if (workshopDetails != null && !string.IsNullOrWhiteSpace(workshopDetails.Title))
                    {
                        Title = ResolveDisplayTitle(AddonId, workshopDetails.Title);
                        addon.Title = workshopDetails.Title;
                        addon.NeedsTitleUpdate = !IsConcreteTitle(workshopDetails.Title);
                        if (!string.IsNullOrWhiteSpace(workshopDetails.Description))
                        {
                            addon.Description = workshopDetails.Description;
                        }
                        if ((addon.Tags == null || addon.Tags.Length == 0) &&
                            workshopDetails.Tags != null && workshopDetails.Tags.Length > 0)
                        {
                            addon.Tags = workshopDetails.Tags;
                            this.RaisePropertyChanged(nameof(Tags));
                            this.RaisePropertyChanged(nameof(TagsDisplay));
                        }
            // Workshop情報を取得
                    }
                }
                catch (Exception ex)
                {
                    SafeFileLogger.TryLogException($"AddonItemViewModel.LoadDetailsAsyncInternal.WorkshopTitleFetch(AddonId={AddonId})", ex);
                }
            }

            // private readonly ILogger logger; // Removed logging
            if (HasWorkshopId)
            {
                await LoadWorkshopDetailsAsync(treatAsHot);
                if (disposed)
                {
                    return;
                }
            }
            
            // private readonly ILogger logger; // Removed logging
            LoadFileTree();
            
            // アセット情報を更新
            UpdateAssetMemberships();
            
            IsDetailsLoaded = true;
            // logger.LogDebug($"Loaded details for addon {AddonId}"); // Removed logging
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException($"AddonItemViewModel.LoadDetailsAsyncInternal(AddonId={AddonId})", ex);
        }
    }
    
    private async Task LoadWorkshopDetailsAsync(bool treatAsHot)
    {
        try
        {
            if (!HasWorkshopId)
            {
                return;
            }

            var workshopService = addonManager.GetSteamWorkshopService();
            var details = await workshopService.GetWorkshopDetailsAsync(AddonId, treatAsHot);
            
            if (details != null)
            {
                TimeCreated = DateTimeOffset.FromUnixTimeSeconds(details.TimeCreated).DateTime;
                TimeUpdated = DateTimeOffset.FromUnixTimeSeconds(details.TimeUpdated).DateTime;
                this.RaisePropertyChanged(nameof(TimeCreated));
                this.RaisePropertyChanged(nameof(TimeUpdated));
                this.RaisePropertyChanged(nameof(TimeCreatedText));
                this.RaisePropertyChanged(nameof(TimeUpdatedText));
            }
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException($"AddonItemViewModel.LoadWorkshopDetailsAsync(AddonId={AddonId})", ex);
        }
    }
    
    private void LoadFileTree()
    {
        try
        {
            FileTree.Clear();
            
            if (Directory.Exists(FolderPath))
            {
                var dirInfo = new DirectoryInfo(FolderPath);
                var rootNode = CreateFileTreeNode(dirInfo, dirInfo.FullName.Length + 1);
                
            // ファイルツリーの取得に失敗
                foreach (var child in rootNode.Children)
                {
                    FileTree.Add(child);
                }
            }
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException($"AddonItemViewModel.LoadFileTree(AddonId={AddonId})", ex);
        }
    }
    
    private FileTreeNode CreateFileTreeNode(DirectoryInfo directory, int basePathLength, int depth = 0)
    {
        var node = new FileTreeNode
        {
            Name = directory.Name,
            FullPath = directory.FullName,
            IsDirectory = true
        };
        
            // アクセスエラーは無視
        if (depth >= 3)
        {
            node.Name += " ...";
            return node;
        }
        
        try
        {
            // サブディレクトリ
            foreach (var subDir in directory.GetDirectories().OrderBy(d => d.Name))
            {
                node.Children.Add(CreateFileTreeNode(subDir, basePathLength, depth + 1));
            }
            
            // ファイル（最大20ファイル）
            var files = directory.GetFiles().OrderBy(f => f.Name).Take(20).ToList();
            foreach (var file in files)
            {
                node.Children.Add(new FileTreeNode
                {
                    Name = file.Name,
                    FullPath = file.FullName,
                    IsDirectory = false,
                    Size = file.Length
                });
            }
            
            if (directory.GetFiles().Length > 20)
            {
                node.Children.Add(new FileTreeNode
                {
                    Name = "... and more files",
                    IsDirectory = false
                });
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Ignore access-denied nodes and continue building the tree.
        }
        catch (PathTooLongException)
        {
            // Ignore too-long paths in the preview tree.
        }
        catch (IOException)
        {
            // Ignore transient filesystem errors in the preview tree.
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException($"AddonItemViewModel.CreateFileTreeNode(AddonId={AddonId},Path={directory.FullName})", ex);
        }
        
        return node;
    }
    
    private void UpdateAssetMemberships()
    {
        try
        {
            AssetMemberships.Clear();
            
            var config = addonManager.GetConfiguration();
            var addedAssets = new HashSet<string>(); // 重複チェック用
            
            foreach (var asset in config.Assets)
            {
                    // 重複を防ぐ
                if (currentAsset != null && asset.Name == currentAsset.Name)
                {
                    continue;
                }
                
                // アセットがすべてのアドオンを含む場合、または個別にこのアドオンを含む場合
                if (asset.ContainsAllAddons() || asset.Addons.Contains(AddonId))
                {
                    // 重複を防ぐ
                    if (!addedAssets.Contains(asset.Name))
                    {
                        AssetMemberships.Add(asset.Name);
                        addedAssets.Add(asset.Name);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException($"AddonItemViewModel.UpdateAssetMemberships(AddonId={AddonId})", ex);
        }
    }

    private void CalculateFileSize()
    {
        if (isFileSizeCalculated)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(FolderPath))
        {
            FileSize = addon.Size > 0 ? addon.Size : 0;
            isFileSizeCalculated = true;
            return;
        }

        if (File.Exists(FolderPath))
        {
            FileSize = new FileInfo(FolderPath).Length;
            isFileSizeCalculated = true;
            return;
        }

        if (!Directory.Exists(FolderPath))
        {
            FileSize = addon.Size > 0 ? addon.Size : 0;
            isFileSizeCalculated = true;
            return;
        }

        try
        {
            long totalSize = 0;
            var dirInfo = new DirectoryInfo(FolderPath);
            
            foreach (var file in dirInfo.GetFiles("*", SearchOption.AllDirectories))
            {
                totalSize += file.Length;
            }
            
            FileSize = totalSize;
        }
        catch (Exception ex)
        {
            // logger.LogError($"Failed to calculate file size for {AddonId}", ex); // Removed logging
            FileSize = addon.Size > 0 ? addon.Size : 0;
        }
        finally
        {
            isFileSizeCalculated = true;
        }
    }

    private void OpenFolder()
    {
        try
        {
            if (FolderPath != null && Directory.Exists(FolderPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = FolderPath,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            // logger.LogError($"Failed to open folder for {AddonId}", ex); // Removed logging
        }
    }

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }

        return $"{len:0.##} {sizes[order]}";
    }

    internal Task LoadThumbnailAsync(bool allowRemote)
    {
        return LoadThumbnailInternalAsync(allowRemote);
    }

    private Task LoadThumbnailAsync()
    {
        return LoadThumbnailInternalAsync(allowRemote: true);
    }

    private async Task LoadThumbnailInternalAsync(bool allowRemote)
    {
        if (!IsThumbnailLoading && ThumbnailBitmap != null)
        {
            return;
        }

        IsThumbnailLoading = true;

        // gmpublisherと同じ方式：LoadThumbnailUrlAsyncを呼び出すだけ
        await LoadThumbnailUrlAsync(allowRemote);
    }
    
    private async Task LoadThumbnailUrlAsync(bool allowRemote)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(ThumbnailPath) && File.Exists(ThumbnailPath))
            {
                ThumbnailUrl = new Uri(ThumbnailPath).AbsoluteUri;
                IsThumbnailLoading = false;
                return;
            }

            // 画像なし
            if (!string.IsNullOrEmpty(FolderPath))
            {
                try
                {
                    var jpgPath = Path.Combine(FolderPath, "addon.jpg");
                    if (File.Exists(jpgPath))
                    {
                        // file://スキームで設定（gmpublisherと同じ）
                        ThumbnailUrl = new Uri(jpgPath).AbsoluteUri;
                        IsThumbnailLoading = false;
                        return;
                    }
                    
                    // addon.pngもチェック
                    var pngPath = Path.Combine(FolderPath, "addon.png");
                    if (File.Exists(pngPath))
                    {
                        ThumbnailUrl = new Uri(pngPath).AbsoluteUri;
                        IsThumbnailLoading = false;
                        return;
                    }
                }
                catch (Exception)
                {
                    // Fallback to other thumbnail sources.
                }
            }

            // private readonly ILogger logger; // Removed logging
            if (HasWorkshopId)
            {
                try
                {
                    var iconResolver = addonManager.GetWorkshopIconResolver();
                    if (iconResolver != null && ulong.TryParse(AddonId, out var workshopId))
                    {
                        var iconPath = await iconResolver.GetIconAsync(workshopId);
                        if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
                        {
                            ThumbnailUrl = new Uri(iconPath).AbsoluteUri;
                            IsThumbnailLoading = false;
                            return;
                        }
                    }
                }
                catch (Exception)
                {
                    // Fallback to remote metadata.
                }
            }

            if (!allowRemote)
            {
                IsThumbnailLoading = false;
                return;
            }

            if (!string.IsNullOrWhiteSpace(addon.ThumbnailUrl))
            {
                ThumbnailUrl = addon.ThumbnailUrl;
                IsThumbnailLoading = false;
                return;
            }
            
            // 2. WorkshopからプレビューURLを取得（Steamworks SDK優先）
            if (HasWorkshopId)
            {
                try
                {
                    var hybridService = ViewModelLocator.HybridWorkshopService;
                    if (hybridService != null)
                    {
                        
                        var details = await hybridService.GetWorkshopDetailsAsync(AddonId);
                        if (details != null)
                        {
                            if (!string.IsNullOrEmpty(details.PreviewUrl))
                            {
                                // CDN直リンクを設定（gmpublisherと同じ）
                                ThumbnailUrl = details.PreviewUrl;
                                IsThumbnailLoading = false;
                                return;
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // Fallback to no thumbnail.
                }
            }
            
            // 画像なし
            ThumbnailUrl = null;
            IsThumbnailLoading = false;
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("AddonItemViewModel.LoadThumbnailUrlAsync", ex);
            // エラー時は画像なし
            ThumbnailUrl = null;
            IsThumbnailLoading = false;
        }
    }

    public bool MatchesFilter(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;

        var lowerFilter = filter.ToLower();
        
        // タイトルが"Loading..."の場合は、元のアドオンタイトルも検索対象にする
        bool titleMatches = Title.ToLower().Contains(lowerFilter);
        if (!titleMatches && addon.Title != null && addon.Title != Title)
        {
            titleMatches = addon.Title.ToLower().Contains(lowerFilter);
        }
        
        // GMAファイルの場合、詳細が読み込まれていなければIDとタイトルのみでフィルタ
        if (IsGmaFile && !IsDetailsLoaded)
        {
            return titleMatches || AddonId.Contains(lowerFilter);
        }
        
        return titleMatches ||
               AddonId.Contains(lowerFilter) ||
               (addon.Author?.ToLower().Contains(lowerFilter) ?? false) ||
               (addon.Description?.ToLower().Contains(lowerFilter) ?? false);
    }
    
    private void OpenWorkshopPage()
    {
        try
        {
            if (HasWorkshopId)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = WorkshopUrl,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException($"AddonItemViewModel.OpenWorkshopPage(AddonId={AddonId})", ex);
        }
    }
    
    private async Task CopyWorkshopUrlAsync()
    {
        try
        {
            if (HasWorkshopId && Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                var clipboard = desktop.MainWindow?.Clipboard;
                if (clipboard != null)
                {
                    await clipboard.SetTextAsync(WorkshopUrl);
                }
                
                var errorHandler = ViewModelLocator.ErrorHandler as UIErrorHandler;
                errorHandler?.HandleInfo(L.Get("AddonDetails.UrlCopied"), "AddonDetails");
            }
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException($"AddonItemViewModel.CopyWorkshopUrlAsync(AddonId={AddonId})", ex);
        }
    }
    
    
    private void LoadNotes()
    {
        if (isNotesLoaded)
        {
            return;
        }

        isNotesLoaded = true;

        try
        {
            var notesDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GmodAddonManager",
                "notes"
            );
            
            if (!Directory.Exists(notesDir))
                Directory.CreateDirectory(notesDir);
            
            var notesFile = Path.Combine(notesDir, $"{AddonId}.txt");
            if (File.Exists(notesFile))
            {
                Notes = File.ReadAllText(notesFile);
            }
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException($"AddonItemViewModel.LoadNotes(AddonId={AddonId})", ex);
        }
    }
    
    private void SaveNotes()
    {
        try
        {
            var notesDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GmodAddonManager",
                "notes"
            );
            
            if (!Directory.Exists(notesDir))
                Directory.CreateDirectory(notesDir);
            
            var notesFile = Path.Combine(notesDir, $"{AddonId}.txt");
            
            if (string.IsNullOrWhiteSpace(Notes))
            {
                if (File.Exists(notesFile))
                    File.Delete(notesFile);
            }
            else
            {
                File.WriteAllText(notesFile, Notes);
            }
            
            var errorHandler = ViewModelLocator.ErrorHandler as UIErrorHandler;
            errorHandler?.HandleInfo(L.Get("AddonDetails.NotesSaved"), "AddonDetails");
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException($"AddonItemViewModel.SaveNotes(AddonId={AddonId})", ex);
        }
    }
    
    private async Task ToggleFavoriteAsync()
    {
        var previousFavorite = IsFavorite;
        var nextFavorite = !IsFavorite;

        try
        {
            IsFavorite = nextFavorite;
            addon.IsFavorite = nextFavorite;
            
            // 設定を保存
            await addonManager.SaveConfigurationAsync();
            
            var errorHandler = ViewModelLocator.ErrorHandler as UIErrorHandler;
            if (IsFavorite)
            {
                errorHandler?.HandleInfo(L.Get("AddonDetails.AddedToFavorites"), "AddonDetails");
            }
            else
            {
                errorHandler?.HandleInfo(L.Get("AddonDetails.RemovedFromFavorites"), "AddonDetails");
            }
        }
        catch (Exception ex)
        {
            // Keep UI/model state consistent when persistence fails.
            IsFavorite = previousFavorite;
            addon.IsFavorite = previousFavorite;

            SafeFileLogger.TryLogException($"AddonItemViewModel.ToggleFavoriteAsync(AddonId={AddonId})", ex);
            (ViewModelLocator.ErrorHandler as UIErrorHandler)?.HandleError(ex, "AddonItemViewModel.ToggleFavoriteAsync");
        }
    }
    
    private async Task LoadBitmapFromUrlAsync(string url)
    {
        try
        {
            if (disposed)
            {
                return;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                if (Path.IsPathRooted(url))
                {
                    uri = new Uri(Path.GetFullPath(url));
                }
                else
                {
                    return;
                }
            }

            var bitmap = await RemoteImageLoader.LoadFromUrlAsync(uri);
            if (bitmap != null)
            {
                if (disposed)
                {
                    bitmap.Dispose();
                    return;
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (disposed)
                    {
                        bitmap.Dispose();
                        return;
                    }

                    ThumbnailBitmap = bitmap;
                });
            }
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException($"AddonItemViewModel.LoadBitmapFromUrlAsync(AddonId={AddonId})", ex);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        LocalizationManager.Instance.PropertyChanged -= OnLocalizationChanged;
        thumbnailBitmap?.Dispose();
        thumbnailBitmap = null;
        GC.SuppressFinalize(this);
    }
}
