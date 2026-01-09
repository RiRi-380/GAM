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
                    addonManager.LogTaskStart(payload.TaskId!, payload.Note);
                    return true;
                case "task_end":
                    if (string.IsNullOrWhiteSpace(payload.TaskId))
                    {
                        error = "missing_task_id";
                        return false;
                    }
                    addonManager.LogTaskEnd(payload.TaskId!, payload.ExpectedHash, payload.Success, payload.Note);
                    return true;
                case "bl_start":
                    addonManager.LogBlSwitchStart(payload.Method, payload.Note);
                    return true;
                case "bl_end":
                    addonManager.LogBlSwitchEnd(payload.Method, payload.ExpectedHash, payload.Success, payload.Note);
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
