using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.UI.Services
{
    public sealed class ExperimentIpcServer : IDisposable
    {
        private readonly AddonManager addonManager;
        private readonly string pipeName;
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private Task? serverTask;

        public ExperimentIpcServer(AddonManager addonManager, string pipeName)
        {
            this.addonManager = addonManager ?? throw new ArgumentNullException(nameof(addonManager));
            this.pipeName = string.IsNullOrWhiteSpace(pipeName) ? "GAMExperiment" : pipeName.Trim();
        }

        public void Start()
        {
            if (serverTask != null)
            {
                return;
            }

            serverTask = Task.Run(() => RunAsync(cts.Token));
        }

        public void Dispose()
        {
            cts.Cancel();
            try
            {
                serverTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Best-effort shutdown.
            }
            finally
            {
                cts.Dispose();
            }
        }

        private async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                using var server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                try
                {
                    await server.WaitForConnectionAsync(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                try
                {
                    await HandleConnectionAsync(server, token);
                }
                catch
                {
                    // Ignore per-connection errors to keep server alive.
                }
            }
        }

        private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken token)
        {
            using var reader = new StreamReader(server);
            using var writer = new StreamWriter(server) { AutoFlush = true };

            var payload = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(payload))
            {
                await WriteResponseAsync(writer, false, "empty_request");
                return;
            }

            IpcCommand? command;
            try
            {
                command = JsonSerializer.Deserialize<IpcCommand>(payload);
            }
            catch (JsonException)
            {
                await WriteResponseAsync(writer, false, "invalid_json");
                return;
            }

            if (command == null || string.IsNullOrWhiteSpace(command.Command))
            {
                await WriteResponseAsync(writer, false, "missing_command");
                return;
            }

            var normalized = NormalizeCommand(command.Command);
            var ok = ExecuteCommand(normalized, command, out var error);
            await WriteResponseAsync(writer, ok, error);
        }

        private bool ExecuteCommand(string command, IpcCommand payload, out string? error)
        {
            error = null;

            switch (command)
            {
                case "task_start":
                    if (string.IsNullOrWhiteSpace(payload.TaskId))
                    {
                        error = "missing_task_id";
                        return false;
                    }
                    if (!addonManager.LogTaskStart(payload.TaskId!, out var taskStartError, payload.Note))
                    {
                        error = taskStartError ?? "task_start_failed";
                        return false;
                    }
                    return true;
                case "task_end":
                    if (string.IsNullOrWhiteSpace(payload.TaskId))
                    {
                        error = "missing_task_id";
                        return false;
                    }
                    if (!addonManager.LogTaskEnd(payload.TaskId!, out var taskEndError, payload.ExpectedHash, payload.Success, payload.Note))
                    {
                        error = taskEndError ?? "task_end_failed";
                        return false;
                    }
                    return true;
                case "bl_start":
                    if (!addonManager.LogBlSwitchStart(out var blStartError, payload.Method, payload.Note))
                    {
                        error = blStartError ?? "bl_start_failed";
                        return false;
                    }
                    return true;
                case "bl_end":
                    if (!addonManager.LogBlSwitchEnd(out var blEndError, payload.Method, payload.ExpectedHash, payload.Success, payload.Note))
                    {
                        error = blEndError ?? "bl_end_failed";
                        return false;
                    }
                    return true;
                default:
                    error = "unknown_command";
                    return false;
            }
        }

        private static async Task WriteResponseAsync(StreamWriter writer, bool ok, string? error)
        {
            var response = new IpcResponse { Ok = ok, Error = error };
            var json = JsonSerializer.Serialize(response);
            await writer.WriteLineAsync(json);
        }

        private static string NormalizeCommand(string command)
        {
            return command.Trim().ToLowerInvariant().Replace("-", "_");
        }

        private sealed class IpcCommand
        {
            [JsonPropertyName("command")]
            public string? Command { get; set; }

            [JsonPropertyName("task_id")]
            public string? TaskId { get; set; }

            [JsonPropertyName("note")]
            public string? Note { get; set; }

            [JsonPropertyName("expected_hash")]
            public string? ExpectedHash { get; set; }

            [JsonPropertyName("success")]
            public bool? Success { get; set; }

            [JsonPropertyName("method")]
            public string? Method { get; set; }
        }

        private sealed class IpcResponse
        {
            [JsonPropertyName("ok")]
            public bool Ok { get; set; }

            [JsonPropertyName("error")]
            public string? Error { get; set; }
        }
    }
}
