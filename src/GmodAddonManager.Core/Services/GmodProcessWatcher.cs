using System;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

namespace GmodAddonManager.Core.Services
{
    public class GmodProcessWatcher : IDisposable
    {
        private const string GMOD_PROCESS_NAME = "hl2";
        private ManagementEventWatcher? startWatcher;
        private ManagementEventWatcher? stopWatcher;
        private Timer? pollingTimer;
        private bool isGmodRunning;
        private readonly object lockObject = new object();

        public event EventHandler<ProcessEventArgs>? GmodStarted;
        public event EventHandler<ProcessEventArgs>? GmodStopped;

        public bool IsGmodRunning 
        { 
            get 
            { 
                lock (lockObject) 
                { 
                    return isGmodRunning; 
                } 
            } 
        }

        public bool IsNoAddonsActive()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    return false;
                }

                var processes = Process.GetProcessesByName(GMOD_PROCESS_NAME);
                foreach (var process in processes)
                {
                    try
                    {
                        var commandLine = GetProcessCommandLine(process.Id);
                        if (!string.IsNullOrWhiteSpace(commandLine) &&
                            commandLine.IndexOf("-noaddons", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return true;
                        }
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch
            {
                // Best-effort; ignore failures
            }

            return false;
        }

        public GmodProcessWatcher()
        {
        }


        public void StartWatching()
        {
            try
            {
                // GmodProcessWatcher.StartWatching called
                
                // Check if running in WSL
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
                {
                    // Running on Linux/WSL, skipping WMI
                    StartPolling();
                    return;
                }
                
                // WMI繧､繝吶Φ繝医え繧ｩ繝・メ繝｣繝ｼ縺ｮ險ｭ螳・
                var startQuery = new WqlEventQuery(
                    "SELECT * FROM Win32_ProcessStartTrace WHERE ProcessName = 'hl2.exe'");
                startWatcher = new ManagementEventWatcher(startQuery);
                startWatcher.EventArrived += OnProcessStarted;
                startWatcher.Start();

                var stopQuery = new WqlEventQuery(
                    "SELECT * FROM Win32_ProcessStopTrace WHERE ProcessName = 'hl2.exe'");
                stopWatcher = new ManagementEventWatcher(stopQuery);
                stopWatcher.EventArrived += OnProcessStopped;
                stopWatcher.Start();

                // Started WMI-based process monitoring for Gmod
            }
            catch (Exception)
            {
                // WMI縺御ｽｿ縺医↑縺・ｴ蜷医・繝昴・繝ｪ繝ｳ繧ｰ縺ｫ繝輔か繝ｼ繝ｫ繝舌ャ繧ｯ
                StartPolling();
            }

            // 蛻晏屓繝√ぉ繝・け
            CheckGmodProcess();
        }

        private void StartPolling()
        {
            // 5遘偵＃縺ｨ縺ｫ繝励Ο繧ｻ繧ｹ繧偵メ繧ｧ繝・け
            pollingTimer = new Timer(
                _ => CheckGmodProcess(), 
                null, 
                TimeSpan.Zero, 
                TimeSpan.FromSeconds(5));
            
        }

        private void OnProcessStarted(object sender, EventArrivedEventArgs e)
        {
            lock (lockObject)
            {
                if (!isGmodRunning)
                {
                    isGmodRunning = true;
                    var processId = Convert.ToInt32(e.NewEvent["ProcessID"]);
                    
                    GmodStarted?.Invoke(this, new ProcessEventArgs 
                    { 
                        ProcessId = processId,
                        Timestamp = DateTime.Now 
                    });
                }
            }
        }

        private void OnProcessStopped(object sender, EventArrivedEventArgs e)
        {
            lock (lockObject)
            {
                if (isGmodRunning)
                {
                    isGmodRunning = false;
                    var processId = Convert.ToInt32(e.NewEvent["ProcessID"]);
                    
                    GmodStopped?.Invoke(this, new ProcessEventArgs 
                    { 
                        ProcessId = processId,
                        Timestamp = DateTime.Now 
                    });
                }
            }
        }

        private void CheckGmodProcess()
        {
            try
            {
                // CheckGmodProcess called
                var processes = Process.GetProcessesByName(GMOD_PROCESS_NAME);
                // Found processes
                var wasRunning = isGmodRunning;

            lock (lockObject)
            {
                isGmodRunning = processes.Any();

                if (!wasRunning && isGmodRunning)
                {
                    // Gmod縺瑚ｵｷ蜍輔＠縺・
                    
                    GmodStarted?.Invoke(this, new ProcessEventArgs 
                    { 
                        ProcessId = processes[0].Id,
                        Timestamp = DateTime.Now 
                    });
                }
                else if (wasRunning && !isGmodRunning)
                {
                    // Gmod縺檎ｵゆｺ・＠縺・
                    
                    GmodStopped?.Invoke(this, new ProcessEventArgs 
                    { 
                        ProcessId = -1,
                        Timestamp = DateTime.Now 
                    });
                }
            }

            // 繝励Ο繧ｻ繧ｹ繝上Φ繝峨Ν繧定ｧ｣謾ｾ
            foreach (var process in processes)
            {
                process.Dispose();
            }
            }
            catch (Exception)
            {
                // CheckGmodProcess error
                // In WSL, process monitoring might not work correctly
                // Default to not running
                lock (lockObject)
                {
                    isGmodRunning = false;
                }
            }
        }

        private static string? GetProcessCommandLine(int processId)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {processId}");
                foreach (ManagementObject obj in searcher.Get())
                {
                    return obj["CommandLine"]?.ToString();
                }
            }
            catch
            {
                // Ignore command line lookup errors
            }

            return null;
        }

        public void StopWatching()
        {
            
            try
            {
                startWatcher?.Stop();
                startWatcher?.Dispose();
                stopWatcher?.Stop();
                stopWatcher?.Dispose();
                pollingTimer?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GmodProcessWatcher] StopWatching cleanup failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            StopWatching();
        }
    }

    public class ProcessEventArgs : EventArgs
    {
        public int ProcessId { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
