using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PSUPanel.Shared;

namespace PSUPanel.Tray;

internal sealed class SnapshotServer : IDisposable
{
    private readonly MonitorService _monitor;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _serverTask;

    public SnapshotServer(MonitorService monitor)
    {
        _monitor = monitor;
        _serverTask = Task.Run(Run);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        try
        {
            _serverTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException exception) when (exception.InnerException is OperationCanceledException)
        {
        }

        _stopping.Dispose();
    }

    private async Task Run()
    {
        var cancellationToken = _stopping.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    SnapshotProtocol.PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                using var reader = new StreamReader(server, new UTF8Encoding(false), false, 1024, true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, true);
                var request = await reader.ReadLineAsync(timeout.Token);
                if (request is null)
                {
                    continue;
                }

                HandleRequest(request);
                var json = JsonSerializer.Serialize(_monitor.GetSnapshot(), MonitorJsonContext.Default.MonitorSnapshot);
                await writer.WriteLineAsync(json.AsMemory(), timeout.Token);
                await writer.FlushAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or UnauthorizedAccessException or ArgumentException or JsonException)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(250, cancellationToken);
                }
            }
        }
    }

    private void HandleRequest(string request)
    {
        if (request == SnapshotProtocol.GetSnapshot)
        {
            return;
        }

        if (request == SnapshotProtocol.Rescan || request == SnapshotProtocol.AutomaticPort)
        {
            _monitor.SetPreferredPort(null);
            return;
        }

        if (request == SnapshotProtocol.RequestIdentity)
        {
            _monitor.RequestIdentity();
            return;
        }

        if (request.StartsWith(SnapshotProtocol.PortPrefix, StringComparison.Ordinal))
        {
            _monitor.SetPreferredPort(request[SnapshotProtocol.PortPrefix.Length..]);
        }
    }
}
