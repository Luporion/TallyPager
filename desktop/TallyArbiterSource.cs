using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace TallyPager;

internal sealed record ArbiterState(string Status, MixerInput[] Devices, MixerInput[] Atem, MixerInput[] Vmix)
{
    public static ArbiterState Off { get; } = new("Getrennt", [], [], []);

    public MixerInput[] For(MixerKind mixer) => mixer switch
    {
        MixerKind.Arbiter => Devices, MixerKind.ArbiterAtem => Atem, MixerKind.ArbiterVmix => Vmix, _ => []
    };
}

internal sealed class TallyArbiterSource : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private bool disposed;
    private volatile ArbiterState state = ArbiterState.Off with { Status = "Verbinde …" };
    public ArbiterState State => state;

    public TallyArbiterSource(Uri endpoint)
    {
        var token = stop.Token;
        _ = Task.Run(() => Run(endpoint, token));
    }

    public static Uri? Endpoint(string address)
    {
        address = address.Trim();
        bool hasScheme = address.Contains("://", StringComparison.Ordinal);
        if (!Uri.TryCreate(hasScheme ? address : "http://" + address, UriKind.Absolute, out var host) ||
            host.Scheme is not ("http" or "https") || host.HostNameType == UriHostNameType.Unknown ||
            host.UserInfo.Length != 0 || host.Host.Length == 0 || host.AbsolutePath != "/" ||
            host.Query.Length != 0 || host.Fragment.Length != 0)
            return null;
        // A plain hostname/IP uses the normal TA port. A URL can specify reverse-proxy ports.
        string authority = hasScheme ? address[(address.IndexOf("://", StringComparison.Ordinal) + 3)..] : address;
        bool explicitPort = authority.TrimEnd('/').LastIndexOf(':') > authority.LastIndexOf(']');
        return new UriBuilder(host)
        {
            Scheme = host.Scheme == "https" ? "wss" : "ws",
            Port = !hasScheme && !explicitPort ? 4455 : host.Port,
            Path = "/socket.io/",
            Query = "EIO=4&transport=websocket"
        }.Uri;
    }

    private async Task Run(Uri endpoint, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(3000);
                    await socket.ConnectAsync(endpoint, timeout.Token);
                }
                await Receive(socket, token);
            }
            catch (Exception error) when (error is WebSocketException or IOException or OperationCanceledException or
                TimeoutException or JsonException)
            {
                if (token.IsCancellationRequested) return;
                state = ArbiterState.Off with { Status = "Keine aktuellen Daten · neuer Versuch" };
            }
            try { await Task.Delay(1200, token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task Receive(ClientWebSocket socket, CancellationToken token)
    {
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(token);
        var wire = connection.Token;
        var cache = new ArbiterSnapshot();
        var packet = ReadPacket(socket, wire);
        bool opened = false, joined = false;
        long lastSnapshot = Environment.TickCount64;
        long pollDue = 0, metaDue = 0;
        try
        {
            while (!wire.IsCancellationRequested)
            {
                long now = Environment.TickCount64;
                if (now - lastSnapshot > 3500) throw new TimeoutException();
                if (!cache.IsFresh(now) && !ReferenceEquals(state, ArbiterState.Off) && state.Devices.Length > 0)
                    state = ArbiterState.Off with { Status = "Warte auf aktuelle Tally-Daten …" };
                if (joined && now >= pollDue)
                {
                    pollDue = now + 500;
                    if (now >= metaDue)
                    {
                        metaDue = now + 2000;
                        foreach (string request in ArbiterSnapshot.Metadata)
                            await Send(socket, "42[\"" + request + "\"]", wire);
                    }
                    await Send(socket, "42[\"device_states\"]", wire);
                }

                if (await Task.WhenAny(packet, Task.Delay(50, wire)) != packet) continue;
                string message = await packet;
                if (!opened && message.StartsWith("0{", StringComparison.Ordinal))
                {
                    opened = true;
                    await Send(socket, "40", wire);
                }
                else if (opened && message.StartsWith("40", StringComparison.Ordinal))
                    joined = true;
                else if (message.StartsWith('2'))
                    await Send(socket, "3" + message[1..], wire);
                else if (message.StartsWith("41", StringComparison.Ordinal) || message.StartsWith("44", StringComparison.Ordinal))
                    throw new IOException("Socket.IO namespace disconnected.");
                else if (joined && message.StartsWith("42[", StringComparison.Ordinal))
                {
                    cache.Accept(message.AsMemory(2), now);
                    if (cache.Build(now) is { } snapshot)
                    {
                        lastSnapshot = now;
                        state = snapshot;
                    }
                }
                packet = ReadPacket(socket, wire);
            }
        }
        finally
        {
            connection.Cancel();
            try { await packet; }
            catch (Exception error) when (error is OperationCanceledException or WebSocketException or IOException) { }
        }
    }

    private static Task Send(ClientWebSocket socket, string message, CancellationToken token) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(message).AsMemory(), WebSocketMessageType.Text, true, token).AsTask();

    private static async Task<string> ReadPacket(ClientWebSocket socket, CancellationToken token)
    {
        const int maximum = 4_000_000;
        var output = new ArrayBufferWriter<byte>();
        while (output.WrittenCount <= maximum)
        {
            var result = await socket.ReceiveAsync(output.GetMemory(4096), token);
            if (result.MessageType != WebSocketMessageType.Text)
                throw new IOException("Expected Tally Arbiter text packet.");
            output.Advance(result.Count);
            if (output.WrittenCount > maximum) break;
            if (result.EndOfMessage) return Encoding.UTF8.GetString(output.WrittenSpan);
        }
        throw new IOException("Tally Arbiter packet too large.");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stop.Cancel();
        stop.Dispose();
    }
}

// Independent of the socket so out-of-order events and source health can be tested.
internal sealed class ArbiterSnapshot
{
    internal static readonly string[] Metadata = ["devices", "device_sources", "sources", "bus_options"];
    private const string AtemId = "44b8bc4f", VmixId = "58b6af42";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, long> received = [];
    private Device[] devices = [];
    private Mapping[] mappings = [];
    private Source[] sources = [];
    private Bus[] buses = [];
    private Tally[] tallies = [];

    private sealed record Device(string Id, string Name, bool? Enabled);
    private sealed record Mapping(string Id, string DeviceId, string SourceId);
    private sealed record Source(string Id, string SourceTypeId, bool Connected, bool? Enabled);
    private sealed record Bus(string Id, string Type);
    private sealed record Tally(string DeviceId, string BusId, string[]? Sources);

    public void Accept(ReadOnlyMemory<char> payload, long now)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2 ||
            root[0].ValueKind != JsonValueKind.String || root[1].ValueKind != JsonValueKind.Array) return;
        string name = root[0].GetString()!;
        var value = root[1];
        switch (name)
        {
            case "devices": devices = value.Deserialize<Device[]>(Json) ?? []; break;
            case "device_sources": mappings = value.Deserialize<Mapping[]>(Json) ?? []; break;
            case "sources": sources = value.Deserialize<Source[]>(Json) ?? []; break;
            case "bus_options": buses = value.Deserialize<Bus[]>(Json) ?? []; break;
            case "device_states": tallies = value.Deserialize<Tally[]>(Json) ?? []; break;
            default: return;
        }
        received[name] = now;
    }

    public ArbiterState? Build(long now)
    {
        if (!IsFresh(now)) return null;
        var display = devices.Where(device => device is { Enabled: not false } && !string.IsNullOrWhiteSpace(device.Id))
            .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        return new($"Verbunden · {display.Length} Geräte", Inputs(display, null), Inputs(display, AtemId), Inputs(display, VmixId));
    }

    public bool IsFresh(long now) =>
        received.TryGetValue("device_states", out long lastState) && now - lastState <= 1500 &&
        Metadata.All(key => received.TryGetValue(key, out long value) && now - value <= 3200);

    private MixerInput[] Inputs(Device[] display, string? type)
    {
        var sourceIds = sources.Where(source => source is not null && (type is null || source.SourceTypeId == type))
            .Select(source => source.Id).ToHashSet(StringComparer.Ordinal);
        var reachable = sources.Where(source => source is { Connected: true, Enabled: not false })
            .Select(source => source.Id).ToHashSet(StringComparer.Ordinal);
        var program = buses.Where(bus => bus is { Type: "program" }).Select(bus => bus.Id).ToHashSet();
        var preview = buses.Where(bus => bus is { Type: "preview" }).Select(bus => bus.Id).ToHashSet();

        return display.Select((device, index) =>
        {
            var relevant = mappings.Where(map => map is not null && map.DeviceId == device.Id &&
                sourceIds.Contains(map.SourceId)).ToArray();
            if (type is not null && relevant.Length == 0) return null;
            var live = relevant.Where(map => reachable.Contains(map.SourceId)).Select(map => map.Id).ToHashSet();
            var statuses = tallies.Where(tally => tally is not null && tally.DeviceId == device.Id).ToArray();
            int state = 0;
            if (live.Count > 0 && statuses.Length > 0)
            {
                var active = statuses.Where(tally => tally.Sources?.Any(live.Contains) == true).ToArray();
                state = active.Any(tally => program.Contains(tally.BusId)) ? 3 :
                    active.Any(tally => preview.Contains(tally.BusId)) ? 2 : 1;
            }
            return new MixerInput(index + 1, device.Name ?? device.Id, state, device.Id);
        }).OfType<MixerInput>().ToArray();
    }
}
