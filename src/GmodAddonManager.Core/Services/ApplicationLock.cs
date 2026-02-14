using System;
using System.IO;
using System.Diagnostics;

namespace GmodAddonManager.Core.Services
{
    /// <summary>
    /// アプリケーションの多重起動防止
    /// </summary>
    public class ApplicationLock : IDisposable
    {
        private FileStream? lockFileStream;
        private readonly string lockFilePath;
        
        public ApplicationLock(string addonManagerPath)
        {
            lockFilePath = Path.Combine(addonManagerPath, ".gam.lock");
        }
        
        /// <summary>
        /// ロックを取得
        /// </summary>
        public bool TryAcquireLock()
        {
            try
            {
                var lockDir = Path.GetDirectoryName(lockFilePath);
                if (!string.IsNullOrEmpty(lockDir) && !Directory.Exists(lockDir))
                {
                    Directory.CreateDirectory(lockDir);
                }

                // ロックファイルを排他的に開く
                lockFileStream = new FileStream(
                    lockFilePath, 
                    FileMode.OpenOrCreate, 
                    FileAccess.Write, 
                    FileShare.None,
                    4096,
                    FileOptions.DeleteOnClose // プロセス終了時に自動削除
                );
                
                // プロセス情報を書き込む
                using (var writer = new StreamWriter(lockFileStream, System.Text.Encoding.UTF8, -1, true))
                {
                    writer.WriteLine($"PID: {Process.GetCurrentProcess().Id}");
                    writer.WriteLine($"Started: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
                    // Use session ID instead of machine/user names for privacy
                    writer.WriteLine($"Session: {Guid.NewGuid():N}");
                    writer.Flush();
                }
                
                return true;
            }
            catch (IOException)
            {
                // ファイルが既にロックされている（他のインスタンスが実行中）
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                // アクセス権限がない
                return false;
            }
        }
        
        /// <summary>
        /// 実行中のプロセス情報を取得
        /// </summary>
        public ProcessInfo? GetRunningProcessInfo()
        {
            try
            {
                if (File.Exists(lockFilePath))
                {
                    // ロックファイルを読み取り専用で開く試み
                    using (var stream = new FileStream(lockFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(stream))
                    {
                        var pidLine = reader.ReadLine();
                        if (pidLine?.StartsWith("PID: ") == true)
                        {
                            if (int.TryParse(pidLine.Substring(5), out int pid))
                            {
                                try
                                {
                                    var process = Process.GetProcessById(pid);
                                    if (!process.HasExited)
                                    {
                                        return new ProcessInfo
                                        {
                                            ProcessId = pid,
                                            ProcessName = process.ProcessName,
                                            StartTime = process.StartTime
                                        };
                                    }
                                }
                                catch
                                {
                                    // プロセスが見つからない、または終了している
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // エラーは無視
            }
            
            return null;
        }
        
        /// <summary>
        /// ロックを解放
        /// </summary>
        public void ReleaseLock()
        {
            if (lockFileStream != null)
            {
                try
                {
                    lockFileStream.Close();
                    lockFileStream.Dispose();
                    lockFileStream = null;
                }
                catch (Exception ex)
                {
                    // Failed to close lock file stream - log but continue cleanup
                    System.Diagnostics.Debug.WriteLine($"Failed to close lock file stream: {ex.Message}");
                }
                
                // DeleteOnCloseが効かない場合の念のため
                try
                {
                    if (File.Exists(lockFilePath))
                    {
                        File.Delete(lockFilePath);
                    }
                }
                catch (Exception ex)
                {
                    // Failed to delete lock file - not critical as it's marked for delete on close
                    System.Diagnostics.Debug.WriteLine($"Failed to delete lock file: {ex.Message}");
                }
            }
            
        }
        
        public void Dispose()
        {
            ReleaseLock();
        }
        
        public class ProcessInfo
        {
            public int ProcessId { get; set; }
            public string ProcessName { get; set; } = "";
            public DateTime StartTime { get; set; }
        }
    }
}
