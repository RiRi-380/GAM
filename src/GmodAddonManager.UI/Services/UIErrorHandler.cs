using System;
using System.Threading.Tasks;
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
        
        public override async void HandleError(Exception ex, string context, ErrorSeverity severity = ErrorSeverity.Error)
        {
            // 基底クラスでログ記録
            base.HandleError(ex, context, severity);
            
            // UI通知 - Sanitize error message before showing to user
            if (severity >= ErrorSeverity.Error)
            {
                var sanitizedMessage = PathSanitizer.SanitizeException(ex);
                var sanitizedContext = PathSanitizer.SanitizePath(context);
                await _dialogService.ShowErrorAsync("Error", $"{sanitizedContext}: {sanitizedMessage}");
            }
            // Warningはステータスバーに表示する想定
        }
        
        public override void HandleInfo(string message, string context)
        {
            // 基底クラスでログ記録
            base.HandleInfo(message, context);
            
            // ステータスバーに表示 - Already sanitized by base class
            var sanitizedMessage = PathSanitizer.SanitizePath(message);
            ShowStatusMessage(sanitizedMessage, StatusMessageType.Info);
        }
        
        public override void HandleWarning(string message, string context)
        {
            // 基底クラスでログ記録
            base.HandleWarning(message, context);
            
            // ステータスバーに表示 - Already sanitized by base class
            var sanitizedMessage = PathSanitizer.SanitizePath(message);
            ShowStatusMessage(sanitizedMessage, StatusMessageType.Warning);
        }
        
        private void ShowStatusMessage(string message, StatusMessageType type)
        {
            try
            {
                // MainWindowViewModel経由でStatusBarViewModelにアクセス
                var mainViewModel = ViewModelLocator.MainWindowViewModel;
                if (mainViewModel?.StatusBarViewModel != null)
                {
                    mainViewModel.StatusBarViewModel.ShowMessage(message, type);
                }
            }
            catch
            {
                // ステータスバーへの表示が失敗しても処理を継続
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