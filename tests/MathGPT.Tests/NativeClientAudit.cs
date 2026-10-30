using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MathGPT.Core;

namespace MathGPT.Tests;

internal static class NativeClientAudit
{
    public static async Task<int> Run(string root)
    {
        Directory.CreateDirectory(root);
        var store = new StateStore(Path.Combine(root, "state"));
        store.State.WorkDirectory = Path.Combine(root, "中文 空格 工作区");
        Directory.CreateDirectory(store.State.WorkDirectory);
        var packageState = new StateStore(Path.Combine(Path.GetDirectoryName(root)!, "package-audit", "state"));
        var guard = new TcpListener(IPAddress.Loopback, 0); guard.Start();
        var port = ((IPEndPoint)guard.LocalEndpoint).Port; guard.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        using var cancellation = new CancellationTokenSource();
        var requests = new List<object>();
        var serving = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync().WaitAsync(cancellation.Token); }
                catch (OperationCanceledException) { return; }
                _ = Respond(ctx, requests);
            }
        });
        var results = new List<object>();
        var before = ExistingConfigHashes();
        var variants = new[]
        {
            (ClientKind.Codex, "gpt-6.1-sol", WireProtocol.Responses, "high"),
            (ClientKind.Claude, "claude-sonnet-4-5", WireProtocol.Anthropic, ""),
            (ClientKind.Harness, "gpt-6.1-sol", WireProtocol.Responses, "high"),
            (ClientKind.Harness, "gpt-6-sol", WireProtocol.Responses, "none"),
            (ClientKind.Harness, "claude-sonnet-4-5", WireProtocol.Anthropic, ""),
            (ClientKind.Harness, "gemini-3.8-flash-low", WireProtocol.ChatCompletions, "")
        };
        foreach (var (kind, selectedModel, wire, effort) in variants)
        {
            try
            {
                var installation = packageState.State.Clients.First(c => c.Kind == kind);
                var p = new ConnectionProfile { Name = "fixture-" + kind, BaseUrl = $"http://127.0.0.1:{port}/v1",
                    Model = selectedModel, Group = "fixture", Protocol = wire, Reasoning = effort };
                store.SaveProfile(p, "fixture-local-only-key");
                var client = new ClientService(store);
                client.Configure(installation, p, new ModelRoute { Id = p.Model, Group = p.Group, Available = true, Protocols = [p.Protocol] });
                var start = client.LaunchInfo(installation, p);
                start.CreateNoWindow = true; start.WindowStyle = ProcessWindowStyle.Hidden;
                start.RedirectStandardOutput = true; start.RedirectStandardError = true;
                start.ArgumentList.Clear();
                if (kind == ClientKind.Codex)
                {
                    foreach (var arg in new[] { "exec", "--json", "--skip-git-repo-check", "--sandbox", "read-only", "--cd", store.State.WorkDirectory,
                        "--model", p.Model, "Return the fixture greeting only. Do not use tools." }) start.ArgumentList.Add(arg);
                }
                else if (kind == ClientKind.Claude)
                {
                    foreach (var arg in new[] { "-p", "--output-format", "json", "--no-session-persistence", "--max-turns", "1", "--model", p.Model,
                        "Return the fixture greeting only. Do not use tools." }) start.ArgumentList.Add(arg);
                }
                else
                {
                    start.Environment["ELECTRON_RUN_AS_NODE"] = "1";
                    var entry = Path.Combine(installation.InstallationRoot, "resources", "app.asar", "dsh", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
                    foreach (var arg in new[] { entry, "--profile", "headless", "--patch",
                        Path.Combine(installation.ConfigDirectory, "profiles", "desktop", "cordis.patch.yml"),
                        "Return the fixture greeting only. Do not use tools." }) start.ArgumentList.Add(arg);
                }
                using var process = Process.Start(start)!;
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(70));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); }
                var output = await outputTask; var errors = await errorTask;
                // Fixtures only. There are no real API credentials or production requests in these logs.
                var logName = kind + "-" + wire + "-" + (effort.Length > 0 ? effort : "default");
                SafeFiles.AtomicWrite(Path.Combine(root, logName + "-stdout.log"), output.Replace("fixture-local-only-key", "[fixture-key]"));
                SafeFiles.AtomicWrite(Path.Combine(root, logName + "-stderr.log"), errors.Replace("fixture-local-only-key", "[fixture-key]"));
                var passed = process.ExitCode == 0 && output.Contains("MathGPT Fixture OK");
                results.Add(new { kind = kind.ToString(), result = passed ? "passed" : "failed", exitCode = process.ExitCode,
                    model = p.Model, protocol = p.Protocol.ToString(), reasoning = p.Reasoning,
                    actualClient = installation.Executable, isolatedConfig = installation.ConfigDirectory, fixtureOnly = true });
                Console.WriteLine($"{kind} {wire} {effort}: {(passed ? "PASS" : "FAIL")} exit {process.ExitCode}");
            }
            catch (Exception ex)
            {
                results.Add(new { kind = kind.ToString(), result = "failed", message = Privacy.Friendly(ex), type = ex.GetType().Name });
                Console.WriteLine(kind + ": FAIL " + Privacy.Friendly(ex));
            }
        }
        var after = ExistingConfigHashes();
        var unchanged = JsonSerializer.Serialize(before) == JsonSerializer.Serialize(after);
        results.Add(new { name = "original-config-and-auth-files", result = unchanged ? "passed" : "failed", before, after });
        SafeFiles.AtomicWrite(Path.Combine(root, "native-results.json"), JsonSerializer.Serialize(new { checks = results, requests }, JsonData.Options));
        cancellation.Cancel(); listener.Stop(); await serving;
        return results.Any(r => JsonSerializer.Serialize(r).Contains("\"failed\"")) ? 1 : 0;
    }
    private static Dictionary<string, string> ExistingConfigHashes()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] relative = [".codex/config.toml", ".codex/auth.json", ".claude/settings.json", ".claude.json", ".dsh/cordis.patch.yml", ".dsh/.credentials.yaml"];
        return relative.ToDictionary(p => p, p =>
        {
            var file = Path.Combine(home, p);
            return File.Exists(file) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))) : "absent";
        });
    }
    private static async Task Respond(HttpListenerContext ctx, List<object> requests)
    {
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            using var document = JsonDocument.Parse(body.Length > 0 ? body : "{}");
            var b = document.RootElement;
            var path = ctx.Request.Url!.AbsolutePath;
            lock (requests) requests.Add(new { path, model = b.Str("model"), stream = b.Flag("stream"),
                reasoning = b.Obj("reasoning").Str("effort", b.Str("reasoning_effort")),
                selectedCredential = ctx.Request.Headers["Authorization"] == "Bearer fixture-local-only-key" ||
                    ctx.Request.Headers["x-api-key"] == "fixture-local-only-key" });
            var model = b.Str("model");
            if (path.EndsWith("/count_tokens")) await Write(ctx, JsonSerializer.Serialize(new { input_tokens = 20 }));
            else if (path.EndsWith("/responses"))
            {
                var item = new { type = "message", id = "msg_fixture", role = "assistant", status = "completed",
                    content = new[] { new { type = "output_text", text = "MathGPT Fixture OK", annotations = Array.Empty<object>() } } };
                var response = new { id = "resp_fixture", model, status = "completed", output = new[] { item }, usage = new { input_tokens = 20, output_tokens = 10, total_tokens = 30 } };
                if (b.Flag("stream"))
                {
                    ctx.Response.ContentType = "text/event-stream";
                    var events = new List<object> {
                        new { type = "response.created", response = new { id = "resp_fixture", model, status = "in_progress", output = Array.Empty<object>() } },
                        new { type = "response.output_item.added", output_index = 0, item = new { type = "message", id = "msg_fixture", role = "assistant", status = "in_progress", content = Array.Empty<object>() } },
                        new { type = "response.output_text.delta", item_id = "msg_fixture", output_index = 0, content_index = 0, delta = "MathGPT Fixture OK" },
                        new { type = "response.output_item.done", output_index = 0, item },
                        new { type = "response.completed", response }
                    };
                    foreach (var e in events) await WriteChunk(ctx, "data: " + JsonSerializer.Serialize(e) + "\n\n");
                    ctx.Response.Close();
                }
                else await Write(ctx, JsonSerializer.Serialize(response));
            }
            else if (path.EndsWith("/messages"))
            {
                var message = new { id = "msg_fixture", type = "message", role = "assistant", model,
                    content = new[] { new { type = "text", text = "MathGPT Fixture OK" } }, stop_reason = "end_turn",
                    stop_sequence = (string?)null, usage = new { input_tokens = 20, output_tokens = 10 } };
                if (b.Flag("stream"))
                {
                    ctx.Response.ContentType = "text/event-stream";
                    var events = new List<object> {
                        new { type = "message_start", message = new { id = "msg_fixture", type = "message", role = "assistant", model, content = Array.Empty<object>(), usage = new { input_tokens = 20, output_tokens = 0 } } },
                        new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } },
                        new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = "MathGPT Fixture OK" } },
                        new { type = "content_block_stop", index = 0 },
                        new { type = "message_delta", delta = new { stop_reason = "end_turn", stop_sequence = (string?)null }, usage = new { output_tokens = 10 } },
                        new { type = "message_stop" }
                    };
                    foreach (var e in events)
                    {
                        using var type = JsonDocument.Parse(JsonSerializer.Serialize(e));
                        await WriteChunk(ctx, "event: " + type.RootElement.Str("type") + "\ndata: " + JsonSerializer.Serialize(e) + "\n\n");
                    }
                    ctx.Response.Close();
                }
                else await Write(ctx, JsonSerializer.Serialize(message));
            }
            else if (path.EndsWith("/chat/completions"))
            {
                if (b.Flag("stream"))
                {
                    ctx.Response.ContentType = "text/event-stream";
                    await WriteChunk(ctx, "data: " + JsonSerializer.Serialize(new { id = "chat_fixture", @object = "chat.completion.chunk",
                        created = 1, model, choices = new[] { new { index = 0, delta = new { role = "assistant", content = "MathGPT Fixture OK" }, finish_reason = (string?)null } } }) + "\n\n");
                    await WriteChunk(ctx, "data: " + JsonSerializer.Serialize(new { id = "chat_fixture", @object = "chat.completion.chunk",
                        created = 1, model, choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } },
                        usage = new { prompt_tokens = 20, completion_tokens = 10, total_tokens = 30 } }) + "\n\ndata: [DONE]\n\n");
                    ctx.Response.Close();
                }
                else await Write(ctx, JsonSerializer.Serialize(new { id = "chat_fixture", model,
                    choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = "MathGPT Fixture OK" } } },
                    usage = new { prompt_tokens = 20, completion_tokens = 10, total_tokens = 30 } }));
            }
            else if (path.EndsWith("/models")) await Write(ctx, JsonSerializer.Serialize(new { data = new[] { new { id = "gpt-6.1-sol" }, new { id = "claude-sonnet-4-5" } } }));
            else await Write(ctx, "{}");
        }
        catch (Exception) { try { ctx.Response.Close(); } catch (ObjectDisposedException) { } }
    }
    private static async Task Write(HttpListenerContext ctx, string text)
    {
        ctx.Response.ContentType = "application/json";
        await WriteChunk(ctx, text); ctx.Response.Close();
    }
    private static async Task WriteChunk(HttpListenerContext ctx, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await ctx.Response.OutputStream.WriteAsync(bytes); await ctx.Response.OutputStream.FlushAsync();
    }
}
