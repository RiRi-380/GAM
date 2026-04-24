using System;
using System.Collections.Generic;
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
        private static readonly string[] GmodProcessNames = new[]
        {
            "hl2",
            "gmod",
            "garrysmod"
        };
        private readonly List<ManagementEventWatcher> startWatchers = new List<ManagementEventWatcher>();
        private readonly List<ManagementEventWatcher> stopWatchers = new List<ManagementEventWatcher>();
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

                // WMI process watchers
                foreach (var processName in GmodProcessNames)
                {
                    var exeName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? processName
                        : processName + ".exe";

                    var startQuery = new WqlEventQuery(
                        $"SELECT * FROM Win32_ProcessStartTrace WHERE ProcessName = '{exeName}'");
                    var startWatcher = new ManagementEventWatcher(startQuery);
                    startWatcher.EventArrived += OnProcessStarted;
                    startWatcher.Start();
                    startWatchers.Add(startWatcher);

                    var stopQuery = new WqlEventQuery(
                        $"SELECT * FROM Win32_ProcessStopTrace WHERE ProcessName = '{exeName}'");
                    var stopWatcher = new ManagementEventWatcher(stopQuery);
                    stopWatcher.EventArrived += OnProcessStopped;
                    stopWatcher.Start();
                    stopWatchers.Add(stopWatcher);
                }

                // Started WMI-based process monitoring for Gmod
            }
            catch (Exception ex)
            {
                // WMIが使えない場合はポーリングにフォールバック
                StopWatching();
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
            CheckGmodProcess();
        }

        private void OnProcessStopped(object sender, EventArrivedEventArgs e)
        {
            CheckGmodProcess();
        }

        private void CheckGmodProcess()
        {
            try
            {
                var processes = new List<Process>();
                foreach (var name in GmodProcessNames)
                {
                    processes.AddRange(Process.GetProcessesByName(name));
                }

                var wasRunning = isGmodRunning;

                lock (lockObject)
                {
                    isGmodRunning = processes.Any();

                    if (!wasRunning && isGmodRunning)
                    {
                        GmodStarted?.Invoke(this, new ProcessEventArgs
                        {
                            ProcessId = processes[0].Id,
                            Timestamp = DateTime.Now
                        });
                    }
                    else if (wasRunning && !isGmodRunning)
                    {
                        GmodStopped?.Invoke(this, new ProcessEventArgs
                        {
                            ProcessId = -1,
                            Timestamp = DateTime.Now
                        });
                    }
                }

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
                foreach (var watcher in startWatchers)
                {
                    watcher?.Stop();
                    watcher?.Dispose();
                }
                startWatchers.Clear();

                foreach (var watcher in stopWatchers)
                {
                    watcher?.Stop();
                    watcher?.Dispose();
                }
                stopWatchers.Clear();

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
