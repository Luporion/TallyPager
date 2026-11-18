using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TallyPager;

internal enum MixerKind { Off, Atem, Vmix, Arbiter, ArbiterAtem, ArbiterVmix }
internal sealed record Assignment(MixerKind Mixer, int Input, string? Key = null);
internal sealed record Pager(string Id, string Name, string Key, Assignment? Inside = null, Assignment? Outside = null,
    string? ConfiguredTransport = null);
internal sealed record Preview(int Inside, int Outside, string Chat, string Next, bool Test = false,
    string InsideSource = "Innen", string OutsideSource = "Aussen", string ChatId = "", bool RequireAck = false)
{
    public static Preview Clear { get; } = new(0, 0, "", "");
}
internal enum Connection { Disconnected, Delayed, Connected }
internal readonly record struct DeviceSlot(int Number, string Mode, string? Gateway = null, int Channel = 0)
{
    public override string ToString() => $"Profil {Number + 1} · {Transport.Name(Mode)}";

    public static DeviceSlot[] Parse(string fields)
    {
        var slots = new List<DeviceSlot>(4);
        foreach (string field in fields.Split(';'))
        {
            string[] values = field.Split(',');
            if (values.Length < 2 || values[0].Length != 1 || values[0][0] is < '0' or > '3') continue;
            int number = values[0][0] - '0';
            if (slots.Any(slot => slot.Number == number)) continue;
            switch (values)
            {
                case [_, "udp" or "ble"]:
                    slots.Add(new(number, values[1]));
                    break;
                case [_, "espnow" or "espnow-lr", var gateway, var channel]
                    when Transport.IsMac(gateway) && int.TryParse(channel, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
                        && parsed is >= 1 and <= 13:
                    slots.Add(new(number, values[1], gateway, parsed));
                    break;
            }
        }
        return slots.ToArray();
    }
}
internal sealed record PagerStatus(string Id, string Name, Connection Connection, int? Battery, bool PreviewReceived,
    int? Rssi, int? Rtt, Preview View, bool CanAck, DateTimeOffset? SentAt,
    DateTimeOffset? ReceivedAt, DateTimeOffset? AcknowledgedAt, long? TelemetryAgeMs,
    string Transport, WifiSettings? Settings, FirmwareInfo? Firmware, string? Address = null,
    bool LastKnownAddress = false, int? ActiveSlot = null, DeviceSlot[]? Slots = null)
{
    public bool Online => Connection != Connection.Disconnected;
}

internal sealed class PilotCore : IDisposable
{
    public const int Port = 46224;
    internal const int ManagementPort = 46226;
    public static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TallyPager", "Pilot");
    private readonly string configPath;
    private readonly Dictionary<string, Link> devices = new(StringComparer.Ordinal);
    private readonly UdpClient udp = new(AddressFamily.InterNetwork);
    private IRadioBridge? radio;
    private IRadioBridge? bluetooth;
    private long lastBroadcast;

    private sealed record Peer(IPEndPoint? Ip, IRadioBridge? Bridge = null, string? Mac = null);
    private sealed record Offer(Peer Endpoint, string Nonce, string Session, long Expires);
    private sealed record ManagedPeer(IPEndPoint Endpoint, string Session, long Expires)
    {
        public uint Counter { get; set; }
        public WifiSettings? Settings { get; set; }
        public long LastSeen { get; set; }
        public FirmwareInfo? Firmware { get; set; }
    }
    private sealed class ConfigRequest(string id, string command, string session, long deadline)
    {
        public string Id { get; } = id;
        public string Command { get; } = command;
        public string Session { get; } = session;
        public long Deadline { get; } = deadline;
        public long LastSent { get; set; } = -6000;
        public bool Testing { get; set; }
        public TaskCompletionSource<WifiSettings> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class SlotRequest(string id, DeviceSlot slot, string session, long deadline)
    {
        public string Id { get; } = id;
        public DeviceSlot Slot { get; } = slot;
        public string Session { get; } = session;
        public long Deadline { get; } = deadline;
        public long LastSent { get; set; } = -6000;
        public bool Testing { get; set; }
        public TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
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
        public long ClockAt { get; set; } = -60000;
        public int? Battery { get; set; }
        public int? Rssi { get; set; }
        public int? Rtt { get; set; }
        public int? StateAge { get; set; }
        public bool Paused { get; set; }
        public bool CanAck { get; set; }
        public DateTimeOffset? SentAt { get; set; }
        public DateTimeOffset? ReceivedAt { get; set; }
        public DateTimeOffset? AcknowledgedAt { get; set; }
        public uint WifiLossCount { get; set; }
        public uint SnapshotLossCount { get; set; }
        public Preview Preview { get; set; } = Preview.Clear;
        public WifiSettings? Settings { get; set; }
        public FirmwareInfo? Firmware { get; set; }
        public FirmwareInfo? LastFirmware { get; set; }
        public ManagedPeer? Managed { get; set; }
        public string? DiscoverNonce { get; set; }
        public long DiscoverUntil { get; set; }
        public uint ConfigCounter { get; set; }
        public ConfigRequest? Configuration { get; set; }
        public SlotRequest? SlotChange { get; set; }
        public DeviceSlot[] Slots { get; set; } = [];
        public int? ActiveSlot { get; set; }
        public string? LastAddress { get; set; }
        public string Transport { get; set; } = pager.ConfiguredTransport ?? "udp";
    }

    internal IPEndPoint Endpoint => (IPEndPoint)udp.Client.LocalEndPoint!;
    internal string StorageDirectory => Path.GetDirectoryName(configPath)!;
    internal DiagnosticLog Diagnostics { get; }

    public PilotCore(string? dataDirectory = null, int port = Port)
    {
        configPath = Path.Combine(dataDirectory ?? DataDirectory, "devices.json");
        try
        {
            bool migrate = false;
            if (File.Exists(configPath))
            {
                foreach (var stored in JsonSerializer.Deserialize<Pager[]>(File.ReadAllText(configPath)) ?? [])
                {
                    bool encrypted = stored.Key.StartsWith(LocalSecret.Prefix, StringComparison.Ordinal);
                    var pager = encrypted ? stored with { Key = LocalSecret.Open(stored.Key) } : stored;
                    if (IsHex(pager.Id, 12) && IsHex(pager.Key, 64))
                    {
                        devices[pager.Id] = new(pager);
                        migrate |= !encrypted;
                    }
                }
            }
            udp.Client.ExclusiveAddressUse = true;
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            udp.EnableBroadcast = true;
            // An offline stick can trigger an ICMP Port Unreachable on Windows.
            // Other sticks must continue to receive despite that unrelated reply.
            const int udpConnectionReset = unchecked((int)0x9800000C);
            udp.Client.IOControl(udpConnectionReset, new byte[4], null);
            if (migrate) SaveDevices(Devices);
            Diagnostics = new(Path.Combine(StorageDirectory, "diagnostics.json"));
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
        var entries = devices.Values.Select(link => link.Pager).Where(x => x.Id != pager.Id).Append(pager);
        SaveDevices(entries);
        if (devices.TryGetValue(pager.Id, out var link))
        {
            if (pager.ConfiguredTransport != link.Pager.ConfiguredTransport && pager.ConfiguredTransport is { } mode)
                link.Transport = mode;
            link.Pager = pager;
        }
        else devices[pager.Id] = new(pager);
    }

    private void SaveDevices(IEnumerable<Pager> entries) =>
        ProfileStore.Write(configPath, entries.Select(p => p with { Key = LocalSecret.Seal(p.Key) }).ToArray(),
            JsonSerializerOptions.Default);

    public PagerStatus[] Statuses()
    {
        long now = Environment.TickCount64;
        return devices.Values.Select(link =>
        {
            long age = now - link.SeenAt;
            var state = age >= 3500 ? Connection.Disconnected :
                age >= 1700 || link.SeenSequence == 0 || link.StateAge >= 750 || link.Rtt >= 250 ? Connection.Delayed : Connection.Connected;
            bool online = state != Connection.Disconnected;
            var managed = CurrentManagement(link, now);
            var managedFirmware = managed?.Firmware;
            var firmware = link.Firmware ?? managedFirmware;
            if (managedFirmware is { Advertised: true } && firmware is { Advertised: false } &&
                firmware.VersionText == managedFirmware.VersionText) firmware = managedFirmware;
            var wifi = managedFirmware?.Supports(FirmwareInfo.Wifi) == true ? managed?.Settings :
                online && link.Firmware?.Supports(FirmwareInfo.Wifi) == true ? link.Settings : null;
            var address = online ? link.LastAddress : managed?.Endpoint.Address.ToString();
            bool canSwitch = online && link.Firmware?.Supports(FirmwareInfo.RemoteSlots) == true;
            return new PagerStatus(link.Pager.Id, link.Pager.Name, state, link.Battery,
                online && link.PreviewSequence > 0 && link.SeenSequence >= link.PreviewSequence,
                online ? link.Rssi : null, online ? link.Rtt : null, link.Preview, CanAcknowledge(link),
                link.SentAt, link.ReceivedAt, link.AcknowledgedAt, link.SeenAt >= 0 ? age : null,
                link.Transport, wifi, firmware ?? link.LastFirmware ??
                    (online && link.Transport != "udp" ? FirmwareInfo.LegacyRadio : null),
                address ?? link.LastAddress, address is null && link.LastAddress is not null,
                canSwitch ? link.ActiveSlot : null, canSwitch ? link.Slots : null);
        }).ToArray();
    }

    public void ObserveUsbFirmware(string id, FirmwareInfo? firmware)
    {
        if (firmware is not null && devices.TryGetValue(id, out var link))
            link.LastFirmware = PreferFirmware(link.LastFirmware, firmware);
    }

    private static bool CanAcknowledge(Link link) => link.Firmware?.Features is not null ?
        link.Firmware.Supports(FirmwareInfo.Acknowledge) : link.CanAck;

    private static FirmwareInfo PreferFirmware(FirmwareInfo? previous, FirmwareInfo incoming) =>
        previous is { Advertised: true } && previous.VersionText == incoming.VersionText && !incoming.Advertised ? previous : incoming;

    private static void ObserveFirmware(Link link, FirmwareInfo? firmware, ManagedPeer? peer = null)
    {
        if (firmware is null) return;
        if (peer is null) link.Firmware = PreferFirmware(link.Firmware, firmware);
        else peer.Firmware = PreferFirmware(peer.Firmware, firmware);
        link.LastFirmware = PreferFirmware(link.LastFirmware, firmware);
    }

    private static FirmwareInfo? ParseFirmware(string[] fields) =>
        fields is [_, "firmware", _, _, var version, var features] ?
            FirmwareInfo.Read(version, features.Length == 0 ? [] : features.Split(',')) : null;

    private static ManagedPeer? CurrentManagement(Link link, long now) =>
        link.Managed is { } peer && peer.Expires > now + 46000 && now - peer.LastSeen < 6500 ? peer : null;

    public void SetPreview(string id, Preview preview, bool paused = false)
    {
        var link = devices[id];
        if (link.Preview == preview && link.Paused == paused) return;
        if (link.Preview.ChatId != preview.ChatId)
            link.SentAt = link.ReceivedAt = link.AcknowledgedAt = null;
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
            if (datagram.Length <= 900) Handle(new(source), Encoding.ASCII.GetString(datagram));
        }
        ReadRadio(radio);
        ReadRadio(bluetooth);
        long now = Environment.TickCount64;
        if (now - lastBroadcast < 250) return;
        lastBroadcast = now;
        foreach (var link in devices.Values)
        {
            RetryConfiguration(link, now);
            RetrySlot(link, now);
            if (now - Math.Max(link.SeenAt, link.HandshakeAt) < 5000)
            {
                SendState(link);
                if (!link.Paused && link.Firmware?.Supports("clock") == true &&
                    link.Active is { } active && now - link.ClockAt >= 15000)
                {
                    int seconds = (int)DateTime.Now.TimeOfDay.TotalSeconds;
                    Send(link, active.Endpoint, FormattableString.Invariant($"TP1|clock|{link.Pager.Id}|{active.Session}|{seconds}"));
                    link.ClockAt = now;
                }
            }
        }
    }

    private void ReadRadio(IRadioBridge? bridge)
    {
        if (bridge is null) return;
        for (int i = 0; i < 24 && bridge.TryRead(out var packet); i++)
            if (packet.Payload.Length <= 900 && Transport.IsMac(packet.Address))
                Handle(new(null, bridge, packet.Address), packet.Payload);
    }

    public void AttachRadio(IRadioBridge? bridge) => ReplaceBridge(ref radio, bridge);
    public void AttachBluetooth(IRadioBridge? bridge) => ReplaceBridge(ref bluetooth, bridge);

    private void ReplaceBridge(ref IRadioBridge? current, IRadioBridge? bridge)
    {
        if (ReferenceEquals(current, bridge)) return;
        var previous = current;
        previous?.Dispose();
        current = bridge;
        if (previous is null) return;
        foreach (Link link in devices.Values)
        {
            if (ReferenceEquals(link.Pending?.Endpoint.Bridge, previous)) link.Pending = null;
            if (!ReferenceEquals(link.Active?.Endpoint.Bridge, previous)) continue;
            link.Active = null;
            link.SeenAt = -6000;
        }
    }

    public Task<string> SwitchSlot(string id, DeviceSlot slot)
    {
        long now = Environment.TickCount64;
        if (!devices.TryGetValue(id, out var link) || link.Active is not { } active || now - link.SeenAt >= 3500 ||
            link.Paused || link.Firmware?.Supports(FirmwareInfo.RemoteSlots) != true)
            throw new InvalidOperationException("Stick per Tally verbinden und Firmware für den Funkwechsel prüfen.");
        if (link.Configuration is not null || link.SlotChange is not null)
            throw new InvalidOperationException("Für diesen Stick läuft bereits eine Änderung.");
        if (!link.Slots.Contains(slot) || link.ActiveSlot == slot.Number)
            throw new InvalidOperationException("Ein anderes, bereits auf dem Stick gespeichertes Geräteprofil auswählen.");
        var request = new SlotRequest(RandomHex(16), slot, active.Session, now + 45000);
        link.SlotChange = request;
        RetrySlot(link, now);
        return request.Completion.Task;
    }

    private void RetrySlot(Link link, long now)
    {
        if (link.SlotChange is not { } request) return;
        if (now >= request.Deadline)
        {
            link.SlotChange = null;
            request.Completion.TrySetResult("timeout");
            return;
        }
        if (link.Active is not { } active || active.Session != request.Session || request.Testing ||
            now - request.LastSent < 750) return;
        request.LastSent = now;
        try
        {
            Send(link, active.Endpoint, FormattableString.Invariant(
                $"TP1|switch_slot|{link.Pager.Id}|{active.Session}|{request.Id}|{request.Slot.Number}"));
        }
        catch (SocketException) { } // The wireless peer can drop between retries.
    }

    public Task<WifiSettings> ChangeWifi(string id, WifiChange change)
    {
        long now = Environment.TickCount64;
        if (!devices.TryGetValue(id, out var link))
            throw new InvalidOperationException("Der Stick ist auf diesem Rechner nicht gepaart.");
        if (link.Configuration is not null || link.SlotChange is not null)
            throw new InvalidOperationException("Für diesen Stick läuft bereits eine Änderung.");
        var managed = CurrentManagement(link, now);
        var active = link.Settings is not null && link.Firmware?.Supports(FirmwareInfo.Wifi) == true && now - link.SeenAt < 3500 &&
            link.Active?.Endpoint.Ip is not null ? link.Active : null;
        if (managed?.Firmware?.Supports(FirmwareInfo.Wifi) != true) managed = null;
        if (active is null && managed is null)
            throw new InvalidOperationException("Der Stick ist nicht erreichbar. Bitte erneut im WLAN suchen.");
        string request = RandomHex(16);
        string session = active?.Session ?? managed!.Session;
        uint counter = active is null ? ++managed!.Counter : ++link.ConfigCounter;
        string command = WifiManagement.Command(id, link.Pager.Key, session, request, counter, change);
        var pending = new ConfigRequest(request, command, session, now + 45000);
        link.Configuration = pending;
        RetryConfiguration(link, now);
        return pending.Completion.Task;
    }

    public int DiscoverWifi(IPAddress? address = null)
    {
        IPAddress[] targets = address is null ? WifiManagement.BroadcastAddresses().ToArray() : [address];
        long now = Environment.TickCount64;
        foreach (Link link in devices.Values)
        {
            if (link.Configuration is not null) continue;
            string challenge = RandomHex(16);
            link.DiscoverNonce = challenge;
            link.DiscoverUntil = now + 6500;
            foreach (var target in targets)
            {
                try { Send(link, new(new IPEndPoint(target, ManagementPort)), $"TP1|discover|{link.Pager.Id}|{challenge}"); }
                catch (SocketException) when (address is null) { }
            }
        }
        return targets.Length;
    }

    private void RetryConfiguration(Link link, long now)
    {
        if (link.Configuration is not { } pending) return;
        if (now >= pending.Deadline)
        {
            link.Configuration = null;
            pending.Completion.TrySetException(new TimeoutException(
                "Der Stick hat kein Ergebnis gemeldet. Aktuellen Stand nach Wiederverbindung prüfen."));
        }
        else if (!pending.Testing && now - pending.LastSent >= 750)
        {
            Peer? target = link.Active is { Endpoint.Ip: not null } active && active.Session == pending.Session
                ? active.Endpoint : link.Managed is { } managed && managed.Session == pending.Session
                    ? new(managed.Endpoint) : null;
            if (target is null) return;
            pending.LastSent = now;
            Send(link, target, pending.Command);
        }
    }

    private void Handle(Peer source, string datagram)
    {
        int boundary = datagram.LastIndexOf('|');
        if (boundary < 0) return;
        string body = datagram[..boundary];
        string[] fields = body.Split('|');
        if (fields.Length is < 4 or > 11 || fields[0] != "TP1" ||
            !devices.TryGetValue(fields[2], out var link) ||
            !IsHex(datagram[(boundary + 1)..], 64) ||
            !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(datagram[(boundary + 1)..]),
                Signature(link.Pager.Key, body))) return;

        long now = Environment.TickCount64;
        if (source.Ip is { Port: ManagementPort } management)
        {
            if (fields is [_, "found", _, var challenge, var access, ..] && now < link.DiscoverUntil &&
                link.DiscoverNonce == challenge && IsHex(access, 32) &&
                WifiManagement.Parse(["TP1", "cfg_info", fields[2], access, .. fields[5..]], management.Address) is { } found)
            {
                var discovered = new ManagedPeer(management, access, now + 90000) { Settings = found, LastSeen = now };
                link.Managed = discovered;
                link.LastAddress ??= management.Address.ToString();
                ObserveFirmware(link, FirmwareInfo.Read(found.Version), discovered);
                link.DiscoverNonce = null;
            }
            else if (link.Managed is { } peer && peer.Expires > now && peer.Session == fields[3] &&
                peer.Endpoint.Equals(management))
            {
                if (WifiManagement.Parse(fields, management.Address) is { } updated)
                {
                    peer.Settings = updated;
                    peer.LastSeen = now;
                    ObserveFirmware(link, FirmwareInfo.Read(updated.Version), peer);
                    ConfigResult(link, updated);
                }
                else ObserveFirmware(link, ParseFirmware(fields), peer);
            }
            return;
        }
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
            link.CanAck = false;
            link.Firmware = null;
            link.Settings = null;
            link.Slots = [];
            link.ActiveSlot = null;
            link.LastAddress = source.Ip?.Address.ToString() ?? Transport.DisplayAddress(source.Mac);
            link.ConfigCounter = 0;
            link.Transport = source.Bridge?.Mode ?? "udp";
            link.HandshakeAt = now;
            link.ClockAt = -60000;
            link.SeenAt = -6000;
            link.PreviewSequence = SendState(link);
        }
        else if (link.Active is { } active && active.Endpoint.Equals(source) && active.Session == fields[3])
        {
            if (fields is [_, "caps", _, _, "ack"])
                link.CanAck = true;
            else if (fields[1] == "firmware")
                ObserveFirmware(link, ParseFirmware(fields));
            else if (fields is [_, "slots", _, _, var slot, var entries] && InRange(slot, 0, 3) is int current)
            {
                link.ActiveSlot = current;
                link.Slots = DeviceSlot.Parse(entries);
            }
            else if (fields is [_, "slot_result", _, _, var request, var result] &&
                link.SlotChange is { } change && request == change.Id)
            {
                if (result == "testing") change.Testing = true;
                else if (result is "rollback" or "rejected" || result == "applied" &&
                    active.Session != change.Session && link.Transport == change.Slot.Mode && link.ActiveSlot == change.Slot.Number)
                {
                    link.SlotChange = null;
                    change.Completion.TrySetResult(result);
                }
            }
            else if (source.Ip is { } ip && fields[1] == "cfg_info" &&
                WifiManagement.Parse(fields, ip.Address) is { } settings)
            {
                link.Settings = settings;
                ObserveFirmware(link, FirmwareInfo.Read(settings.Version));
                ConfigResult(link, settings);
            }
            else if (fields is [_, "chat_rcpt", _, _, var chatId, var kind] && chatId.Length > 0 &&
                chatId == link.Preview.ChatId && kind is ("shown" or "ack"))
            {
                link.ReceivedAt ??= DateTimeOffset.UtcNow;
                if (kind == "ack" && link.Preview.RequireAck && (link.Firmware?.Features is null || CanAcknowledge(link)))
                {
                    link.CanAck = true;
                    link.AcknowledgedAt ??= DateTimeOffset.UtcNow;
                    Send(link, source, $"TP1|chat_ok|{link.Pager.Id}|{active.Session}|{chatId}");
                }
            }
            else if (fields is [_, "diag", _, _, var wifi, var snapshots, var reason, var lastAge, var downtime, var missed, var stateDowntime] &&
                uint.TryParse(wifi, out uint wifiCount) && uint.TryParse(snapshots, out uint snapshotCount) &&
                int.TryParse(reason, out int reasonCode) && uint.TryParse(lastAge, out uint ageMs) &&
                uint.TryParse(downtime, out uint outageMs) && uint.TryParse(missed, out uint gaps) &&
                uint.TryParse(stateDowntime, out uint stateOutageMs))
            {
                if (wifiCount != link.WifiLossCount)
                    Diagnostics.Add(link.Pager.Name, "WLAN-Unterbrechung", $"Zähler {wifiCount}, Grund {reasonCode}, letzte Funkpause {outageMs} ms");
                if (snapshotCount != link.SnapshotLossCount)
                    Diagnostics.Add(link.Pager.Name, "PC-Zustände fehlen", $"Zähler {snapshotCount}, Alter {ageMs} ms beim Abbruch, " +
                        $"nach {stateOutageMs} ms wieder verbunden, Sequenzlücken {gaps}");
                link.WifiLossCount = wifiCount;
                link.SnapshotLossCount = snapshotCount;
            }
            else if (fields is [_, "ping", _, _, var marker] &&
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

    private static void ConfigResult(Link link, WifiSettings settings)
    {
        if (link.Configuration is not { } change || change.Id != settings.RequestId) return;
        if (settings.Result == "testing") change.Testing = true;
        else if (settings.Result != "idle")
        {
            link.Configuration = null;
            change.Completion.TrySetResult(settings);
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
        body += $"|{(preview.Test ? "test" : "live")}|{preview.InsideSource}|{preview.OutsideSource}";
        if (preview.ChatId.Length > 0)
        {
            bool requestAck = preview.RequireAck && (link.Firmware?.Features is null || CanAcknowledge(link));
            body += $"|{preview.ChatId}|{(requestAck ? 1 : 0)}";
        }
        if (!Send(link, active.Endpoint, body)) return 0;
        if (preview.ChatId.Length > 0) link.SentAt ??= DateTimeOffset.UtcNow;
        return link.Sequence;
    }

    private bool Send(Link link, Peer endpoint, string body)
    {
        string packet = body + "|" + Convert.ToHexStringLower(Signature(link.Pager.Key, body));
        if (endpoint.Ip is { } ip) return udp.Send(Encoding.ASCII.GetBytes(packet), ip) > 0;
        return endpoint.Bridge is { } bridge && endpoint.Mac is { } mac && bridge.Send(mac, packet);
    }

    private static byte[] Signature(string key, string body) =>
        HMACSHA256.HashData(Convert.FromHexString(key), Encoding.ASCII.GetBytes(body));

    private static string TextHex(string text) => Convert.ToHexStringLower(Encoding.UTF8.GetBytes(text));

    private static bool IsHex(string value, int length) =>
        value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public void Dispose()
    {
        radio?.Dispose();
        bluetooth?.Dispose();
        Diagnostics.Dispose();
        udp.Dispose();
    }
}
