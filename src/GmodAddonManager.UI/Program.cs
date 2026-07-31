using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.Models;
using System.IO;
using System.Text;

namespace GmodAddonManager.UI;

class Program
{
    private static int shutdownRequested;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    static extern bool AllocConsole();
    
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);
    
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    static extern bool FreeConsole();
    
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetConsoleCP(uint wCodePageID);
    
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetConsoleOutputCP(uint wCodePageID);
    
    [STAThread]
    public static void Main(string[] args)
    {
        if (!RestartHandoff.TryWaitForPreviousProcess(args, out var applicationArgs))
        {
            SafeFileLogger.TryLogInfo(
                "Program.Main",
                "Restart handoff failed or timed out; startup was cancelled to avoid overlapping instances.");
            return;
        }

        args = applicationArgs;

        try
        {
            // Keep relative logs/resources under the app output directory.
            Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;
        }
        catch
        {
            // Best effort only.
        }
        // 險ｭ螳壹ｒ隱ｭ縺ｿ霎ｼ繧薙〒繧ｳ繝ｳ繧ｽ繝ｼ繝ｫ陦ｨ遉ｺ繧貞愛譁ｭ
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var settings = AppSettings.Load();
            if (settings.ShowConsoleOnStartup)
            {
                // 隕ｪ繝励Ο繧ｻ繧ｹ縺ｮ繧ｳ繝ｳ繧ｽ繝ｼ繝ｫ縺ｫ繧｢繧ｿ繝・メ繧定ｩｦ縺ｿ繧・
                if (!AttachConsole(-1))
                {
                    // 繧｢繧ｿ繝・メ縺ｧ縺阪↑縺・ｴ蜷医・譁ｰ縺励＞繧ｳ繝ｳ繧ｽ繝ｼ繝ｫ繧剃ｽ懈・
                    AllocConsole();
                }
                
                // Windows繧ｳ繝ｳ繧ｽ繝ｼ繝ｫ縺ｮ繝・ヵ繧ｩ繝ｫ繝医お繝ｳ繧ｳ繝ｼ繝・ぅ繝ｳ繧ｰ繧剃ｽｿ逕ｨ
                // 譌･譛ｬ隱杆indows縺ｧ縺ｯShift-JIS (CP932)縺後ョ繝輔か繝ｫ繝・
                // UTF-8險ｭ螳壹ｒ蜑企勁縺励√す繧ｹ繝・Β繝・ヵ繧ｩ繝ｫ繝医ｒ菴ｿ逕ｨ
                
                // 讓呎ｺ門・蜉帙ｒ繧ｳ繝ｳ繧ｽ繝ｼ繝ｫ縺ｫ繝ｪ繝繧､繝ｬ繧ｯ繝・
                Console.OutputEncoding = Encoding.UTF8;
                Console.InputEncoding = Encoding.UTF8;
                
                // Console.WriteLine("=== Gmod Addon Manager Debug Console ===");
                // Console.WriteLine($"Started at: {DateTime.Now}");
                // Console.WriteLine($"Version: {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
                // Console.WriteLine("========================================\n");
            }
        }
        // 邂｡逅・・ｨｩ髯舌メ繧ｧ繝・け繧剃ｸ譎ら噪縺ｫ辟｡蜉ｹ蛹・
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
                    // Avalonia UI縺ｧ縺ｮ繝繧､繧｢繝ｭ繧ｰ陦ｨ遉ｺ
                    BuildAvaloniaApp()
                        .AfterSetup(_ =>
                        {
                            var window = new Window
                            {
                                Title = "繧ｨ繝ｩ繝ｼ",
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
                                            Text = "縺薙・繧｢繝励Μ繧ｱ繝ｼ繧ｷ繝ｧ繝ｳ縺ｯ邂｡逅・・ｨｩ髯舌′蠢・ｦ√〒縺吶・,
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
        
        // 繧ｰ繝ｭ繝ｼ繝舌Ν萓句､悶ワ繝ｳ繝峨Λ繝ｼ縺ｮ險ｭ螳・
        SetupGlobalExceptionHandlers();
        
        // 繧ｰ繝ｬ繝ｼ繧ｹ繝輔Ν繧ｷ繝｣繝・ヨ繝繧ｦ繝ｳ縺ｮ險ｭ螳・
        SetupGracefulShutdown();
        
        try
        {
#if DEBUG
            // 襍ｷ蜍輔Ο繧ｰ
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
            // 繧ｨ繝ｩ繝ｼ繝ｭ繧ｰ繧偵ヵ繧｡繧､繝ｫ縺ｫ蜃ｺ蜉・
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
        // 譛ｪ蜃ｦ逅・・萓句､悶ｒ繧ｭ繝｣繝・メ
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            var exception = e.ExceptionObject as Exception;
            var errorHandler = GetErrorHandler();
            
            if (errorHandler != null)
            {
                errorHandler.HandleError(
                    exception ?? new InvalidOperationException("Unknown error"),
                    "Unhandled Exception",
                    ErrorSeverity.Critical
                );
            }
            else
            {
                // 繝輔か繝ｼ繝ｫ繝舌ャ繧ｯ: 繝輔ぃ繧､繝ｫ縺ｫ繝ｭ繧ｰ繧定ｨ倬鹸
                LogUnhandledException(exception);
            }
            
            // 繧ｯ繝ｪ繝・ぅ繧ｫ繝ｫ繧ｨ繝ｩ繝ｼ縺ｮ蝣ｴ蜷医√Θ繝ｼ繧ｶ繝ｼ縺ｫ騾夂衍縺励※縺九ｉ邨ゆｺ・
            if (e.IsTerminating)
            {
                TryShowCriticalErrorDialog(exception);
            }
        };
        
        // 髱槫酔譛溘ち繧ｹ繧ｯ縺ｮ譛ｪ蜃ｦ逅・ｾ句､悶ｒ繧ｭ繝｣繝・メ
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
                // 繝輔か繝ｼ繝ｫ繝舌ャ繧ｯ: 繝輔ぃ繧､繝ｫ縺ｫ繝ｭ繧ｰ繧定ｨ倬鹸
                LogUnhandledException(e.Exception);
            }
            
            e.SetObserved(); // 萓句､悶ｒ蜃ｦ逅・ｸ医∩縺ｨ縺励※繝槭・繧ｯ
        };
    }
    
    private static IErrorHandler? GetErrorHandler()
    {
        try
        {
            // ViewModelLocator縺九ｉErrorHandler繧貞叙蠕・
            return ViewModelLocator.ErrorHandler;
        }
        catch (Exception ex)
        {
            LogUnhandledException(ex);
            return null;
        }
    }
    
    private static void SetupGracefulShutdown()
    {
        // Ctrl+C繝上Φ繝峨Λ繝ｼ
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true; // 蜊ｳ蠎ｧ縺ｫ邨ゆｺ・＠縺ｪ縺・
            PerformGracefulShutdown();
        };
    }
    
    private static void PerformGracefulShutdown()
    {
        if (Interlocked.Exchange(ref shutdownRequested, 1) != 0)
        {
            return;
        }

        try
        {
            // 迴ｾ蝨ｨ縺ｮ謫堺ｽ懃憾諷九ｒ菫晏ｭ・
            var errorHandler = GetErrorHandler();
            errorHandler?.HandleInfo("Performing graceful shutdown...", "Shutdown");
            
            // Avalonia繧｢繝励Μ繧ｱ繝ｼ繧ｷ繝ｧ繝ｳ縺ｮ繧ｷ繝｣繝・ヨ繝繧ｦ繝ｳ
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                if (Dispatcher.UIThread.CheckAccess())
                {
                    ShutdownDesktop(desktop);
                }
                else
                {
                    Dispatcher.UIThread.Post(() => ShutdownDesktop(desktop));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The Avalonia dispatcher is already shutting down. There is
            // nothing left to request, and this is a normal exit condition.
        }
        catch (Exception ex)
        {
            LogUnhandledException(ex);
        }
    }

    private static void ShutdownDesktop(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            desktop.Shutdown();
        }
        catch (OperationCanceledException)
        {
            // A concurrent window close may already have stopped the
            // dispatcher. Treat that as a completed shutdown.
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
            
            var threadInfo = $"Thread: {Environment.CurrentManagedThreadId} " +
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
            // 繝ｭ繧ｰ譖ｸ縺崎ｾｼ縺ｿ繧ｨ繝ｩ繝ｼ縺ｯ辟｡隕・
        }
    }

    private static void TryShowCriticalErrorDialog(Exception? exception)
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                _ = ShowCriticalErrorDialog(exception);
                return;
            }

            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await ShowCriticalErrorDialog(exception);
                }
                catch (Exception ex)
                {
                    LogUnhandledException(ex);
                }
            });
        }
        catch (Exception ex)
        {
            LogUnhandledException(ex);
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
                    Title = L.Get("Error.CriticalTitle"),
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
                                Text = L.Get("Error.CriticalMessage"),
                                TextWrapping = TextWrapping.Wrap,
                                FontWeight = FontWeight.Bold,
                                Margin = new Thickness(0, 0, 0, 10)
                            },
                            new TextBlock
                            {
                                Text = exception?.Message ?? L.Get("Error.Unknown"),
                                TextWrapping = TextWrapping.Wrap,
                                Margin = new Thickness(0, 0, 0, 10)
                            },
                            new TextBlock
                            {
                                Text = L.Get("Error.CriticalDetailsSaved"),
                                TextWrapping = TextWrapping.Wrap,
                                FontSize = 11,
                                Foreground = Brushes.Gray,
                                Margin = new Thickness(0, 0, 0, 20)
                            },
                            new Button 
                            { 
                                Content = L.Get("Dialog.OK"),
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
        catch (Exception ex)
        {
            LogUnhandledException(ex);
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseReactiveUI()
            .LogToTrace();

}
