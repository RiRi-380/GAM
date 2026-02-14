using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using GmodAddonManager.Core.Services;
using GmodAddonManager.Core.Utils;
using GmodAddonManager.UI.ViewModels;

namespace GmodAddonManager.UI.Services
{
    public class UIErrorHandler : DefaultErrorHandler
    {
        private readonly IDialogService _dialogService;

        public UIErrorHandler(IDialogService dialogService) : base()
        {
            _dialogService = dialogService;
        }

        public override void HandleError(Exception ex, string context, ErrorSeverity severity = ErrorSeverity.Error)
        {
            base.HandleError(ex, context, severity);

            if (severity >= ErrorSeverity.Error)
            {
                var sanitizedMessage = PathSanitizer.SanitizeException(ex);
                var sanitizedContext = PathSanitizer.SanitizePath(context);
                Dispatcher.UIThread.Post(() =>
                {
                    _ = ShowErrorSafeAsync(L.Get("Error.Title"), $"{sanitizedContext}: {sanitizedMessage}");
                });
            }
        }

        public override void HandleInfo(string message, string context)
        {
            base.HandleInfo(message, context);

            var sanitizedMessage = PathSanitizer.SanitizePath(message);
            Dispatcher.UIThread.Post(() => ShowStatusMessage(sanitizedMessage, StatusMessageType.Info));
        }

        public override void HandleWarning(string message, string context)
        {
            base.HandleWarning(message, context);

            var sanitizedMessage = PathSanitizer.SanitizePath(message);
            Dispatcher.UIThread.Post(() => ShowStatusMessage(sanitizedMessage, StatusMessageType.Warning));
        }

        private void ShowStatusMessage(string message, StatusMessageType type)
        {
            try
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var mainViewModel = ViewModelLocator.MainWindowViewModel;
                    if (mainViewModel?.StatusBarViewModel != null)
                    {
                        mainViewModel.StatusBarViewModel.ShowMessage(message, type);
                    }
                });
            }
            catch
            {
                // Ignore status-bar UI failures.
            }
        }

        private async Task ShowErrorSafeAsync(string title, string message)
        {
            try
            {
                await _dialogService.ShowErrorAsync(title, message);
            }
            catch (Exception ex)
            {
                SafeFileLogger.TryLogException("UIErrorHandler.ShowErrorSafeAsync", ex);
            }
        }
    }

    public enum StatusMessageType
    {
        Info,
        Warning,
        Error,
        Success
    }
}
