using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GmodAddonManager.ExperimentCli
{
    internal static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length < 2 || args[0] is "-h" or "--help")
            {
                PrintUsage();
                return 1;
            }

            var category = args[0].Trim().ToLowerInvariant();
            var action = args[1].Trim().ToLowerInvariant();
            var options = ParseOptions(args.Skip(2).ToArray());

            var pipeName = options.GetValueOrDefault("--pipe")
                           ?? Environment.GetEnvironmentVariable("GAM_EXPERIMENT_PIPE_NAME")
                           ?? "GAMExperiment";
            pipeName = NormalizePipeName(pipeName);

            IpcCommand? command = BuildCommand(category, action, options);
            if (command == null)
            {
                PrintUsage();
                return 1;
            }

            try
            {
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
                client.Connect(2000);
                using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);

                var payload = JsonSerializer.Serialize(command);
                var payloadBytes = Encoding.UTF8.GetBytes(payload + "\n");
                client.Write(payloadBytes, 0, payloadBytes.Length);
                client.Flush();

                var responseRaw = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(responseRaw))
                {
                    Console.Error.WriteLine("IPC error: no response from server.");
                    return 1;
                }

                var response = JsonSerializer.Deserialize<IpcResponse>(responseRaw);
                if (response != null && !response.Ok)
                {
                    Console.Error.WriteLine($"IPC error: {response.Error ?? "unknown"}");
                    return 1;
                }

                return 0;
            }
            catch (TimeoutException)
            {
                Console.Error.WriteLine("IPC server not available (timeout). Is the UI app running?");
                return 1;
            }
            catch (IOException)
            {
                Console.Error.WriteLine("IPC server not available. Is the UI app running?");
                return 1;
            }
        }

        private static IpcCommand? BuildCommand(string category, string action, Dictionary<string, string?> options)
        {
            if (category == "task" && action is "start" or "end")
            {
                if (!options.TryGetValue("--task", out var taskId) || string.IsNullOrWhiteSpace(taskId))
                {
                    Console.Error.WriteLine("Missing required option: --task");
                    return null;
                }

                return new IpcCommand
                {
                    Command = $"task_{action}",
                    TaskId = taskId,
                    Note = options.GetValueOrDefault("--note"),
                    ExpectedHash = options.GetValueOrDefault("--expected-hash"),
                    Success = ParseOptionalBool(options.GetValueOrDefault("--success"))
                };
            }

            if ((category == "bl" || category == "blswitch") && action is "start" or "end")
            {
                return new IpcCommand
                {
                    Command = $"bl_{action}",
                    Method = options.GetValueOrDefault("--method"),
                    Note = options.GetValueOrDefault("--note"),
                    ExpectedHash = options.GetValueOrDefault("--expected-hash"),
                    Success = ParseOptionalBool(options.GetValueOrDefault("--success"))
                };
            }

            Console.Error.WriteLine("Unknown command.");
            return null;
        }

        private static Dictionary<string, string?> ParseOptions(string[] args)
        {
            var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (!arg.StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                string? value = null;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    value = args[i + 1];
                    i++;
                }

                options[arg] = value ?? "true";
            }

            return options;
        }

        private static bool? ParseOptionalBool(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "no", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return null;
        }

        private static string NormalizePipeName(string name)
        {
            const string prefix = @"\\.\pipe\";
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return name.Substring(prefix.Length);
            }

            return name;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  gam-exp task start --task <T1> [--note \"...\"] [--pipe <name>]");
            Console.WriteLine("  gam-exp task end --task <T1> [--success 1|0] [--expected-hash <hash>] [--note \"...\"] [--pipe <name>]");
            Console.WriteLine("  gam-exp bl start --method <SteamUI> [--note \"...\"] [--pipe <name>]");
            Console.WriteLine("  gam-exp bl end --method <SteamUI> [--success 1|0] [--expected-hash <hash>] [--note \"...\"] [--pipe <name>]");
        }
    }

    internal sealed class IpcCommand
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

    internal sealed class IpcResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }
}
