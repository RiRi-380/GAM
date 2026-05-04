using System;
using System.Linq;
using System.Threading.Tasks;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using ReactiveUI;

namespace GmodAddonManager.UI.ViewModels;

public sealed class DisableManifestImportViewModel : ViewModelBase
{
    private readonly IDisableManifestImportService importService;
    private string filePath = string.Empty;
    private string statusMessage = "ファイルを選択すると内容を確認できます。";
    private string errorMessage = string.Empty;
    private string resultMessage = string.Empty;
    private bool hasPreview;
    private bool isBusy;
    private int selectedModeIndex;
    private int validCount;
    private int duplicateCount;
    private int invalidCount;
    private int alreadyExcludedCount;
    private int newlyExcludedCount;
    private bool willRequirePendingApply;
    private bool isSoftMode = true;
    private bool createsDisabledAsset;
    private string assetName = DisableManifest.DefaultName;
    private string sampleIds = string.Empty;
    private string invalidLineSummary = string.Empty;

    public DisableManifestImportViewModel(IDisableManifestImportService importService)
    {
        this.importService = importService;
    }

    public string FilePath
    {
        get => filePath;
        private set => SetAndRaise(ref filePath, value);
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetAndRaise(ref statusMessage, value);
    }

    public string ErrorMessage
    {
        get => errorMessage;
        private set
        {
            SetAndRaise(ref errorMessage, value);
            this.RaisePropertyChanged(nameof(HasError));
            this.RaisePropertyChanged(nameof(CanApply));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public string ResultMessage
    {
        get => resultMessage;
        private set
        {
            SetAndRaise(ref resultMessage, value);
            this.RaisePropertyChanged(nameof(HasResult));
        }
    }

    public bool HasResult => !string.IsNullOrWhiteSpace(ResultMessage);

    public bool HasPreview
    {
        get => hasPreview;
        private set
        {
            SetAndRaise(ref hasPreview, value);
            this.RaisePropertyChanged(nameof(CanApply));
        }
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            SetAndRaise(ref isBusy, value);
            this.RaisePropertyChanged(nameof(CanApply));
        }
    }

    public int SelectedModeIndex
    {
        get => selectedModeIndex;
        set
        {
            SetAndRaise(ref selectedModeIndex, value);
            this.RaisePropertyChanged(nameof(FilesystemApplyText));
            this.RaisePropertyChanged(nameof(ApplyButtonText));
            UpdateReadyStatus();
        }
    }

    public int ValidCount
    {
        get => validCount;
        private set => SetAndRaise(ref validCount, value);
    }

    public int DuplicateCount
    {
        get => duplicateCount;
        private set => SetAndRaise(ref duplicateCount, value);
    }

    public int InvalidCount
    {
        get => invalidCount;
        private set
        {
            SetAndRaise(ref invalidCount, value);
            this.RaisePropertyChanged(nameof(HasInvalidLines));
        }
    }

    public int AlreadyExcludedCount
    {
        get => alreadyExcludedCount;
        private set => SetAndRaise(ref alreadyExcludedCount, value);
    }

    public int NewlyExcludedCount
    {
        get => newlyExcludedCount;
        private set => SetAndRaise(ref newlyExcludedCount, value);
    }

    public bool WillRequirePendingApply
    {
        get => willRequirePendingApply;
        private set
        {
            SetAndRaise(ref willRequirePendingApply, value);
            this.RaisePropertyChanged(nameof(FilesystemApplyText));
        }
    }

    public bool IsSoftMode
    {
        get => isSoftMode;
        private set
        {
            SetAndRaise(ref isSoftMode, value);
            this.RaisePropertyChanged(nameof(DisableModeText));
            this.RaisePropertyChanged(nameof(CanApply));
        }
    }

    public bool CreatesDisabledAsset
    {
        get => createsDisabledAsset;
        private set
        {
            SetAndRaise(ref createsDisabledAsset, value);
            this.RaisePropertyChanged(nameof(FilesystemApplyText));
            this.RaisePropertyChanged(nameof(ApplyButtonText));
        }
    }

    public string AssetName
    {
        get => assetName;
        private set => SetAndRaise(ref assetName, value);
    }

    public string SampleIds
    {
        get => sampleIds;
        private set => SetAndRaise(ref sampleIds, value);
    }

    public string InvalidLineSummary
    {
        get => invalidLineSummary;
        private set => SetAndRaise(ref invalidLineSummary, value);
    }

    public bool HasInvalidLines => InvalidCount > 0;

    public string FilesystemApplyText => IsNewMode
        ? "作成のみ（OFF）"
        : WillRequirePendingApply
        ? "GMod終了後に反映"
        : "すぐに反映";

    public string DisableModeText => IsSoftMode ? "Soft disable" : "未対応の無効化モード";

    public string ApplyButtonText => IsNewMode ? "Assetを作成" : "除外を適用";

    public bool CanApply => HasPreview && IsSoftMode && !IsBusy && !HasError;

    private bool IsNewMode => SelectedModeIndex == 2;

    public async Task LoadPreviewAsync(string path)
    {
        IsBusy = true;
        ErrorMessage = string.Empty;
        ResultMessage = string.Empty;
        HasPreview = false;
        FilePath = path;
        StatusMessage = "除外リストを読み込んでいます...";

        try
        {
            var preview = await importService.PreviewAsync(path);
            ValidCount = preview.ValidCount;
            DuplicateCount = preview.DuplicateCount;
            InvalidCount = preview.InvalidCount;
            AlreadyExcludedCount = preview.AlreadyExcludedCount;
            NewlyExcludedCount = preview.NewlyExcludedCount;
            WillRequirePendingApply = preview.WillRequirePendingApply;
            IsSoftMode = preview.IsSoftMode;
            CreatesDisabledAsset = preview.CreatesDisabledAsset;
            AssetName = preview.AssetName;
            SampleIds = string.Join(", ", preview.SampleIds);
            InvalidLineSummary = BuildInvalidLineSummary(preview);
            SelectedModeIndex = preview.Mode switch
            {
                DisableManifestMode.Replace => 1,
                DisableManifestMode.New => 2,
                _ => 0
            };
            HasPreview = true;
            StatusMessage = IsSoftMode
                ? BuildReadyStatus()
                : "この機能はSoft disableモードでのみ利用できます。";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "読み込みに失敗しました。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ApplyAsync()
    {
        if (!CanApply)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        ResultMessage = string.Empty;
        StatusMessage = "除外リストを適用しています...";
        if (IsNewMode)
        {
            StatusMessage = "除外リストAssetを作成しています...";
        }

        try
        {
            var result = await importService.ImportAsync(
                FilePath,
                new DisableManifestImportOptions
                {
                    Mode = SelectedModeIndex switch
                    {
                        1 => DisableManifestMode.Replace,
                        2 => DisableManifestMode.New,
                        _ => DisableManifestMode.Merge
                    },
                    AssetName = AssetName,
                    RequireSoftMode = true
                });

            ResultMessage = result.CreatedDisabledAsset
                ? $"除外リストAssetを作成しました\n" +
                  $"Asset: {result.AssetName}\n" +
                  $"登録ID: {result.AppliedCount}\n" +
                  $"無視した無効行: {result.InvalidCount}\n" +
                  $"状態: OFF\n" +
                  $"反映: 未適用\n\n" +
                  $"このAssetをONにすると、登録IDがGarry's Modでマウントされないようになります。"
                : $"除外リストを適用しました\n" +
                  $"適用ID: {result.AppliedCount}\n" +
                  $"新規除外: {result.NewlyExcludedCount}\n" +
                  $"既に除外済み: {result.AlreadyExcludedCount}\n" +
                  $"無視した無効行: {result.InvalidCount}\n" +
                  $"反映: {(result.AppliedImmediately ? "すぐに反映済み" : "GMod終了後に反映")}\n\n" +
                  $"取り消すには「{result.AssetName}」アセットを無効化または削除してください。";
            StatusMessage = "インポートが完了しました。";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "適用に失敗しました。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string BuildInvalidLineSummary(DisableManifestPreview preview)
    {
        if (preview.InvalidLines.Count == 0)
        {
            return "なし";
        }

        var lines = preview.InvalidLines.Count <= 5
            ? preview.InvalidLines
            : preview.InvalidLines.Take(5);

        return string.Join(
            Environment.NewLine,
            lines.Select(line => $"行 {line.LineNumber}: {line.Reason}"));
    }

    private void UpdateReadyStatus()
    {
        if (HasPreview && IsSoftMode && !IsBusy && !HasError)
        {
            StatusMessage = BuildReadyStatus();
        }
    }

    private string BuildReadyStatus()
    {
        return IsNewMode
            ? "新しいOFFのAssetを作成します。ONにすると除外が有効になります。"
            : "内容を確認してから適用してください。";
    }
}
