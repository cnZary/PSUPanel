using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace PSUPanel.Shared;

public static class SnapshotClient
{
    public static Task<MonitorSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync(SnapshotProtocol.GetSnapshot, cancellationToken);
    }

    public static async Task<MonitorSnapshot> SendAsync(string command, CancellationToken cancellationToken = default)
    {
        if (command.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new ArgumentException("The command must be a single line.", nameof(command));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        using var client = new NamedPipeClientStream(
            ".",
            SnapshotProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(1500, timeout.Token);
        using var reader = new StreamReader(client, new UTF8Encoding(false), false, 4096, true);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, true);
        await writer.WriteLineAsync(command.AsMemory(), timeout.Token);
        await writer.FlushAsync(timeout.Token);
        var response = await reader.ReadLineAsync(timeout.Token)
            ?? throw new IOException("The monitor closed the snapshot connection.");
        return JsonSerializer.Deserialize(response, MonitorJsonContext.Default.MonitorSnapshot)
            ?? throw new JsonException("The monitor returned an empty snapshot.");
    }
}
