using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TallyPager;

internal enum MixerKind { Off, Atem, Vmix }
internal sealed record Assignment(MixerKind Mixer, int Input, string? Key = null);
internal sealed record Pager(string Id, string Name, string Key, Assignment? Inside = null, Assignment? Outside = null);
internal sealed record Preview(int Inside, int Outside, string Chat, string Next, bool Test = false)
{
    public static Preview Clear { get; } = new(0, 0, "", "");
}
internal enum Connection { Disconnected, Delayed, Connected }
internal sealed record PagerStatus(string Id, string Name, Connection Connection, int? Battery, bool PreviewReceived,
    int? Rssi, int? Rtt, Preview View)
{
    public bool Online => Connection != Connection.Disconnected;
}

internal sealed class PilotCore : IDisposable
{
    public const int Port = 46224;
    public static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TallyPager", "Pilot");
    private readonly string configPath;
    private readonly Dictionary<string, Link> devices = new(StringComparer.Ordinal);
    private readonly UdpClient udp = new(AddressFamily.InterNetwork);
    private long lastBroadcast;

    private sealed record Offer(IPEndPoint Endpoint, string Nonce, string Session, long Expires);
    private sealed class Link(Pager pager)
    {
        public Pager Pager { get; set; } = pager;
        public Offer? Pending { get; set; }
        public Offer? Active { get; set; }
        public uint Sequence { get; set; }
        public uint SeenSequence { get; set; }
        public uint TelemetrySequence { get; set; }
        public uint PreviewSequence { get; set; }
        public long SeenAt { get; set; } = -6000;
        public long HandshakeAt { get; set; } = -6000;
        public int? Battery { get; set; }
        public int? Rssi { get; set; }
        public int? Rtt { get; set; }
        public int? StateAge { get; set; }
        public bool Paused { get; set; }
        public Preview Preview { get; set; } = Preview.Clear;
    }

    internal IPEndPoint Endpoint => (IPEndPoint)udp.Client.LocalEndPoint!;

    public PilotCore(string? dataDirectory = null, int port = Port)
    {
        configPath = Path.Combine(dataDirectory ?? DataDirectory, "devices.json");
        try
        {
            if (File.Exists(configPath))
            {
                foreach (var pager in JsonSerializer.Deserialize<Pager[]>(File.ReadAllText(configPath)) ?? [])
                {
                    if (IsHex(pager.Id, 12) && IsHex(pager.Key, 64))
                        devices[pager.Id] = new(pager);
                }
            }
            udp.Client.ExclusiveAddressUse = true;
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            // An offline stick can trigger an ICMP Port Unreachable on Windows.
            // Other sticks must continue to receive despite that unrelated reply.
            const int udpConnectionReset = unchecked((int)0x9800000C);
            udp.Client.IOControl(udpConnectionReset, new byte[4], null);
        }
        catch
        {
            udp.Dispose();
            throw;
        }
    }

    public static string RandomHex(int length) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(length));

    public Pager Prepare(string id, string name) => devices.TryGetValue(id, out var link)
        ? link.Pager with { Name = name }
        : new(id, name, RandomHex(32));

    public Pager[] Devices => devices.Values.Select(link => link.Pager).ToArray();

    public void Save(Pager pager)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var entries = devices.Values.Select(link => link.Pager).Where(x => x.Id != pager.Id).Append(pager);
        string temporary = configPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(entries));
        File.Move(temporary, configPath, true);
        if (devices.TryGetValue(pager.Id, out var link)) link.Pager = pager;
        else devices[pager.Id] = new(pager);
    }

    public PagerStatus[] Statuses()
    {
        long now = Environment.TickCount64;
        return devices.Values.Select(link =>
        {
            long age = now - link.SeenAt;
            var state = age >= 3500 ? Connection.Disconnected :
                age >= 1700 || link.SeenSequence == 0 || link.StateAge >= 750 || link.Rtt >= 250 ? Connection.Delayed : Connection.Connected;
            bool online = state != Connection.Disconnected;
            return new PagerStatus(link.Pager.Id, link.Pager.Name, state, link.Battery,
                online && link.PreviewSequence > 0 && link.SeenSequence >= link.PreviewSequence,
                online ? link.Rssi : null, online ? link.Rtt : null, link.Preview);
        }).ToArray();
    }

    public void SetPreview(string id, Preview preview, bool paused = false)
    {
        var link = devices[id];
        if (link.Preview == preview && link.Paused == paused) return;
        link.Preview = preview;
        link.Paused = paused;
        link.PreviewSequence = SendState(link);
    }

    public void Tick()
    {
        for (int i = 0; i < 32 && udp.Available > 0; i++)
        {
            IPEndPoint source = new(IPAddress.Any, 0);
            var datagram = udp.Receive(ref source);
            if (datagram.Length <= 900) Handle(source, Encoding.ASCII.GetString(datagram));
        }
        long now = Environment.TickCount64;
        if (now - lastBroadcast < 250) return;
        lastBroadcast = now;
        foreach (var link in devices.Values)
        {
            if (now - Math.Max(link.SeenAt, link.HandshakeAt) < 5000) SendState(link);
        }
    }

    private void Handle(IPEndPoint source, string datagram)
    {
        int boundary = datagram.LastIndexOf('|');
        if (boundary < 0) return;
        string body = datagram[..boundary];
        string[] fields = body.Split('|');
        if (fields.Length is < 4 or > 10 || fields[0] != "TP1" ||
            !devices.TryGetValue(fields[2], out var link) ||
            !IsHex(datagram[(boundary + 1)..], 64) ||
            !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(datagram[(boundary + 1)..]),
                Signature(link.Pager.Key, body))) return;

        long now = Environment.TickCount64;
        if (fields is [_, "hello", _, var nonce] && IsHex(nonce, 32))
        {
            if (link.Pending is not { } offer || offer.Expires <= now ||
                !offer.Endpoint.Equals(source) || offer.Nonce != nonce)
                link.Pending = offer = new(source, nonce, RandomHex(16), now + 5000);
            Send(link, source, $"TP1|offer|{link.Pager.Id}|{nonce}|{offer.Session}");
        }
        else if (fields is [_, "ready", _, var responseNonce, var session] &&
            link.Pending is { } pending && pending.Expires > now && pending.Endpoint.Equals(source) &&
            pending.Nonce == responseNonce && pending.Session == session)
        {
            link.Active = pending;
            link.Pending = null;
            link.Sequence = link.SeenSequence = link.PreviewSequence = link.TelemetrySequence = 0;
            link.Battery = link.Rssi = link.Rtt = link.StateAge = null;
            link.HandshakeAt = now;
            link.SeenAt = -6000;
            link.PreviewSequence = SendState(link);
        }
        else if (link.Active is { } active && active.Endpoint.Equals(source) && active.Session == fields[3])
        {
            if (fields is [_, "ping", _, _, var marker] &&
                uint.TryParse(marker, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                Send(link, source, $"TP1|pong|{link.Pager.Id}|{active.Session}|{marker}");
            else if (fields is [_, "link", _, _, _, _, var rssiText, var rttText, var ageText, var counter])
            {
                if (!uint.TryParse(counter, NumberStyles.None, CultureInfo.InvariantCulture, out uint value) ||
                    value <= link.TelemetrySequence || !ReadReceipt(link, fields[4], fields[5], now, allowSame: true)) return;
                link.TelemetrySequence = value;
                link.Rssi = InRange(rssiText, -120, -1);
                link.Rtt = InRange(rttText, 0, 5000);
                link.StateAge = InRange(ageText, 0, 5000);
            }
            else if (fields is [_, "seen", _, _, _, _])
                ReadReceipt(link, fields[4], fields[5], now);
        }
    }

    private static int? InRange(string text, int min, int max) =>
        int.TryParse(text, CultureInfo.InvariantCulture, out int value) && value >= min && value <= max ? value : null;

    private static bool ReadReceipt(Link link, string sequenceText, string batteryText, long now, bool allowSame = false)
    {
        if (uint.TryParse(sequenceText, NumberStyles.None, CultureInfo.InvariantCulture, out uint sequence) &&
            sequence != 0 && (sequence > link.SeenSequence || (allowSame && sequence == link.SeenSequence)) &&
            sequence <= link.Sequence)
        {
            link.SeenSequence = sequence;
            link.SeenAt = now;
            link.Battery = InRange(batteryText, 0, 100);
            return true;
        }
        return false;
    }

    private uint SendState(Link link)
    {
        if (link.Active is not { } active || link.Paused) return 0;
        var preview = link.Preview;
        string body = FormattableString.Invariant(
            $"TP1|state|{link.Pager.Id}|{active.Session}|{++link.Sequence}|{preview.Inside}|{preview.Outside}|{TextHex(preview.Chat)}|{TextHex(preview.Next)}");
        if (preview.Test) body += "|test";
        Send(link, active.Endpoint, body);
        return link.Sequence;
    }

    private void Send(Link link, IPEndPoint endpoint, string body)
    {
        byte[] payload = Encoding.ASCII.GetBytes(body + "|" + Convert.ToHexStringLower(Signature(link.Pager.Key, body)));
        udp.Send(payload, endpoint);
    }

    private static byte[] Signature(string key, string body) =>
        HMACSHA256.HashData(Convert.FromHexString(key), Encoding.ASCII.GetBytes(body));

    private static string TextHex(string text) => Convert.ToHexStringLower(Encoding.UTF8.GetBytes(text));

    private static bool IsHex(string value, int length) =>
        value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public void Dispose() => udp.Dispose();
}
