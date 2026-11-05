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

    public static Task<LabConsoleClient> ConnectAsync(string hub, int port, byte[] key, CancellationToken cancel) =>
        ConnectAsync(hub, port, key, LabKeys.HelloName(null), cancel);

    /// <summary>Connects with a key; <paramref name="name"/> is <see cref="LabKeys.HelloName"/> of the key file.</summary>
    public static async Task<LabConsoleClient> ConnectAsync(string hub, int port, byte[] key, string name, CancellationToken cancel)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(hub, port, cancel);
            var channel = await LabChannel.ConnectAsync(client.GetStream(), LabRole.Console, name, key, cancel);
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

    /// <summary>What the hub finds in the network (it takes some seconds).</summary>
    public async Task<JsonObject> ScanAsync(CancellationToken cancel) => await Ask(new LabMessage(MessageKind.Scan, []), cancel);

    /// <summary>Wakes a computer of the classroom ("*" = all that are off) or any card ("mac:AA-BB-…"); returns how many.</summary>
    public Task<int> WakeAsync(string target, CancellationToken cancel) => CommandAsync(target, "wake", cancel);

    /// <summary>Sends one of <see cref="LabCommands"/> to a computer ("*" = all); returns to how many it went.</summary>
    public async Task<int> CommandAsync(string target, string command, CancellationToken cancel)
    {
        var answer = await Ask(new LabMessage(MessageKind.Command, new JsonObject { ["target"] = target, ["command"] = command }), cancel);
        return answer["sent"]?.GetValue<int>() ?? 0;
    }

    /// <summary>
    /// Asks the hub to give a newer program to the computers ("*" = all, or one by name) between rounds; returns how
    /// many it marked.
    /// </summary>
    public async Task<int> UpdateComputersAsync(string target, CancellationToken cancel) =>
        (await Ask(new LabMessage(MessageKind.Update, new JsonObject { ["what"] = "agents", ["target"] = target }), cancel))["marked"]?.GetValue<int>() ?? 0;

    /// <summary>Asks the server to update itself now (its updater looks at GitHub); returns whether the request was left.</summary>
    public async Task<bool> UpdateServerAsync(CancellationToken cancel) =>
        (await Ask(new LabMessage(MessageKind.Update, new JsonObject { ["what"] = "hub" }), cancel))["requested"]?.GetValue<bool>() ?? false;

    /// <summary>The newer program of the console from the hub, with its signed manifest – checked here; null when there is none.</summary>
    public async Task<(UpdateManifest Manifest, byte[] Data)?> ConsoleProgramAsync(CancellationToken cancel)
    {
        var answer = await AskMessage(new LabMessage(MessageKind.Update, new JsonObject { ["what"] = "console" }), cancel);
        if (answer.Kind != MessageKind.Update)
        {
            return null;
        }

        var manifest = LabUpdates.Open(Convert.FromBase64String(answer.Header["manifest"]?.GetValue<string>() ?? ""),
            Convert.FromBase64String(answer.Header["signature"]?.GetValue<string>() ?? ""));
        return manifest?.File(UpdateManifest.Console) is { } file && LabUpdates.Matches(file, answer.Data) ? (manifest, answer.Data) : null;
    }

    /// <summary>
    /// Changes the configuration (a name, a group or settings – see <see cref="LabConfig.Apply"/>), or only reads it
    /// with an empty change; returns the configuration as it is now, with the hub's defaults.
    /// </summary>
    public Task<JsonObject> ConfigureAsync(JsonObject change, CancellationToken cancel) => Ask(new LabMessage(MessageKind.Configure, change), cancel);

    /// <summary>What the server is like now (program, time, model, disk, memory, fonts, the updater's last result) – hubs from 0.4.3.</summary>
    public Task<JsonObject> ServerStatusAsync(CancellationToken cancel) => Server("status", [], cancel);

    /// <summary>The last lines of the log of the hub ("hub") or of its updater ("update") – hubs from 0.4.3.</summary>
    public async Task<IReadOnlyList<string>> ServerLogAsync(string name, int lines, CancellationToken cancel) =>
        [.. (await Server("log", new JsonObject { ["name"] = name, ["lines"] = lines }, cancel))["lines"]?.AsArray().Select(l => l?.GetValue<string>() ?? "") ?? []];

    /// <summary>Starts the hub again (a round that runs is lost); its service brings it back in about half a minute.</summary>
    public async Task<bool> RestartServerAsync(CancellationToken cancel) =>
        (await Server("restart", [], cancel))["ok"]?.GetValue<bool>() ?? false;

    /// <summary>
    /// Asks the server to start the classroom from the learning file of the newest signed release (its updater takes
    /// it, checks it and restarts the hub; the model before is kept beside it); returns whether the request was left.
    /// </summary>
    public async Task<bool> ServerModelFromReleaseAsync(CancellationToken cancel) =>
        (await Server("model", [], cancel))["requested"]?.GetValue<bool>() ?? false;

    private Task<JsonObject> Server(string what, JsonObject header, CancellationToken cancel)
    {
        header["what"] = what;
        return Ask(new LabMessage(MessageKind.Server, header), cancel);
    }

    public void Dispose()
    {
        _channel.Dispose();
        _client.Dispose();
        _gate.Dispose();
    }

    private async Task<JsonObject> Ask(LabMessage message, CancellationToken cancel) => (await AskMessage(message, cancel)).Header;

    private async Task<LabMessage> AskMessage(LabMessage message, CancellationToken cancel)
    {
        await _gate.WaitAsync(cancel);
        try
        {
            await _channel.SendAsync(message, cancel);
            return await _channel.ReceiveAsync(cancel);
        }
        finally
        {
            _gate.Release();
        }
    }
}
