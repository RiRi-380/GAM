using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;

namespace GmodAddonManager.Core.Services
{
    public class GmodProcessWatcher : IDisposable
    {
        private static readonly string[] GmodProcessNames = { "hl2", "gmod" };
        private const string GmodWmiFilter =
            "ProcessName = 'hl2.exe' OR ProcessName = 'gmod.exe'";

        private readonly object lockObject = new object();
        private readonly Func<IReadOnlyCollection<int>> runningProcessIdProvider;
        private readonly HashSet<int> runningProcessIds = new HashSet<int>();
        private ManagementEventWatcher? startWatcher;
        private ManagementEventWatcher? stopWatcher;
        private Timer? pollingTimer;
        private bool isGmodRunning;

        public event EventHandler<ProcessEventArgs>? GmodStarted;
        public event EventHandler<ProcessEventArgs>? GmodStopped;

        public GmodProcessWatcher()
            : this(GetRunningGmodProcessIds)
        {
        }

        internal GmodProcessWatcher(Func<IReadOnlyCollection<int>> runningProcessIdProvider)
        {
            this.runningProcessIdProvider = runningProcessIdProvider ??
                throw new ArgumentNullException(nameof(runningProcessIdProvider));
        }

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

        internal static bool IsRecognizedProcessName(string? processName)
        {
            if (string.IsNullOrWhiteSpace(processName))
            {
                return false;
            }

            var fileName = Path.GetFileName(processName.Trim());
            var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
            return GmodProcessNames.Any(name =>
                string.Equals(name, nameWithoutExtension, StringComparison.OrdinalIgnoreCase));
        }

        public bool IsNoAddonsActive()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    return false;
                }

                foreach (var processName in GmodProcessNames)
                {
                    var processes = Process.GetProcessesByName(processName);
                    try
                    {
                        foreach (var process in processes)
                        {
                            var commandLine = GetProcessCommandLine(process.Id);
                            if (!string.IsNullOrWhiteSpace(commandLine) &&
                                commandLine.IndexOf("-noaddons", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                return true;
                            }
                        }
                    }
                    finally
                    {
                        foreach (var process in processes)
                        {
                            process.Dispose();
                        }
                    }
                }
            }
            catch
            {
                // This helper is best-effort and does not drive runtime-state writes.
            }

            return false;
        }

        public void StartWatching()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                StartPolling();
                RefreshProcessState();
                return;
            }

            try
            {
                var startQuery = new WqlEventQuery(
                    $"SELECT * FROM Win32_ProcessStartTrace WHERE {GmodWmiFilter}");
                startWatcher = new ManagementEventWatcher(startQuery);
                startWatcher.EventArrived += OnProcessStarted;
                startWatcher.Start();

                var stopQuery = new WqlEventQuery(
                    $"SELECT * FROM Win32_ProcessStopTrace WHERE {GmodWmiFilter}");
                stopWatcher = new ManagementEventWatcher(stopQuery);
                stopWatcher.EventArrived += OnProcessStopped;
                stopWatcher.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GmodProcessWatcher] WMI setup failed; using polling: {ex.Message}");
                DisposeWmiWatchers();
                StartPolling();
            }

            RefreshProcessState();
        }

        private void StartPolling()
        {
            pollingTimer?.Dispose();
            pollingTimer = new Timer(
                _ => RefreshProcessState(),
                null,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5));
        }

        private void OnProcessStarted(object sender, EventArrivedEventArgs e)
        {
            if (TryGetEventProcessId(e, out var processId))
            {
                ObserveProcessStarted(processId);
            }
            else
            {
                RefreshProcessState();
            }
        }

        private void OnProcessStopped(object sender, EventArrivedEventArgs e)
        {
            if (TryGetEventProcessId(e, out var processId))
            {
                ObserveProcessStopped(processId);
            }
            else
            {
                RefreshProcessState();
            }
        }

        internal void ObserveProcessStarted(int processId)
        {
            if (processId <= 0)
            {
                return;
            }

            var raiseStarted = false;
            lock (lockObject)
            {
                var wasRunning = runningProcessIds.Count > 0;
                runningProcessIds.Add(processId);
                isGmodRunning = runningProcessIds.Count > 0;
                raiseStarted = !wasRunning && isGmodRunning;
            }

            if (raiseStarted)
            {
                RaiseGmodStarted(processId);
            }
        }

        internal void ObserveProcessStopped(int processId)
        {
            if (processId <= 0)
            {
                return;
            }

            var raiseStopped = false;
            lock (lockObject)
            {
                var wasRunning = runningProcessIds.Count > 0;
                runningProcessIds.Remove(processId);
                isGmodRunning = runningProcessIds.Count > 0;
                raiseStopped = wasRunning && !isGmodRunning;
            }

            if (raiseStopped)
            {
                RaiseGmodStopped(processId);
            }
        }

        internal void RefreshProcessState()
        {
            var raiseStarted = false;
            var raiseStopped = false;
            var startedProcessId = -1;

            try
            {
                lock (lockObject)
                {
                    // Keep enumeration and state replacement serialized with WMI events.
                    // If enumeration fails, the catch deliberately preserves the last
                    // known state instead of declaring GMod stopped and applying changes.
                    var snapshot = (runningProcessIdProvider() ?? Array.Empty<int>())
                        .Where(processId => processId > 0)
                        .Distinct()
                        .OrderBy(processId => processId)
                        .ToArray();
                    var wasRunning = runningProcessIds.Count > 0;

                    runningProcessIds.Clear();
                    foreach (var processId in snapshot)
                    {
                        runningProcessIds.Add(processId);
                    }

                    isGmodRunning = runningProcessIds.Count > 0;
                    raiseStarted = !wasRunning && isGmodRunning;
                    raiseStopped = wasRunning && !isGmodRunning;
                    if (raiseStarted)
                    {
                        startedProcessId = snapshot[0];
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[GmodProcessWatcher] Process-state refresh failed; preserving previous state: {ex.Message}");
                return;
            }

            if (raiseStarted)
            {
                RaiseGmodStarted(startedProcessId);
            }
            else if (raiseStopped)
            {
                RaiseGmodStopped(-1);
            }
        }

        private void RaiseGmodStarted(int processId)
        {
            GmodStarted?.Invoke(this, new ProcessEventArgs
            {
                ProcessId = processId,
                Timestamp = DateTime.Now
            });
        }

        private void RaiseGmodStopped(int processId)
        {
            GmodStopped?.Invoke(this, new ProcessEventArgs
            {
                ProcessId = processId,
                Timestamp = DateTime.Now
            });
        }

        private static bool TryGetEventProcessId(EventArrivedEventArgs e, out int processId)
        {
            processId = -1;
            try
            {
                processId = Convert.ToInt32(e.NewEvent["ProcessID"]);
                return processId > 0;
            }
            catch
            {
                return false;
            }
        }

        private static IReadOnlyCollection<int> GetRunningGmodProcessIds()
        {
            var processIds = new HashSet<int>();
            foreach (var processName in GmodProcessNames)
            {
                var processes = Process.GetProcessesByName(processName);
                foreach (var process in processes)
                {
                    try
                    {
                        processIds.Add(process.Id);
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }

            return processIds.ToArray();
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
                // Ignore command line lookup errors.
            }

            return null;
        }

        private void DisposeWmiWatchers()
        {
            DisposeWatcher(ref startWatcher, OnProcessStarted);
            DisposeWatcher(ref stopWatcher, OnProcessStopped);
        }

        private static void DisposeWatcher(
            ref ManagementEventWatcher? watcher,
            EventArrivedEventHandler handler)
        {
            var current = watcher;
            watcher = null;
            if (current == null)
            {
                return;
            }

            try
            {
                current.EventArrived -= handler;
                current.Stop();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GmodProcessWatcher] WMI watcher stop failed: {ex.Message}");
            }
            finally
            {
                current.Dispose();
            }
        }

        public void StopWatching()
        {
            DisposeWmiWatchers();
            var timer = Interlocked.Exchange(ref pollingTimer, null);
            timer?.Dispose();
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
