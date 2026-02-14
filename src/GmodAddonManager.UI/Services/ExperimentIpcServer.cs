using System;
using System.Diagnostics.CodeAnalysis;
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
            catch (Exception ex)
            {
                SafeFileLogger.TryLogException("ExperimentIpcServer.Dispose", ex);
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
                catch (Exception ex)
                {
                    SafeFileLogger.TryLogException("ExperimentIpcServer.RunAsync.HandleConnectionAsync", ex);
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
                case "TASK_START":
                    if (string.IsNullOrWhiteSpace(payload.TaskId))
                    {
                        error = "missing_task_id";
                        return false;
                    }
                    return addonManager.LogTaskStart(
                        payload.TaskId!,
                        out error,
                        payload.Note,
                        payload.FromAssetId,
                        payload.FromAssetLabel,
                        payload.ToAssetId,
                        payload.ToAssetLabel);
                case "TASK_END":
                    if (string.IsNullOrWhiteSpace(payload.TaskId))
                    {
                        error = "missing_task_id";
                        return false;
                    }
                    return addonManager.LogTaskEnd(
                        payload.TaskId!,
                        out error,
                        payload.ExpectedHash,
                        payload.Success,
                        payload.Note,
                        payload.FromAssetId,
                        payload.FromAssetLabel,
                        payload.ToAssetId,
                        payload.ToAssetLabel);
                case "BL_START":
                    return addonManager.LogBlSwitchStart(
                        out error,
                        payload.Method,
                        payload.Note,
                        payload.FromAssetId,
                        payload.FromAssetLabel,
                        payload.ToAssetId,
                        payload.ToAssetLabel);
                case "BL_END":
                    return addonManager.LogBlSwitchEnd(
                        out error,
                        payload.Method,
                        payload.ExpectedHash,
                        payload.Success,
                        payload.Note,
                        payload.FromAssetId,
                        payload.FromAssetLabel,
                        payload.ToAssetId,
                        payload.ToAssetLabel);
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
            return command.Trim().ToUpperInvariant().Replace("-", "_");
        }

        [SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Used for System.Text.Json deserialization.")]
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

            [JsonPropertyName("from_asset_id")]
            public string? FromAssetId { get; set; }

            [JsonPropertyName("from_asset_label")]
            public string? FromAssetLabel { get; set; }

            [JsonPropertyName("to_asset_id")]
            public string? ToAssetId { get; set; }

            [JsonPropertyName("to_asset_label")]
            public string? ToAssetLabel { get; set; }
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
