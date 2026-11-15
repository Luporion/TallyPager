using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using TallyPager;

const string id = "123456789abc";
if (args is ["--arbiter", var serverAddress])
{
    await TestArbiterSocket(serverAddress);
    return;
}
string settings = Directory.CreateTempSubdirectory("tallypager-smoke-").FullName;
try
{
    string key;
    string firstSession;
    using var stick = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    using (var core = new PilotCore(settings, port: 0))
    {
        var pager = core.Prepare(id, "Test");
        core.Save(pager);
        key = pager.Key;

        Send(stick, core, $"TP1|hello|{id}|{new string('c', 32)}", new string('0', 64));
        core.Tick();
        Check(stick.Available == 0, "Invalid signature must not get an offer");

        firstSession = Connect(stick, core, key, new string('a', 32));
        core.SetPreview(id, new Preview(3, 2, "Kanzel enger", "Taufe"));
        var state = Read(stick, core, key);
        Check(state is [_, "state", id, _, _, "3", "2", "4b616e7a656c20656e676572", "5461756665", "live", "Innen", "Aussen"],
            "Expected independent layers and Cue/Next");

        // Drop the first state from the simulated stick's perspective. A later
        // snapshot must carry all fields, so the display can recover without an ACK.
        string[] repeated = Read(stick, core, key);
        Check(uint.Parse(repeated[4]) > uint.Parse(state[4]) && repeated[5..].SequenceEqual(state[5..]),
            "Periodic state must repeat the whole display");
        string seen = $"TP1|seen|{id}|{firstSession}|{repeated[4]}|77";
        Send(stick, core, seen, Tag(seen, key));
        core.Tick();
        Check(core.Statuses().Single() is { Battery: 77, Online: true, PreviewReceived: true },
            "Expected authenticated receipt and battery");
        string probe = $"TP1|ping|{id}|{firstSession}|42";
        Send(stick, core, probe, Tag(probe, key));
        Check(Read(stick, core, key) is [_, "pong", id, _, "42"], "Expected session-bound pong");
        string telemetry = $"TP1|link|{id}|{firstSession}|{repeated[4]}|76|-67|280|920|1";
        Send(stick, core, telemetry, Tag(telemetry, key));
        core.Tick();
        Check(core.Statuses().Single() is { Connection: Connection.Delayed, Rssi: -67, Rtt: 280 },
            "Expected the same delayed status and telemetry as the stick");
        core.SetPreview(id, Preview.Clear);
        Check(Read(stick, core, key)[5..].SequenceEqual(new[] { "0", "0", "", "", "live", "Innen", "Aussen" }),
            "Clear should overwrite Chat and Next");
    }
    using (var restarted = new PilotCore(settings, port: 0))
    {
        Check(restarted.Prepare(id, "Test").Key == key, "Pairing must survive Core restart");
        string newSession = Connect(stick, restarted, key, new string('b', 32));
        Check(newSession != firstSession, "Restart should negotiate a fresh session");
        string replay = $"TP1|seen|{id}|{firstSession}|1|99";
        Send(stick, restarted, replay, Tag(replay, key));
        restarted.Tick();
        Check(restarted.Statuses().Single().Battery is null, "Old-session telemetry must be ignored");
    }
    Console.WriteLine("PASS UDP pairing, invalid signature, independent tally, Cue/Next, lost snapshot, clear and Core restart");
    TestAssignments(settings);
    TestArbiter();
    await TestVmix();
    using (var atem = new AtemSource("127.0.0.1"))
        Check(atem.State.Inputs.Length == 0, "An ATEM without a source must remain unknown");
    if (args.Length > 0)
    {
        PagerUsb.LoadFirmware(args[0], settings);
        foreach (var image in PagerUsb.Images)
        {
            Check(new FileInfo(Path.Combine(settings, image.Name)).Length > 0, "Empty firmware image");
            File.Delete(Path.Combine(settings, image.Name));
        }
        Console.WriteLine("PASS separate firmware package and image checksums");
    }
}
finally
{
    foreach (var name in new[] { "devices.json", "devices.json.tmp" })
        File.Delete(Path.Combine(settings, name));
    Directory.Delete(settings);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static string Tag(string body, string key) => Convert.ToHexStringLower(
    HMACSHA256.HashData(Convert.FromHexString(key), Encoding.ASCII.GetBytes(body)));

static void Send(UdpClient stick, PilotCore core, string body, string tag) =>
    stick.Send(Encoding.ASCII.GetBytes(body + "|" + tag), new IPEndPoint(IPAddress.Loopback, core.Endpoint.Port));

static string[] Read(UdpClient stick, PilotCore core, string key)
{
    var timeout = Stopwatch.StartNew();
    while (timeout.ElapsedMilliseconds < 1500)
    {
        core.Tick();
        if (stick.Available > 0)
        {
            IPEndPoint remote = new(IPAddress.Any, 0);
            string packet = Encoding.ASCII.GetString(stick.Receive(ref remote));
            int boundary = packet.LastIndexOf('|');
            Check(packet[(boundary + 1)..] == Tag(packet[..boundary], key), "Unexpected packet signature");
            return packet[..boundary].Split('|');
        }
        Thread.Sleep(5);
    }
    throw new TimeoutException("The Core did not send an expected UDP packet");
}

static string Connect(UdpClient stick, PilotCore core, string key, string nonce)
{
    string hello = $"TP1|hello|{id}|{nonce}";
    Send(stick, core, hello, Tag(hello, key));
    var offer = Read(stick, core, key);
    Check(offer.Length == 5 && offer[1] == "offer" && offer[3] == nonce, "Invalid offer");
    string session = offer[4];
    string ready = $"TP1|ready|{id}|{nonce}|{session}";
    Send(stick, core, ready, Tag(ready, key));
    var initial = Read(stick, core, key);
    Check(initial[1] == "state" && initial[3] == session, "Pairing did not yield an initial state");
    return session;
}

static void TestAssignments(string settings)
{
    using var core = new PilotCore(settings, port: 0);
    var left = core.Devices.Single() with { Inside = new(MixerKind.Vmix, 1, "feed"), Outside = new(MixerKind.Atem, 1) };
    var right = core.Prepare("123456789abd", "Second");
    core.Save(left);
    core.Save(right);
    var atem = new MixerState("Verbunden", [new(1, "Kamera", 3)]);
    var vmix = new MixerState("Verbunden", [new(2, "Äußerer Feed", 2, "feed")]);
    var session = new OperatorSession(core);
    session.SetText([left.Id], "Test", "Next");
    session.Update(atem, vmix, 1000);
    Check(core.Statuses().First(s => s.Id == left.Id).View == new Preview(2, 3, "Test", "Next", false, "vMix", "ATEM"),
        "vMix GUID follows renumbering, inside/outside must be reversible");
    Check(core.Statuses().First(s => s.Id == right.Id).View == new Preview(3, 0, "", "", false, "ATEM", "vMix"),
        "Chat and mappings must stay isolated per stick");
    session.Start([left.Id], new(Scenario.VmixOnOff, 500, 2, 0, 1000));
    session.Update(atem, vmix, 1500);
    Check(core.Statuses().First(s => s.Id == left.Id).View == new Preview(1, 2, "Test", "Next", true, "vMix", "ATEM"),
        "Interval simulation must follow the swapped display assignment");
    session.Stop([left.Id]);
    session.Update(atem, MixerState.Off, 2000);
    Check(core.Statuses().First(s => s.Id == left.Id).View == new Preview(0, 3, "Test", "Next", false, "vMix", "ATEM"),
        "vMix loss must only affect the vMix layer");
    core.Save(left with { Inside = new(MixerKind.Off, 0) });
    session.SetText([right.Id], "Other", "Next B");
    session.SetText([left.Id, right.Id], "Chat only", null);
    session.SetText([left.Id], "", null);
    session.Update(atem, vmix, 2200);
    Check(core.Statuses().First(s => s.Id == left.Id).View == new Preview(0, 3, "", "Next", false, "off", "ATEM"),
        "Disabled layer must be explicitly hidden and clearing Chat must preserve Next");
    Check(session.Text(right.Id) == ("Chat only", "Next B"), "Group send must preserve each device's Next");
    session.SetText([right.Id], null, "");
    Check(session.Text(right.Id) == ("Chat only", ""), "Clearing Next must preserve Chat");
    Console.WriteLine("PASS separate devices, stable mapping, swapped layers and per-device interval simulation");
}

static void TestArbiter()
{
    Check(TallyArbiterSource.Endpoint("192.168.1.2")?.Port == 4455, "Wrong Tally Arbiter default port");
    Check(TallyArbiterSource.Endpoint("localhost:18455")?.Port == 18455, "Explicit port was lost");
    Check(TallyArbiterSource.Endpoint("https://arbiter.local")?.Scheme == "wss", "HTTPS should use secure WebSocket");
    Check(TallyArbiterSource.Endpoint("http://user@arbiter.local:4455") is null, "Do not accept credentials in a source URL");
    var cache = new ArbiterSnapshot();
    void Feed(string json, long time = 1000) => cache.Accept(json.AsMemory(), time);
    Feed("""["devices",[{"id":"cam","name":"Mobile Kamera","enabled":true}]]""");
    Feed("""["device_states",[{"deviceId":"cam","busId":"p","sources":["a1"]},{"deviceId":"cam","busId":"v","sources":["v1"]}]]""");
    Feed("""["bus_options",[{"id":"p","type":"program"},{"id":"v","type":"preview"}]]""");
    Feed("""["device_sources",[{"id":"a1","deviceId":"cam","sourceId":"a"},{"id":"v1","deviceId":"cam","sourceId":"v"}]]""");
    Check(cache.Build(1000) is null, "Missing source metadata must not publish tally");
    Feed("""["sources",[{"id":"a","sourceTypeId":"44b8bc4f","connected":true},{"id":"v","sourceTypeId":"58b6af42","connected":true}]]""");
    var state = cache.Build(1000)!;
    Check(state.Devices.Single().Tally == 3 && state.Atem.Single().Tally == 3 && state.Vmix.Single().Tally == 2,
        "Tally Arbiter must expose separate layers and program priority");
    Feed("""["sources",[{"id":"a","sourceTypeId":"44b8bc4f","connected":false},{"id":"v","sourceTypeId":"58b6af42","connected":true}]]""");
    state = cache.Build(1100)!;
    Check(state.Atem.Single().Tally == 0 && state.Vmix.Single().Tally == 2 && state.Devices.Single().Tally == 2,
        "A disconnected mixer must not promote stale Program");
    Check(cache.Build(2501) is null, "Old snapshots must expire");
    Console.WriteLine("PASS Tally Arbiter address parsing, snapshots, separate mixer layers and source health");
}

static async Task TestArbiterSocket(string endpoint)
{
    using var source = new TallyArbiterSource(TallyArbiterSource.Endpoint(endpoint) ??
        throw new ArgumentException("Invalid Tally Arbiter test endpoint"));
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (!source.State.Status.StartsWith("Verbunden", StringComparison.Ordinal))
        await Task.Delay(100, timeout.Token);
    Console.WriteLine("PASS real Tally Arbiter Engine.IO/Socket.IO connection and metadata polling");
}

static async Task TestVmix()
{
    using var end = new CancellationTokenSource(TimeSpan.FromSeconds(7));
    var token = end.Token;
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    using var source = new VmixSource("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
    var server = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync(token);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        while (await reader.ReadLineAsync(token) is string command)
        {
            string xml = "<vmix><inputs><input number=\"1\" key=\"one\" title=\"Bühne\"/></inputs></vmix>\r\n";
            string reply = command switch
            {
                "SUBSCRIBE TALLY" => "SUBSCRIBE OK TALLY\r\n",
                "XML" => $"TALLY OK 1\r\nXML {Encoding.UTF8.GetByteCount(xml)}\r\n{xml}",
                "TALLY" => "TALLY OK 1\r\n",
                _ => throw new InvalidOperationException("Unexpected vMix command")
            };
            byte[] bytes = Encoding.UTF8.GetBytes(reply);
            await stream.WriteAsync(bytes.AsMemory(0, bytes.Length / 2), token);
            await Task.Delay(10, token);
            await stream.WriteAsync(bytes.AsMemory(bytes.Length / 2), token);
        }
    }, token);
    while (source.State.Inputs.SingleOrDefault() is not { Name: "Bühne", Tally: 3, Key: "one" })
        await Task.Delay(20, token);
    source.Dispose();
    try { await server; }
    catch (IOException) { }
    Console.WriteLine("PASS vMix TCP subscription, interleaved tally and fragmented UTF-8 XML");
}
