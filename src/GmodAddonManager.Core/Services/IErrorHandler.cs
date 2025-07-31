using System;

namespace GmodAddonManager.Core.Services
{
    public enum ErrorSeverity
    {
        Info,
        Warning,
        Error,
        Critical
    }

    public interface IErrorHandler
    {
        void HandleError(Exception ex, string context, ErrorSeverity severity = ErrorSeverity.Error);
        void HandleInfo(string message, string context);
        void HandleWarning(string message, string context);
    }
}