using System.Globalization;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Iris.Lab;

/// <summary>
/// The console's connection to the hub (with the console key): what the hub knows now, the history of a span, and
/// the fixed commands for one computer or all of them. One request at a time.
/// </summary>
public sealed class LabConsoleClient : IDisposable
{
    private readonly TcpClient _client;
    private readonly LabChannel _channel;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private LabConsoleClient(TcpClient client, LabChannel channel) => (_client, _channel) = (client, channel);

    public static async Task<LabConsoleClient> ConnectAsync(string hub, int port, byte[] key, CancellationToken cancel)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(hub, port, cancel);
            var channel = await LabChannel.ConnectAsync(client.GetStream(), LabRole.Console, "console", key, cancel);
            return new LabConsoleClient(client, channel);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public Task<JsonObject> SnapshotAsync(CancellationToken cancel) => Ask(new LabMessage(MessageKind.Snapshot, []), cancel);

    public Task<JsonObject> HistoryAsync(DateTime from, DateTime to, string? agent, int bucketMinutes, CancellationToken cancel) =>
        Ask(new LabMessage(MessageKind.History, new JsonObject
        {
            ["from"] = from.ToString("s", CultureInfo.InvariantCulture),
            ["to"] = to.ToString("s", CultureInfo.InvariantCulture),
            ["agent"] = agent,
            ["bucket"] = bucketMinutes,
        }), cancel);

    /// <summary>Sends one of <see cref="LabCommands"/> to a computer ("*" = all); returns to how many it went.</summary>
    public async Task<int> CommandAsync(string target, string command, CancellationToken cancel)
    {
        var answer = await Ask(new LabMessage(MessageKind.Command, new JsonObject { ["target"] = target, ["command"] = command }), cancel);
        return answer["sent"]?.GetValue<int>() ?? 0;
    }

    public void Dispose()
    {
        _channel.Dispose();
        _client.Dispose();
        _gate.Dispose();
    }

    private async Task<JsonObject> Ask(LabMessage message, CancellationToken cancel)
    {
        await _gate.WaitAsync(cancel);
        try
        {
            await _channel.SendAsync(message, cancel);
            return (await _channel.ReceiveAsync(cancel)).Header;
        }
        finally
        {
            _gate.Release();
        }
    }
}
