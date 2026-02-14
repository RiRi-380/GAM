using System;
using System.IO;
using System.Text.RegularExpressions;

namespace GmodAddonManager.Core.Utils
{
    public static class PathSanitizer
    {
        private static readonly Regex PathRegex = new Regex(@"[A-Za-z]:[\\\/][\s\S]*?(?=[\\\/]|$)", RegexOptions.Compiled);
        private static readonly Regex UserNameRegex = new Regex(@"(?i)(?:Users?[\\\/])([^\\\/]+)", RegexOptions.Compiled);
        private static readonly Regex UnixHomeRegex = new Regex(@"(?i)(?:\/home\/)([^\/]+)", RegexOptions.Compiled);
        
        public static string SanitizePath(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            // First, replace usernames in paths
            input = UserNameRegex.Replace(input, "Users\\{User}");
            input = UnixHomeRegex.Replace(input, "/home/{User}");

            // Replace full paths with relative paths
            input = PathRegex.Replace(input, match =>
            {
                var path = match.Value;
                
                // Check for common paths and replace with placeholders
                if (path.IndexOf("AppData", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "{AppData}";
                if (path.IndexOf("Program Files", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "{ProgramFiles}";
                if (path.IndexOf("Steam", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "{Steam}";
                if (path.IndexOf("Users", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "{UserProfile}";
                
                // For other paths, just show the last directory/file
                try
                {
                    return Path.GetFileName(path) ?? "{Path}";
                }
                catch
                {
                    return "{Path}";
                }
            });

            return input;
        }

        public static string SanitizeException(Exception ex, bool includeStackTrace = false)
        {
            if (ex == null)
                return string.Empty;

            var message = SanitizePath(ex.Message);
            
#if DEBUG
            if (includeStackTrace && ex.StackTrace != null)
            {
                return $"{message}\n\nStack Trace:\n{ex.StackTrace}";
            }
#endif
            
            return message;
        }
    }
}