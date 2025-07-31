using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.ReactiveUI;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Tasks;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.Models;
using System.IO;
using System.Text;

namespace GmodAddonManager.UI;

class Program
{
    [DllImport("kernel32.dll")]
    static extern bool AllocConsole();
    
    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);
    
    [DllImport("kernel32.dll")]
    static extern bool FreeConsole();
    
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetConsoleCP(uint wCodePageID);
    
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetConsoleOutputCP(uint wCodePageID);
    
    [STAThread]
    public static void Main(string[] args)
    {
        // 設定を読み込んでコンソール表示を判断
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var settings = AppSettings.Load();
            if (settings.ShowConsoleOnStartup)
            {
                // 親プロセスのコンソールにアタッチを試みる
                if (!AttachConsole(-1))
                {
                    // アタッチできない場合は新しいコンソールを作成
                    AllocConsole();
                }
                
                // Windowsコンソールのデフォルトエンコーディングを使用
                // 日本語WindowsではShift-JIS (CP932)がデフォルト
                // UTF-8設定を削除し、システムデフォルトを使用
                
                // 標準出力をコンソールにリダイレクト
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), Encoding.UTF8) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError(), Encoding.UTF8) { AutoFlush = true });
                Console.SetIn(new StreamReader(Console.OpenStandardInput(), Encoding.UTF8));
                
                // Console.WriteLine("=== Gmod Addon Manager Debug Console ===");
                // Console.WriteLine($"Started at: {DateTime.Now}");
                // Console.WriteLine($"Version: {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
                // Console.WriteLine("========================================\n");
            }
        }
        // 管理者権限チェックを一時的に無効化
        /*
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = Process.GetCurrentProcess().MainModule.FileName,
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = string.Join(" ", args)
                };
                
                try
                {
                    Process.Start(startInfo);
                    Environment.Exit(0);
                }
                catch (Win32Exception)
                {
                    // Avalonia UIでのダイアログ表示
                    BuildAvaloniaApp()
                        .AfterSetup(_ =>
                        {
                            var window = new Window
                            {
                                Title = "エラー",
                                Width = 400,
                                Height = 150,
                                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                                Content = new StackPanel
                                {
                                    Margin = new Thickness(20),
                                    Children =
                                    {
                                        new TextBlock 
                                        { 
                                            Text = "このアプリケーションは管理者権限が必要です。",
                                            TextWrapping = TextWrapping.Wrap,
                                            Margin = new Thickness(0, 0, 0, 20)
                                        },
                                        new Button 
                                        { 
                                            Content = "OK",
                                            HorizontalAlignment = HorizontalAlignment.Center,
                                            Width = 80
                                        }
                                    }
                                }
                            };
                            ((Button)((StackPanel)window.Content).Children[1]).Click += (_, _) => window.Close();
                            window.ShowDialog(null);
                        })
                        .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
                    Environment.Exit(1);
                }
            }
        }
        */
        
        // グローバル例外ハンドラーの設定
        SetupGlobalExceptionHandlers();
        
        // グレースフルシャットダウンの設定
        SetupGracefulShutdown();
        
        try
        {
#if DEBUG
            // 起動ログ
            var startupLogPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup_log.txt");
            System.IO.File.WriteAllText(startupLogPath, $"Starting at: {DateTime.Now}\nBase Directory: {AppDomain.CurrentDomain.BaseDirectory}\nCurrent Directory: {System.IO.Directory.GetCurrentDirectory()}\n");
#endif
            
#if DEBUG
            // Very early startup log
            System.IO.File.WriteAllText("app_startup.log", $"Program.Main started at: {DateTime.Now}\n");
#endif
            
            var appBuilder = BuildAvaloniaApp();
#if DEBUG
            System.IO.File.AppendAllText("app_startup.log", $"AppBuilder created at: {DateTime.Now}\n");
#endif
            
            appBuilder.StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
#if DEBUG
            // エラーログをファイルに出力
            var errorPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup_error.log");
            System.IO.File.WriteAllText(errorPath, $"Error at: {DateTime.Now}\n{ex.ToString()}");
            System.IO.File.AppendAllText("app_startup.log", $"Fatal error in Main at: {DateTime.Now}\n{ex.ToString()}\n");
            // Console.WriteLine($"Error written to: {errorPath}");
            // Console.WriteLine(ex.ToString());
#endif
            throw;
        }
    }

    private static void SetupGlobalExceptionHandlers()
    {
        // 未処理の例外をキャッチ
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            var exception = e.ExceptionObject as Exception;
            var errorHandler = GetErrorHandler();
            
            if (errorHandler != null)
            {
                errorHandler.HandleError(
                    exception ?? new Exception("Unknown error"),
                    "Unhandled Exception",
                    ErrorSeverity.Critical
                );
            }
            else
            {
                // フォールバック: ファイルにログを記録
                LogUnhandledException(exception);
            }
            
            // クリティカルエラーの場合、ユーザーに通知してから終了
            if (e.IsTerminating)
            {
                ShowCriticalErrorDialog(exception).Wait();
            }
        };
        
        // 非同期タスクの未処理例外をキャッチ
        TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            var errorHandler = GetErrorHandler();
            
            if (errorHandler != null)
            {
                foreach (var ex in e.Exception.InnerExceptions)
                {
                    errorHandler.HandleError(
                        ex,
                        "Unobserved Task Exception",
                        ErrorSeverity.Error
                    );
                }
            }
            else
            {
                // フォールバック: ファイルにログを記録
                LogUnhandledException(e.Exception);
            }
            
            e.SetObserved(); // 例外を処理済みとしてマーク
        };
    }
    
    private static IErrorHandler? GetErrorHandler()
    {
        try
        {
            // ViewModelLocatorからErrorHandlerを取得
            return ViewModelLocator.ErrorHandler;
        }
        catch
        {
            return null;
        }
    }
    
    private static void SetupGracefulShutdown()
    {
        // Ctrl+Cハンドラー
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true; // 即座に終了しない
            PerformGracefulShutdown();
        };
        
        // プロセス終了イベント
        AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
        {
            PerformGracefulShutdown();
        };
        
        // アプリケーション終了時のフック（Windows向け）
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                if (e.IsTerminating)
                {
                    PerformGracefulShutdown();
                }
            };
        }
    }
    
    private static void PerformGracefulShutdown()
    {
        try
        {
            // 現在の操作状態を保存
            var errorHandler = GetErrorHandler();
            errorHandler?.HandleInfo("Performing graceful shutdown...", "Shutdown");
            
            // Avaloniaアプリケーションのシャットダウン
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }
        catch (Exception ex)
        {
            LogUnhandledException(ex);
        }
    }
    
    private static void LogUnhandledException(Exception? exception)
    {
        try
        {
            var logDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GmodAddonManager",
                "logs"
            );
            
            if (!System.IO.Directory.Exists(logDir))
                System.IO.Directory.CreateDirectory(logDir);
            
            var logFile = System.IO.Path.Combine(logDir, $"error_{DateTime.Now:yyyyMMdd}.log");
            
            var threadInfo = $"Thread: {System.Threading.Thread.CurrentThread.ManagedThreadId} " +
                           $"(IsBackground: {System.Threading.Thread.CurrentThread.IsBackground}, " +
                           $"IsThreadPoolThread: {System.Threading.Thread.CurrentThread.IsThreadPoolThread})";
            
            var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Critical] Unhandled Exception\n" +
                          $"{threadInfo}\n" +
                          $"Exception Type: {exception?.GetType().FullName ?? "Unknown"}\n" +
                          $"Message: {exception?.Message ?? "No message"}\n" +
                          $"Stack Trace:\n{exception?.StackTrace ?? "No stack trace"}\n";
            
            // Inner exceptions
            var innerEx = exception?.InnerException;
            int depth = 1;
            while (innerEx != null && depth <= 5)
            {
                logEntry += $"\n--- Inner Exception {depth} ---\n" +
                           $"Type: {innerEx.GetType().FullName}\n" +
                           $"Message: {innerEx.Message}\n" +
                           $"Stack Trace:\n{innerEx.StackTrace}\n";
                innerEx = innerEx.InnerException;
                depth++;
            }
            
            logEntry += "\n========================================\n\n";
            
            System.IO.File.AppendAllText(logFile, logEntry);
        }
        catch
        {
            // ログ書き込みエラーは無視
        }
    }
    
    private static async Task ShowCriticalErrorDialog(Exception? exception)
    {
        try
        {
            var app = Application.Current;
            if (app?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var errorWindow = new Window
                {
                    Title = "Critical Error",
                    Width = 500,
                    Height = 300,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Content = new StackPanel
                    {
                        Margin = new Thickness(20),
                        Children =
                        {
                            new TextBlock 
                            { 
                                Text = "A critical error has occurred and the application must close.",
                                TextWrapping = TextWrapping.Wrap,
                                FontWeight = FontWeight.Bold,
                                Margin = new Thickness(0, 0, 0, 10)
                            },
                            new TextBlock
                            {
                                Text = exception?.Message ?? "Unknown error",
                                TextWrapping = TextWrapping.Wrap,
                                Margin = new Thickness(0, 0, 0, 10)
                            },
                            new TextBlock
                            {
                                Text = "Error details have been saved to the application logs.",
                                TextWrapping = TextWrapping.Wrap,
                                FontSize = 11,
                                Foreground = Brushes.Gray,
                                Margin = new Thickness(0, 0, 0, 20)
                            },
                            new Button 
                            { 
                                Content = "OK",
                                HorizontalAlignment = HorizontalAlignment.Center,
                                Width = 100
                            }
                        }
                    }
                };
                
                var button = (Button)((StackPanel)errorWindow.Content).Children[3];
                button.Click += (_, _) => errorWindow.Close();
                
                await errorWindow.ShowDialog(desktop.MainWindow ?? errorWindow);
            }
        }
        catch
        {
            // ダイアログ表示エラーは無視
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseReactiveUI()
            .LogToTrace();
}