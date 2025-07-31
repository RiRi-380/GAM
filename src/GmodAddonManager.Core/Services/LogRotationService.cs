using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace GmodAddonManager.Core.Services
{
    public class LogRotationService
    {
        private readonly string _logDirectory;
        private readonly int _maxLogFiles;
        private readonly long _maxLogSizeBytes;

        public LogRotationService(string logDirectory, int maxLogFiles = 10, long maxLogSizeMB = 10)
        {
            _logDirectory = logDirectory;
            _maxLogFiles = maxLogFiles;
            _maxLogSizeBytes = maxLogSizeMB * 1024 * 1024;
        }

        public async Task RotateLogsAsync()
        {
            await Task.Run(() => RotateLogs());
        }

        public void RotateLogs()
        {
            if (!Directory.Exists(_logDirectory))
                return;

            try
            {
                // Get all log files
                var logFiles = Directory.GetFiles(_logDirectory, "*.log")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTime)
                    .ToList();

                // Remove old log files exceeding max count
                if (logFiles.Count > _maxLogFiles)
                {
                    foreach (var file in logFiles.Skip(_maxLogFiles))
                    {
                        try
                        {
                            file.Delete();
                        }
                        catch
                        {
                            // Ignore deletion errors
                        }
                    }
                }

                // Check current log file size
                var currentLogFile = logFiles.FirstOrDefault();
                if (currentLogFile != null && currentLogFile.Length > _maxLogSizeBytes)
                {
                    // Rotate current log file
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var rotatedFileName = Path.GetFileNameWithoutExtension(currentLogFile.Name) + $"_{timestamp}.log";
                    var rotatedPath = Path.Combine(_logDirectory, rotatedFileName);

                    try
                    {
                        File.Move(currentLogFile.FullName, rotatedPath);
                    }
                    catch
                    {
                        // Ignore rotation errors
                    }
                }

                // Clean up logs older than 30 days
                var cutoffDate = DateTime.Now.AddDays(-30);
                foreach (var file in logFiles.Where(f => f.LastWriteTime < cutoffDate))
                {
                    try
                    {
                        file.Delete();
                    }
                    catch
                    {
                        // Ignore deletion errors
                    }
                }
            }
            catch
            {
                // Ignore all rotation errors to prevent affecting main application
            }
        }
    }
}