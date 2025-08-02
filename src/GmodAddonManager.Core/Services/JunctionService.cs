using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GmodAddonManager.Core.Services
{
    public class JunctionService : IJunctionService
    {
        private const uint GENERIC_READ = 0x80000000;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        private const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;
        private const int ERROR_NOT_A_REPARSE_POINT = 4390;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool CreateSymbolicLink(
            string lpSymlinkFileName,
            string lpTargetFileName,
            int dwFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool RemoveDirectory(string lpPathName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLink(
            string lpFileName,
            string lpExistingFileName,
            IntPtr lpSecurityAttributes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DeleteFile(string lpFileName);

        private const int SYMBOLIC_LINK_FLAG_DIRECTORY = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct REPARSE_DATA_BUFFER
        {
            public uint ReparseTag;
            public ushort ReparseDataLength;
            public ushort Reserved;
            public ushort SubstituteNameOffset;
            public ushort SubstituteNameLength;
            public ushort PrintNameOffset;
            public ushort PrintNameLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 0x3FF0)]
            public byte[] PathBuffer;
        }

        public void CreateJunction(string junctionPath, string targetPath)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new PlatformNotSupportedException("Junction points are only supported on Windows.");
            }

            // パスの検証
            ValidatePath(junctionPath, "junctionPath");
            ValidatePath(targetPath, "targetPath");

            // Get absolute paths to prevent TOCTOU attacks
            string absoluteJunctionPath = Path.GetFullPath(junctionPath);
            string absoluteTargetPath = Path.GetFullPath(targetPath);

            // Verify target exists before any operations
            if (!Directory.Exists(absoluteTargetPath))
            {
                throw new DirectoryNotFoundException($"Target directory does not exist: {absoluteTargetPath}");
            }

            // Use a lock to prevent race conditions
            lock (this)
            {
                if (Directory.Exists(absoluteJunctionPath))
                {
                    if (IsJunction(absoluteJunctionPath))
                    {
                        // 既存のジャンクションがある場合、それが同じターゲットを指しているか確認
                        try
                        {
                            string existingTarget = GetJunctionTarget(absoluteJunctionPath);
                            if (string.Equals(existingTarget, absoluteTargetPath, StringComparison.OrdinalIgnoreCase))
                            {
                                // 同じターゲットを指している場合は何もしない
                                return;
                            }
                        }
                        catch
                        {
                            // If we can't read the target, remove and recreate
                        }
                        
                        // 異なるターゲットの場合は再作成
                        RemoveJunction(absoluteJunctionPath);
                    }
                    else
                    {
                        // 通常のディレクトリが存在する場合は削除して再作成
                        // Steamが無効化されたアドオンのディレクトリを作成することがあるため
                        try
                        {
                            // ディレクトリが空かチェック
                            if (Directory.GetFileSystemEntries(absoluteJunctionPath).Length == 0)
                            {
                                // 空のディレクトリなら削除
                                Directory.Delete(absoluteJunctionPath, false);
                            }
                            else
                            {
                                // 空でない場合は移動して後で削除
                                string backupPath = absoluteJunctionPath + "_backup_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                                Directory.Move(absoluteJunctionPath, backupPath);
                                // 後で削除を試みる（ベストエフォート）
                                Task.Run(async () =>
                                {
                                    await Task.Delay(5000);
                                    try { Directory.Delete(backupPath, true); } catch { }
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            throw new IOException($"Path already exists and is not a junction: {junctionPath}. Failed to remove existing directory: {ex.Message}", ex);
                        }
                    }
                }

                // Windows APIを直接使用してジャンクションを作成
                if (!CreateSymbolicLink(absoluteJunctionPath, absoluteTargetPath, SYMBOLIC_LINK_FLAG_DIRECTORY))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new Win32Exception(error, $"Failed to create junction from '{junctionPath}' to '{absoluteTargetPath}'");
                }
            }
        }

        public void RemoveJunction(string junctionPath)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new PlatformNotSupportedException("Junction points are only supported on Windows.");
            }

            if (!Directory.Exists(junctionPath))
            {
                return;
            }

            if (!IsJunction(junctionPath))
            {
                throw new IOException($"Path is not a junction: {junctionPath}");
            }

            bool result = RemoveDirectory(junctionPath);
            if (!result)
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, $"Failed to remove junction: {junctionPath}");
            }
        }

        public bool IsJunction(string path)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return false;
            }

            if (!Directory.Exists(path))
            {
                return false;
            }

            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                return (attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            }
            catch
            {
                return false;
            }
        }

        public string GetJunctionTarget(string junctionPath)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new PlatformNotSupportedException("Junction points are only supported on Windows.");
            }

            if (!IsJunction(junctionPath))
            {
                throw new IOException($"Path is not a junction: {junctionPath}");
            }

            IntPtr handle = CreateFile(
                junctionPath,
                GENERIC_READ,
                0,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS,
                IntPtr.Zero);

            if (handle.ToInt64() == -1)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                int bufferSize = Marshal.SizeOf(typeof(REPARSE_DATA_BUFFER));
                IntPtr buffer = Marshal.AllocHGlobal(bufferSize);

                try
                {
                    uint bytesReturned;
                    bool result = DeviceIoControl(
                        handle,
                        FSCTL_GET_REPARSE_POINT,
                        IntPtr.Zero,
                        0,
                        buffer,
                        (uint)bufferSize,
                        out bytesReturned,
                        IntPtr.Zero);

                    if (!result)
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == ERROR_NOT_A_REPARSE_POINT)
                        {
                            throw new IOException("Path is not a reparse point.");
                        }
                        throw new Win32Exception(error);
                    }

                    REPARSE_DATA_BUFFER reparseData = Marshal.PtrToStructure<REPARSE_DATA_BUFFER>(buffer);
                    
                    string targetPath = System.Text.Encoding.Unicode.GetString(
                        reparseData.PathBuffer,
                        reparseData.SubstituteNameOffset,
                        reparseData.SubstituteNameLength);

                    if (targetPath.StartsWith(@"\??\"))
                    {
                        targetPath = targetPath.Substring(4);
                    }

                    return targetPath;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        public void ValidateAdminPrivileges()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            bool isAdmin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

            if (!isAdmin)
            {
                throw new UnauthorizedAccessException("Administrator privileges are required to create junction points.");
            }
        }

        public void CreateHardLink(string hardLinkPath, string targetPath)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new PlatformNotSupportedException("Hard links are only supported on Windows.");
            }

            // パスの検証
            ValidatePath(hardLinkPath, "hardLinkPath");
            ValidatePath(targetPath, "targetPath");

            if (File.Exists(hardLinkPath))
            {
                // ハードリンクかどうかを確認
                if (IsHardLink(hardLinkPath, targetPath))
                {
                    // 同じターゲットを指している場合は何もしない
                    return;
                }
                else
                {
                    // 既存のファイルを削除
                    File.Delete(hardLinkPath);
                }
            }

            string absoluteTargetPath = Path.GetFullPath(targetPath);
            if (!File.Exists(absoluteTargetPath))
            {
                throw new FileNotFoundException($"Target file does not exist: {absoluteTargetPath}");
            }

            // ディレクトリが存在しない場合は作成
            string directory = Path.GetDirectoryName(hardLinkPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            bool result = CreateHardLink(hardLinkPath, absoluteTargetPath, IntPtr.Zero);
            if (!result)
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, $"Failed to create hard link from '{hardLinkPath}' to '{absoluteTargetPath}'");
            }
        }

        public void RemoveHardLink(string hardLinkPath)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new PlatformNotSupportedException("Hard links are only supported on Windows.");
            }

            if (!File.Exists(hardLinkPath))
            {
                return;
            }

            bool result = DeleteFile(hardLinkPath);
            if (!result)
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, $"Failed to remove hard link: {hardLinkPath}");
            }
        }

        private void ValidatePath(string path, string paramName)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Path cannot be null or empty.", paramName);
            }

            // 危険な文字のチェック
            char[] invalidChars = Path.GetInvalidPathChars();
            if (path.IndexOfAny(invalidChars) >= 0)
            {
                throw new ArgumentException($"Path contains invalid characters: {path}", paramName);
            }

            // パストラバーサルの防止 - より厳密なチェック
            try
            {
                string fullPath = Path.GetFullPath(path);
                
                // Check for path traversal attempts
                if (path.Contains("..", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Path traversal detected in: {path}", paramName);
                }
                
                // Additional check for Windows special paths
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // Check for UNC paths, device paths, and other special formats
                    if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) && path.Length > 1 && path[1] == '\\')
                    {
                        throw new ArgumentException($"Special path format not allowed: {path}", paramName);
                    }
                    
                    // Check for alternate data streams
                    if (path.Contains(":", StringComparison.Ordinal) && !Path.IsPathRooted(path))
                    {
                        throw new ArgumentException($"Alternate data streams not allowed: {path}", paramName);
                    }
                }
            }
            catch (Exception ex) when (!(ex is ArgumentException))
            {
                throw new ArgumentException($"Invalid path format: {path}", paramName, ex);
            }
        }

        public bool IsHardLink(string filePath, string targetPath)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return false;
            }

            if (!File.Exists(filePath) || !File.Exists(targetPath))
            {
                return false;
            }

            try
            {
                // 簡易的な実装：ファイルの内容が同じかどうかを確認
                // 本来はファイルのインデックスを比較すべきだが、簡易実装とする
                var fileInfo1 = new FileInfo(filePath);
                var fileInfo2 = new FileInfo(targetPath);

                // サイズが同じかチェック
                if (fileInfo1.Length != fileInfo2.Length)
                {
                    return false;
                }

                // 最終更新日時が同じかチェック（ハードリンクは同じになる）
                return fileInfo1.LastWriteTimeUtc == fileInfo2.LastWriteTimeUtc;
            }
            catch
            {
                return false;
            }
        }

        public void CreateWorkshopAddonStructure(string workshopPath, string addonId, string managedGmaPath)
        {
            // Steam が期待するディレクトリ構造を作成
            // /steamapps/workshop/content/4000/{addonId}/
            //                                          └─ {addonId}.gma (ハードリンク)
            
            string addonDirectory = Path.Combine(workshopPath, addonId);
            string gmaPath = Path.Combine(addonDirectory, $"{addonId}.gma");

            // ディレクトリを作成（通常のディレクトリとして）
            if (!Directory.Exists(addonDirectory))
            {
                Directory.CreateDirectory(addonDirectory);
            }
            else if (File.GetAttributes(addonDirectory).HasFlag(FileAttributes.ReparsePoint))
            {
                // ジャンクションが存在する場合は削除して通常のディレクトリを作成
                RemoveJunction(addonDirectory);
                Directory.CreateDirectory(addonDirectory);
            }

            // GMAファイルへのハードリンクを作成
            CreateHardLink(gmaPath, managedGmaPath);
        }

        public void RemoveWorkshopAddonStructure(string workshopPath, string addonId)
        {
            string addonDirectory = Path.Combine(workshopPath, addonId);
            
            if (!Directory.Exists(addonDirectory))
                return;
                
            try
            {
                // まずハードリンクを削除
                string gmaPath = Path.Combine(addonDirectory, $"{addonId}.gma");
                if (File.Exists(gmaPath))
                {
                    try
                    {
                        // ファイル属性をクリア
                        File.SetAttributes(gmaPath, FileAttributes.Normal);
                        
                        // ハードリンクかどうかに関わらず削除
                        // （ハードリンクの場合でも通常のFile.Deleteで削除可能）
                        File.Delete(gmaPath);
                    }
                    catch (Exception ex)
                    {
                        // ファイル削除に失敗しても続行
                    }
                }
                
                // ディレクトリ内のすべてのファイルの属性をクリア
                foreach (var file in Directory.GetFiles(addonDirectory, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    catch (Exception ex)
                    {
                        // Failed to clear file attributes - log but continue with deletion attempt
                        System.Diagnostics.Debug.WriteLine($"Failed to clear attributes for {file}: {ex.Message}");
                    }
                }
                
                // ディレクトリを削除
                Directory.Delete(addonDirectory, true);
            }
            catch (UnauthorizedAccessException ex)
            {
                // アクセス拒否の場合、属性を変更して再試行
                try
                {
                    // ディレクトリ自体の属性を変更
                    var dirInfo = new DirectoryInfo(addonDirectory);
                    dirInfo.Attributes = FileAttributes.Normal;
                    
                    // 再度削除を試みる
                    Directory.Delete(addonDirectory, true);
                }
                catch (Exception retryEx)
                {
                    throw new InvalidOperationException(
                        $"Failed to remove workshop addon structure for {addonId} on drive {Path.GetPathRoot(addonDirectory)}: {retryEx.Message}", 
                        retryEx
                    );
                }
            }
            catch (IOException ex)
            {
                // ファイルが使用中の場合
                throw new InvalidOperationException(
                    $"Failed to remove workshop addon structure for {addonId}: Files may be in use. {ex.Message}", 
                    ex
                );
            }
            catch (Exception ex)
            {
                // その他のエラー
                throw new InvalidOperationException(
                    $"Failed to remove workshop addon structure for {addonId}: {ex.Message}", 
                    ex
                );
            }
        }
    }
}