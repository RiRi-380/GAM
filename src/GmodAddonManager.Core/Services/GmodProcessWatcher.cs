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
        private ManagementEventWatcher startWatcher;
        private ManagementEventWatcher stopWatcher;
        private Timer pollingTimer;
        private bool isGmodRunning;
        private readonly object lockObject = new object();

        public event EventHandler<ProcessEventArgs> GmodStarted;
        public event EventHandler<ProcessEventArgs> GmodStopped;

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
                
                // WMIイベントウォッチャーの設定
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
            catch (Exception ex)
            {
                // WMIが使えない場合はポーリングにフォールバック
                StartPolling();
            }

            // 初回チェック
            CheckGmodProcess();
        }

        private void StartPolling()
        {
            // 5秒ごとにプロセスをチェック
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
                    // Gmodが起動した
                    
                    GmodStarted?.Invoke(this, new ProcessEventArgs 
                    { 
                        ProcessId = processes[0].Id,
                        Timestamp = DateTime.Now 
                    });
                }
                else if (wasRunning && !isGmodRunning)
                {
                    // Gmodが終了した
                    
                    GmodStopped?.Invoke(this, new ProcessEventArgs 
                    { 
                        ProcessId = -1,
                        Timestamp = DateTime.Now 
                    });
                }
            }

            // プロセスハンドルを解放
            foreach (var process in processes)
            {
                process.Dispose();
            }
            }
            catch (Exception ex)
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