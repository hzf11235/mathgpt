using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MathGPT.Core;

namespace MathGPT.App;

internal sealed record AppCommand(string Action, ClientKind Kind = ClientKind.Codex, string Profile = "");
internal sealed class AppInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly string name;
    private readonly CancellationTokenSource cancellation = new();
    public bool Primary { get; }
    public AppInstance(string dataRoot)
    {
        name = "MathGPT-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataRoot).ToUpperInvariant())))[..24];
        mutex = new Mutex(true, @"Local\" + name, out var created);
        Primary = created;
    }
    public async Task Send(AppCommand command)
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await pipe.ConnectAsync(timeout.Token);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command) + "\n");
            await pipe.WriteAsync(bytes, timeout.Token);
        }
        catch (OperationCanceledException) { throw new UserException("MathGPT 正在启动或忙碌。请稍后使用已有窗口。"); }
    }
    public void Listen(MainWindow window) => _ = Task.Run(async () =>
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation.Token);
                using var buffer = new MemoryStream();
                var one = new byte[1];
                while (buffer.Length < 2048 && await pipe.ReadAsync(one, cancellation.Token) > 0)
                {
                    if (one[0] == '\n') break;
                    buffer.WriteByte(one[0]);
                }
                if (buffer.Length >= 2048) continue;
                var command = JsonSerializer.Deserialize<AppCommand>(buffer.ToArray());
                if (command is null) continue;
                await window.Dispatcher.InvokeAsync(async () =>
                {
                    if (window.WindowState == System.Windows.WindowState.Minimized) window.WindowState = System.Windows.WindowState.Normal;
                    window.Show(); window.Activate();
                    if (command.Action == "launch" && Enum.IsDefined(command.Kind))
                    {
                        try { await window.LaunchClient(command.Kind, UrlPolicy.SafeId(command.Profile)); }
                        catch (UserException ex) { window.Status(ex.Message, true); }
                    }
                });
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { /* A disconnected sender cannot mutate configuration. */ }
            catch (JsonException) { /* Only the tiny structured command contract is accepted. */ }
        }
    });
    public void Dispose()
    {
        cancellation.Cancel(); cancellation.Dispose();
        if (Primary) mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
