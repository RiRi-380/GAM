using System;
using System.IO;
using GmodAddonManager.Core.Utils;

namespace GmodAddonManager.Core.Services
{
    public class DefaultErrorHandler : IErrorHandler
    {
        protected readonly string _logDirectory;
        private readonly LogRotationService _logRotationService;
        
        public DefaultErrorHandler()
        {
            _logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GmodAddonManager", "logs"
            );
            Directory.CreateDirectory(_logDirectory);
            _logRotationService = new LogRotationService(_logDirectory);
        }
        
        public virtual void HandleError(Exception ex, string context, ErrorSeverity severity = ErrorSeverity.Error)
        {
            // Sanitize paths in error messages
            var sanitizedMessage = PathSanitizer.SanitizePath(ex.Message);
            var sanitizedContext = PathSanitizer.SanitizePath(context);
            
            // スレッド情報を取得
            var threadInfo = $"Thread: {System.Threading.Thread.CurrentThread.ManagedThreadId} " +
                           $"(IsBackground: {System.Threading.Thread.CurrentThread.IsBackground}, " +
                           $"IsThreadPoolThread: {System.Threading.Thread.CurrentThread.IsThreadPoolThread})";
            
            var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{severity}] {sanitizedContext}\n" +
                          $"{threadInfo}\n" +
                          $"Exception Type: {ex.GetType().FullName}\n" +
                          $"Message: {sanitizedMessage}\n" +
                          $"Stack Trace:\n{ex.StackTrace}\n";
            
            // Inner exceptions
            var innerEx = ex.InnerException;
            int depth = 1;
            while (innerEx != null && depth <= 5)
            {
                logEntry += $"\n--- Inner Exception {depth} ---\n" +
                           $"Type: {innerEx.GetType().FullName}\n" +
                           $"Message: {PathSanitizer.SanitizePath(innerEx.Message)}\n" +
                           $"Stack Trace:\n{innerEx.StackTrace}\n";
                innerEx = innerEx.InnerException;
                depth++;
            }
            
            logEntry += "----------------------------------------\n";
            
            // ファイルログ
            var logFile = Path.Combine(_logDirectory, $"error_{DateTime.Now:yyyyMMdd}.log");
            try
            {
                File.AppendAllText(logFile, logEntry);
                
                // Check if log rotation is needed
                _logRotationService.RotateLogs();
            }
            catch
            {
                // ログ書き込みエラーは無視
            }
        }
        
        public virtual void HandleInfo(string message, string context)
        {
            // Sanitize paths
            var sanitizedMessage = PathSanitizer.SanitizePath(message);
            var sanitizedContext = PathSanitizer.SanitizePath(context);
            
            var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Info] {sanitizedContext}: {sanitizedMessage}\n";
            
            var logFile = Path.Combine(_logDirectory, $"info_{DateTime.Now:yyyyMMdd}.log");
            try
            {
                File.AppendAllText(logFile, logEntry);
            }
            catch
            {
                // ログ書き込みエラーは無視
            }
        }
        
        public virtual void HandleWarning(string message, string context)
        {
            // Sanitize paths
            var sanitizedMessage = PathSanitizer.SanitizePath(message);
            var sanitizedContext = PathSanitizer.SanitizePath(context);
            
            var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Warning] {sanitizedContext}: {sanitizedMessage}\n";
            
            var logFile = Path.Combine(_logDirectory, $"warning_{DateTime.Now:yyyyMMdd}.log");
            try
            {
                File.AppendAllText(logFile, logEntry);
            }
            catch
            {
                // ログ書き込みエラーは無視
            }
        }
    }
}