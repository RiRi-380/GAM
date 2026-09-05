using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using GmodAddonManager.UI.Services;

namespace GmodAddonManager.UI.Views;

public partial class DiagnosticReportDialog : Window
{
    private readonly Func<Task<string?>> selectDestination;
    private readonly Func<string, string, Task> save;
    private bool saving;

    public DiagnosticReportDialog() : this(string.Empty) { }

    public DiagnosticReportDialog(string report)
    {
        InitializeComponent();
        ReportTextBox.Text = report;
        selectDestination = SelectDestinationAsync;
        save = DiagnosticReportService.SaveAsync;
    }

    internal DiagnosticReportDialog(string report, Func<Task<string?>> selectDestination,
        Func<string, string, Task> save) : this(report)
    {
        this.selectDestination = selectDestination;
        this.save = save;
    }

    private async Task<string?> SelectDestinationAsync()
    {
        if (!StorageProvider.CanSave) throw new InvalidOperationException();
        using var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L.Get("Diagnostics.Save"),
            SuggestedFileName = $"GAM-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            DefaultExtension = "txt",
            ShowOverwritePrompt = true,
            FileTypeChoices = new[] { new FilePickerFileType(L.Get("Diagnostics.TextFile")) { Patterns = new[] { "*.txt" } } }
        });
        if (file == null) return null;
        return file.TryGetLocalPath() ?? throw new InvalidOperationException();
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        try
        {
            await SaveReportAsync();
        }
        catch (Exception)
        {
            ShowResult("Diagnostics.SaveFailed");
        }
    }

    internal async Task SaveReportAsync()
    {
        if (saving) return;
        saving = true;
        SaveButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        ResultText.IsVisible = false;
        try
        {
            var path = await selectDestination();
            if (path == null) return;
            await save(path, ReportTextBox.Text ?? string.Empty);
            ShowResult("Diagnostics.Saved");
        }
        catch (Exception)
        {
            // Keep paths and exception messages out of the report and dialog.
            ShowResult("Diagnostics.SaveFailed");
        }
        finally
        {
            saving = false;
            SaveButton.IsEnabled = true;
            CloseButton.IsEnabled = true;
        }
    }

    private void ShowResult(string key)
    {
        ResultText.Text = L.Get(key);
        ResultText.IsVisible = true;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
