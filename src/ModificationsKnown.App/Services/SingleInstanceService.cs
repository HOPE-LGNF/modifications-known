using System.IO.Pipes;
using System.Text;

namespace ModificationsKnown.App.Services;

/// <summary>
/// One launcher instance per user session. A browser NXM launch starts a second
/// process; that process forwards the URL over a named pipe and exits.
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    private const string PipeName = "ModificationsKnown.Nxm.Pipe";

    private readonly FileStream? _lockStream;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _gate = new();
    private Action<string>? _handler;
    private string? _pending;
    private bool _serverStarted;

    public SingleInstanceService()
    {
        try
        {
            var lockPath = Path.Combine(Path.GetTempPath(), "ModificationsKnown.nxm.lock");
            _lockStream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            IsFirstInstance = true;
        }
        catch (IOException)
        {
            IsFirstInstance = false;
        }
        catch
        {
            // If the OS refuses a lock, never block the launcher.
            IsFirstInstance = true;
        }
    }

    public bool IsFirstInstance { get; }

    public bool IsServerRunning => _serverStarted;

    public void SetHandler(Action<string> handler)
    {
        lock (_gate)
        {
            _handler = handler;
            if (_pending is null) return;
            var pending = _pending;
            _pending = null;
            handler(pending);
        }
    }

    public void StartServer()
    {
        if (!IsFirstInstance || _serverStarted) return;
        _serverStarted = true;
        _ = Task.Run(ServerLoopAsync);
    }

    public bool TryForward(string url, TimeSpan connectTimeout)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        var deadline = DateTime.UtcNow + connectTimeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                client.Connect(250);
                using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
                writer.WriteLine(url);
                return true;
            }
            catch
            {
                Thread.Sleep(150);
            }
        }

        return false;
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _lockStream?.Dispose();
        _cancellation.Dispose();
    }

    private async Task ServerLoopAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(_cancellation.Token);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var line = await reader.ReadLineAsync(_cancellation.Token);
                if (string.IsNullOrWhiteSpace(line) || line.Length > 8192) continue;

                lock (_gate)
                {
                    if (_handler is null) _pending = line;
                    else _handler(line);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // A broken client should not stop the server.
                await Task.Delay(200);
            }
        }
    }
}
