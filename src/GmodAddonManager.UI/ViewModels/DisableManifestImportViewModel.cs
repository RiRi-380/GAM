using System;
using System.Linq;
using System.Threading.Tasks;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using ReactiveUI;

namespace GmodAddonManager.UI.ViewModels;

public sealed class DisableManifestImportViewModel : ViewModelBase
{
    private readonly IDisableManifestImportService importService;
    private string filePath = string.Empty;
    private string statusMessage = L.Get("DisableManifest.SelectFileStatus");
    private string errorMessage = string.Empty;
    private bool hasPreview;
    private bool isBusy;
    private int validCount;
    private int duplicateCount;
    private int invalidCount;
    private bool isSoftMode = true;
    private string assetName = DisableManifest.DefaultName;
    private string sampleIds = string.Empty;
    private string invalidLineSummary = string.Empty;

    public DisableManifestImportViewModel(IDisableManifestImportService importService)
    {
        this.importService = importService ?? throw new ArgumentNullException(nameof(importService));
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

    public int ValidCount
    {
        get => validCount;
        private set
        {
            SetAndRaise(ref validCount, value);
            this.RaisePropertyChanged(nameof(CanApply));
        }
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

    public string DisableModeText => IsSoftMode
        ? L.Get("DisableManifest.SoftMode")
        : L.Get("DisableManifest.SoftModeOnly");

    public string CanApplyText => L.Get("DisableManifest.CreateExcludedAsset");

    public bool CanApply => HasPreview && IsSoftMode && ValidCount > 0 && !IsBusy && !HasError;

    public async Task LoadPreviewAsync(string path)
    {
        IsBusy = true;
        ErrorMessage = string.Empty;
        HasPreview = false;
        FilePath = path;
        StatusMessage = L.Get("DisableManifest.Loading");

        try
        {
            var preview = await importService.PreviewAsync(path);
            ValidCount = preview.ValidCount;
            DuplicateCount = preview.DuplicateCount;
            InvalidCount = preview.InvalidCount;
            IsSoftMode = preview.IsSoftMode;
            AssetName = preview.AssetName;
            SampleIds = string.Join(", ", preview.SampleIds);
            InvalidLineSummary = BuildInvalidLineSummary(preview);
            HasPreview = true;
            StatusMessage = IsSoftMode
                ? L.Get("DisableManifest.Ready")
                : L.Get("DisableManifest.SoftModeOnlyDetail");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = L.Get("DisableManifest.LoadFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> ApplyAsync()
    {
        if (!CanApply)
        {
            return false;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        StatusMessage = L.Get("DisableManifest.Importing");

        try
        {
            await importService.ImportAsync(
                FilePath,
                new DisableManifestImportOptions
                {
                    Mode = DisableManifestMode.New,
                    AssetName = AssetName,
                    RequireSoftMode = true
                });

            StatusMessage = L.Get("DisableManifest.ImportComplete");
            return true;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = L.Get("DisableManifest.ImportFailedStatus");
            return false;
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
            return L.Get("DisableManifest.None");
        }

        var lines = preview.InvalidLines.Count <= 5
            ? preview.InvalidLines
            : preview.InvalidLines.Take(5);

        return string.Join(
            Environment.NewLine,
            lines.Select(line => L.Format("DisableManifest.InvalidLineFormat", line.LineNumber, line.Reason)));
    }
}
