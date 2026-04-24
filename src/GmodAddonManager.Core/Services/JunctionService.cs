using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace GmodAddonManager.Core.Services
{
    public class JunctionService : IJunctionService
    {
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint FILE_SHARE_DELETE = 0x00000004;
        private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        private const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;
        private const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
        private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
        private const int ERROR_NOT_A_REPARSE_POINT = 4390;
        private const int ERROR_PRIVILEGE_NOT_HELD = 1314;
        private const int ERROR_ACCESS_DENIED = 5;
        private const int REPARSE_DATA_BUFFER_HEADER_SIZE = 8;

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
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

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool RemoveDirectory(string lpPathName);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLink(
            string lpFileName,
            string lpExistingFileName,
            IntPtr lpSecurityAttributes);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DeleteFile(string lpFileName);

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

                Directory.CreateDirectory(absoluteJunctionPath);

                try
                {
                    ApplyJunctionReparsePoint(absoluteJunctionPath, absoluteTargetPath);
                }
                catch
                {
                    try
                    {
                        if (Directory.Exists(absoluteJunctionPath) && IsJunction(absoluteJunctionPath))
                        {
                            RemoveJunction(absoluteJunctionPath);
                        }
                        else if (Directory.Exists(absoluteJunctionPath))
                        {
                            Directory.Delete(absoluteJunctionPath, true);
                        }
                    }
                    catch
                    {
                        // ベストエフォートのクリーンアップ
                    }
                    throw;
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

        private void ApplyJunctionReparsePoint(string junctionPath, string targetPath)
        {
            string normalizedTarget = NormalizeDirectoryPath(targetPath);
            string substituteName = @"\??\" + normalizedTarget;
            string printName = normalizedTarget;

            byte[] substituteBytes = Encoding.Unicode.GetBytes(substituteName);
            byte[] printBytes = Encoding.Unicode.GetBytes(printName);

            if (substituteBytes.Length + printBytes.Length + 4 > 0x3FF0)
            {
                throw new PathTooLongException($"Target path is too long for a junction: {targetPath}");
            }

            var reparseData = new REPARSE_DATA_BUFFER
            {
                ReparseTag = IO_REPARSE_TAG_MOUNT_POINT,
                // ReparseDataLength is the size of the mount point reparse buffer (excluding the 8-byte tag/length/reserved header).
                // It must include the 8 bytes of offset/length fields plus the path buffer (substitute + null + print + null).
                ReparseDataLength = (ushort)(substituteBytes.Length + printBytes.Length + 12),
                SubstituteNameOffset = 0,
                SubstituteNameLength = (ushort)substituteBytes.Length,
                PrintNameOffset = (ushort)(substituteBytes.Length + 2),
                PrintNameLength = (ushort)printBytes.Length,
                PathBuffer = new byte[0x3FF0]
            };

            Array.Copy(substituteBytes, reparseData.PathBuffer, substituteBytes.Length);
            Array.Copy(printBytes, 0, reparseData.PathBuffer, reparseData.PrintNameOffset, printBytes.Length);

            IntPtr bufferPtr = IntPtr.Zero;
            IntPtr handle = IntPtr.Zero;

            try
            {
                handle = CreateFile(
                    junctionPath,
                    GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS,
                    IntPtr.Zero);

                if (handle.ToInt64() == -1)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (IsPrivilegeError(error))
                    {
                        throw new UnauthorizedAccessException(
                            "Failed to create junction. Enable Windows Developer Mode or run the application as administrator.",
                            new Win32Exception(error));
                    }
                    throw new Win32Exception(error, $"Failed to open junction path: {junctionPath} (Win32={error})");
                }

                int structSize = Marshal.SizeOf<REPARSE_DATA_BUFFER>();
                bufferPtr = Marshal.AllocHGlobal(structSize);
                Marshal.StructureToPtr(reparseData, bufferPtr, false);

                uint inBufferSize = (uint)(reparseData.ReparseDataLength + REPARSE_DATA_BUFFER_HEADER_SIZE);
                bool result = DeviceIoControl(
                    handle,
                    FSCTL_SET_REPARSE_POINT,
                    bufferPtr,
                    inBufferSize,
                    IntPtr.Zero,
                    0,
                    out _,
                    IntPtr.Zero);

                if (!result)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (IsPrivilegeError(error))
                    {
                        throw new UnauthorizedAccessException(
                            "Failed to create junction. Enable Windows Developer Mode or run the application as administrator.",
                            new Win32Exception(error));
                    }
                    throw new Win32Exception(error, $"Failed to create junction from '{junctionPath}' to '{targetPath}' (Win32={error})");
                }
            }
            finally
            {
                if (bufferPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(bufferPtr);
                }

                if (handle != IntPtr.Zero && handle.ToInt64() != -1)
                {
                    CloseHandle(handle);
                }
            }
        }

        private static string NormalizeDirectoryPath(string path)
        {
            string absolute = Path.GetFullPath(path);
            if (!absolute.EndsWith(Path.DirectorySeparatorChar))
            {
                absolute += Path.DirectorySeparatorChar;
            }
            return absolute;
        }

        private static bool IsPrivilegeError(int errorCode)
        {
            return errorCode == ERROR_PRIVILEGE_NOT_HELD || errorCode == ERROR_ACCESS_DENIED;
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

            string tempRoot = Path.Combine(Path.GetTempPath(), "GmodAddonManager");
            string testSuffix = Guid.NewGuid().ToString("N");
            string targetDir = Path.Combine(tempRoot, $"junction_target_{testSuffix}");
            string junctionDir = Path.Combine(tempRoot, $"junction_link_{testSuffix}");

            try
            {
                Directory.CreateDirectory(tempRoot);
                Directory.CreateDirectory(targetDir);

                CreateJunction(junctionDir, targetDir);
            }
            catch (UnauthorizedAccessException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[JunctionService] Junction self-test failed: {ex.Message}");
            }
            catch (Win32Exception ex) when (IsPrivilegeError(ex.NativeErrorCode))
            {
                System.Diagnostics.Debug.WriteLine($"[JunctionService] Junction self-test failed with privilege error: {ex.Message} (Win32={ex.NativeErrorCode})");
            }
            catch (Exception ex)
            {
                // 権限以外のエラーは初期化を止めず警告に留める
                System.Diagnostics.Debug.WriteLine($"[JunctionService] Junction self-test failed (non-privilege): {ex.Message}");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(junctionDir))
                    {
                        try { RemoveJunction(junctionDir); } catch { }
                        try { Directory.Delete(junctionDir, true); } catch { }
                    }

                    if (Directory.Exists(targetDir))
                    {
                        Directory.Delete(targetDir, true);
                    }

                    if (Directory.Exists(tempRoot) && Directory.GetFileSystemEntries(tempRoot).Length == 0)
                    {
                        Directory.Delete(tempRoot);
                    }
                }
                catch
                {
                    // ベストエフォートのクリーンアップ
                }
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

            if (!AreOnSameVolume(hardLinkPath, absoluteTargetPath))
            {
                throw new InvalidOperationException(
                    $"Hard link requires same-volume paths. Link: {hardLinkPath}, Target: {absoluteTargetPath}");
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

        private static bool AreOnSameVolume(string path1, string path2)
        {
            try
            {
                var root1 = Path.GetPathRoot(Path.GetFullPath(path1));
                var root2 = Path.GetPathRoot(Path.GetFullPath(path2));
                return string.Equals(root1, root2, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
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
