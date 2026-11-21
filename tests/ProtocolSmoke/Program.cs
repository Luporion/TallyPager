using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    string key = PilotCore.RandomHex(32);
    string firstSession;
    string deviceConfiguration = Path.Combine(settings, "devices.json");
    File.WriteAllText(deviceConfiguration, JsonSerializer.Serialize(new[] { new Pager(id, "Test", key) }));
    using var stick = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    using (var core = new PilotCore(settings, port: 0))
    {
        Check(core.Prepare(id, "Test").Key == key && !File.ReadAllText(deviceConfiguration).Contains(key),
            "Legacy paired key must survive DPAPI migration");

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
    TestAcknowledgements(settings);
    TestFirmwareCompatibility(settings);
    TestRadioBridge(settings);
    TestBasePreview(settings);
    await TestRemoteSlots(settings);
    TestRadioFragments();
    await TestWifiManagement(settings);
    await TestWifiDiscovery(settings);
    TestAssignments(settings);
    TestManualPresentation(settings);
    TestPresentationProtocol(settings);
    await TestDisplayPower(settings);
    TestRegieCheck(settings);
    await TestDiagnosticStorage(settings);
    TestProfiles(settings);
    await TestCompanion();
    TestArbiter();
    await TestVmix();
    await TestVmixWithoutSubscription();
    await TestVmixWeb();
    await TestVmixFailure();
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
        await TestGitHubUpdate(settings, Path.GetFullPath(args[0]), args.Length > 1 ? Path.GetFullPath(args[1]) : null);
        if (args.Length > 1)
        {
            PagerUsb.LoadFirmware(args[1], null, esp8266: true);
            try
            {
                PagerUsb.LoadFirmware(args[1], null);
                throw new InvalidOperationException("ESP8266 firmware must never be accepted as a M5Stick package");
            }
            catch (InvalidDataException) { }
            Console.WriteLine("PASS ESP8266 firmware package, checksums and board validation");
        }
    }
}
finally
{
    foreach (string file in Directory.EnumerateFiles(settings)) File.Delete(file);
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
    Preview Left() => core.Statuses().First(s => s.Id == left.Id).View with { ChatId = "" };
    Check(Left() == new Preview(2, 3, "Test", "Next", false, "vMix", "ATEM"),
        "vMix GUID follows renumbering, inside/outside must be reversible");
    Check(core.Statuses().First(s => s.Id == right.Id).View == new Preview(3, 0, "", "", false, "ATEM", "vMix"),
        "Chat and mappings must stay isolated per stick");
    session.Start([left.Id], new(Scenario.VmixOnOff, 500, 2, 0, 1000));
    session.Update(atem, vmix, 1500);
    Check(Left() == new Preview(1, 2, "Test", "Next", true, "vMix", "ATEM"),
        "Interval simulation must follow the swapped display assignment");
    session.Stop([left.Id]);
    session.Update(atem, MixerState.Off, 2000);
    Check(Left() == new Preview(0, 3, "Test", "Next", false, "vMix", "ATEM"),
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

static void TestFirmwareCompatibility(string settings)
{
    const string mac = "10061c286f4c";
    var uiVersion = Version.Parse(PagerUsb.Version);
    string newerVersion = new Version(uiVersion.Major, uiVersion.Minor, uiVersion.Build + 1).ToString();
    var legacyBase = PagerUsb.ParseStatus($$"""TP {"id":"{{id}}","version":"0.6.0","baseRole":true,"radioReady":true,"mode":"ble","radioAddress":"{{mac}}"}""");
    var early = PagerUsb.ParseStatus($$"""TP {"id":"{{id}}","version":"0.4.0","configured":true}""");
    var future = PagerUsb.ParseStatus($$"""TP {"id":"{{id}}","version":"{{newerVersion}}","features":["usb-config","radio-base","ble","screen-colors-v2"],"configured":true,"baseRole":true,"radioReady":true,"mode":"ble","futureObject":{"other":2} }""");
    Check(legacyBase?.Firmware is { Older: true, Advertised: false } && RadioBridge.CanConnect(legacyBase),
        "0.6.0 USB radio base must remain compatible without an update");
    Check(early?.Firmware is { Older: true } && early.Firmware.Missing.Contains("WLAN-Einstellungen") &&
        !early.Firmware.AllowsUsbMode("ble"), "Legacy firmware must show actual missing features");
    Check(future?.Firmware is { Newer: true, Advertised: true } && RadioBridge.CanConnect(future) &&
        future.Firmware.Unknown.SequenceEqual(new[] { "screen-colors-v2" }) &&
        future.Firmware.Missing.Contains("Chat quittieren"), "New USB versions must retain known features and list new feature IDs");
    Check(!RadioBridge.CanConnect(future! with { Mode = "future-radio" }), "Unknown radio mode must not be started");
    var pager = new Pager(id, "Gerät", PilotCore.RandomHex(32));
    string command = PagerUsb.ConfigCommand(early!, pager, "192.168.1.1", "", "", "udp", "", 6);
    Check(!command.Contains("\"mode\""), "Old USB config must keep its original command shape");
    string modernCommand = PagerUsb.ConfigCommand(legacyBase!, pager, "192.168.1.1", "", "", "udp", "", 6);
    Check(modernCommand.Contains("\"mode\":\"udp\""), "Provisioning a 0.6.0 base must restore the correct pager mode");
    var slots = legacyBase! with { Version = PagerUsb.Version, Features = ["usb-config", "radio-base", "device-slots"] };
    string slotCommand = PagerUsb.ConfigCommand(slots, pager, "192.168.1.1", "Test-WLAN", "", "udp", "", 6, slot: 2);
    Check(slotCommand.Contains("\"slot\":2"), "USB must transmit the selected device profile");
    try { PagerUsb.ConfigCommand(early!, pager, "", "", "", "ble", "", 6); throw new Exception("Unsupported USB mode accepted"); }
    catch (InvalidOperationException) { }

    using var core = new PilotCore(settings, port: 0);
    using var stick = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    string key = core.Devices.Single().Key;
    string session = Connect(stick, core, key, new string('e', 32));
    void SendReport(string body)
    {
        Send(stick, core, body, Tag(body, key));
        core.Tick();
    }
    SendReport($"TP1|link|{id}|{session}|1|82|-48|12|10|1");
    SendReport($"TP1|cfg_info|{id}|{session}|0.6.0|192.168.1.2|5061676572|||idle");
    Check(core.Statuses().Single() is { Firmware.VersionText: "0.6.0", Settings: not null, CanAck: true },
        "Unmodified 0.6.0 WLAN telemetry must provide a version and preserve WLAN management");
    SendReport($"TP1|firmware|{id}|{new string('f', 32)}|{newerVersion}|screen-colors-v2");
    Check(core.Statuses().Single().Firmware?.VersionText == "0.6.0", "Old session provided a firmware capability list");
    SendReport($"TP1|firmware|{id}|{session}|{newerVersion}|usb-config,link-stats,screen-colors-v2");
    SendReport($"TP1|cfg_info|{id}|{session}|{newerVersion}|192.168.1.2|5061676572|||idle");
    SendReport($"TP1|caps|{id}|{session}|ack");
    var report = core.Statuses().Single();
    Check(report.Firmware is { Advertised: true } && report.Firmware.VersionText == newerVersion &&
        report.Firmware.Unknown.SequenceEqual(new[] { "screen-colors-v2" }) && report.Settings is null && !report.CanAck,
        "Explicit firmware capabilities must win over older telemetry and control optional actions");
    core.SetPreview(id, new Preview(3, 1, "Text", "", ChatId: PilotCore.RandomHex(16), RequireAck: true));
    string[] state;
    do { state = Read(stick, core, key); } while (state.Length < 14);
    Check(state[5] == "3" && state[7] == "54657874" && state[13] == "0", "Known core behavior must remain usable without optional ACK");
    Console.WriteLine("PASS version-independent USB setup and base, legacy WLAN version, authenticated future features and optional action gating");
}

static void TestRegieCheck(string settings)
{
    using var core = new PilotCore(settings, port: 0);
    var pagers = core.Devices;
    core.Save(pagers[0] with { ConfiguredTransport = "espnow-lr" });
    var offline = core.Statuses().First(p => p.Id == pagers[0].Id);
    Check(offline.Transport == "espnow-lr", "The check needs to know the configured radio even if the pager is offline");
    var connected = core.Statuses().First(p => p.Id == pagers[1].Id) with
    {
        Connection = Connection.Connected, Rtt = 25, Firmware = FirmwareInfo.Read("0.6.0")
    };
    var assignments = new CameraRole(Guid.NewGuid(), "Mobile Kamera", new(MixerKind.Vmix, 1, "feed"),
        new(MixerKind.Off, 0));
    var missing = new CameraRole(Guid.NewGuid(), "Zweite Kamera", new(MixerKind.Atem, 5), new(MixerKind.Off, 0));
    var unbound = new CameraRole(Guid.NewGuid(), "Reserve", new(MixerKind.Off, 0), new(MixerKind.Off, 0));
    var profile = new RegieProfile(Guid.NewGuid(), "Probe", new("192.168.0.20", "192.168.0.30"),
        [assignments, missing, unbound], [], []);
    var bindings = new Dictionary<Guid, string> { [assignments.Id] = connected.Id, [missing.Id] = offline.Id };
    var vmix = new MixerState("Verbunden", [new(8, "ATEM Feed", 2, "FEED"), new(1, "Fremder Eingang", 3, "other")]);
    var checks = RegieCheck.Run(profile, bindings, [offline, connected], MixerState.Off, vmix, ArbiterState.Off, "espnow");
    Check(checks.Any(row => row is { Target: "ATEM", Result: CheckResult.Problem }) &&
        checks.Any(row => row.Target == "Mobile Kamera · Innen" && row.Result == CheckResult.Ready && row.Detail.Contains("ATEM Feed")) &&
        checks.Any(row => row is { Target: "USB-Funkbasis", Result: CheckResult.Problem }) &&
        checks.Any(row => row is { Target: "Reserve", Result: CheckResult.Problem }) &&
        checks.Any(row => row is { Target: "Zweite Kamera · Firmware", Result: CheckResult.Warning }),
        "Regiecheck missed a source, actual configured input, mismatched base, unbound pager or missing firmware");
    Check(!checks.Any(row => row.Target == "Tally Arbiter") &&
        !checks.Any(row => row.Target == "Mobile Kamera · Außen"), "Unused Arbiter and disabled display layer should not block readiness");
    var changedCatalog = vmix with { Inputs = [new(1, "Fremder Eingang", 3, "other")] };
    var changedChecks = RegieCheck.Run(profile, bindings, [offline, connected], MixerState.Off, changedCatalog,
        ArbiterState.Off, "espnow-lr");
    Check(changedChecks.Any(row => row is { Target: "Mobile Kamera · Innen", Result: CheckResult.Problem }) &&
        changedChecks.Any(row => row is { Target: "USB-Funkbasis", Result: CheckResult.Ready }),
        "A changed vMix catalog must not match by number if an input key was saved");

    var session = new OperatorSession(core);
    session.Update(MixerState.Off, vmix, 1000);
    var baseline = core.Statuses().ToDictionary(p => p.Id, p => p.View);
    Check(session.StartPagerCheck(pagers[0].Id, 1000) && !session.StartPagerCheck(pagers[0].Id, 1100),
        "A test must not restart or replace a running simulation");
    session.Update(MixerState.Off, vmix, 1000);
    Check(core.Statuses().First(p => p.Id == pagers[0].Id).View is { Test: true, Inside: 2, Outside: 1 } &&
        core.Statuses().First(p => p.Id == pagers[1].Id).View == baseline[pagers[1].Id], "Test was not isolated to the selected pager");
    session.Update(MixerState.Off, vmix, 3000);
    Check(core.Statuses().First(p => p.Id == pagers[0].Id).View is { Test: true, Inside: 1, Outside: 3 }, "Second test phase failed");
    session.Update(MixerState.Off, vmix, 7000);
    Check(core.Statuses().All(p => p.View == baseline[p.Id]) && !session.IsTesting(pagers[0].Id),
        "Timer must remove only the temporary test and restore current live state");
    using var restarted = new PilotCore(settings, port: 0);
    Check(restarted.Statuses().First(p => p.Id == pagers[0].Id).Transport == "espnow-lr",
        "The configured transport must be restored on restart");
    Console.WriteLine("PASS readiness for sources, mappings, firmware, pager roles and base; single-pager test and automatic live restore");
}

static async Task TestDiagnosticStorage(string settings)
{
    string path = Path.Combine(settings, "history-test.json");
    using (var log = new DiagnosticLog(path))
    {
        for (int i = 0; i < DiagnosticLog.Capacity + 5; i++) log.Add("Stick A", "Test", $"Ereignis {i}");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        while (!File.Exists(path)) await Task.Delay(50, timeout.Token);
        var onDisk = JsonSerializer.Deserialize<DiagnosticEntry[]>(File.ReadAllText(path), ProfileStore.Json);
        Check(onDisk is { Length: DiagnosticLog.Capacity } && onDisk[0].Detail == "Ereignis 5",
            "Periodic saving should retain only the latest entries");
        log.Add("PC", "Anwendung", "Beendet");
    }
    using (var restarted = new DiagnosticLog(path))
        Check(restarted.Entries.Last().Detail == "Beendet" && restarted.Entries.Length == DiagnosticLog.Capacity,
            "The final unsaved event must survive a restart");
    File.WriteAllText(path, "{ broken data");
    using (var repaired = new DiagnosticLog(path))
    {
        Check(repaired.StorageIssue is not null && repaired.Entries.Length == 0, "Corrupt history must not break the application");
        repaired.Add("PC", "Anwendung", "Neuer Verlauf");
    }
    using var recovered = new DiagnosticLog(path);
    Check(recovered.StorageIssue is null && recovered.Entries.Single().Detail == "Neuer Verlauf", "A new history must recover after bad data");
    Console.WriteLine("PASS bounded diagnostic history, periodic saving, restart, shutdown flush and corrupt-file recovery");
}

static async Task TestGitHubUpdate(string settings, string firmware, string? baseFirmware)
{
    string exe = Path.Combine(Path.GetDirectoryName(firmware)!, GitHubUpdates.ExeName);
    if (!File.Exists(exe))
    {
        Console.WriteLine("SKIP paired updater test because the supplied firmware ZIP has no accompanying Windows EXE");
        return;
    }
    static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    var version = Version.Parse(PagerUsb.Version);
    string firmwareName = GitHubUpdates.FirmwareName(version);
    string exeHash = Hash(exe);
    byte[] sidecar = Encoding.ASCII.GetBytes($"{Hash(firmware)}  {firmwareName}\n");
    var asset = new GitHubAsset(1, GitHubUpdates.ExeName, new FileInfo(exe).Length, "sha256:" + exeHash, "uploaded");
    var release = new GitHubRelease($"v{version}", false, false,
        [asset, new(2, firmwareName, new FileInfo(firmware).Length, null, "uploaded"),
            new(3, firmwareName + ".sha256", sidecar.Length, null, "uploaded")]);
    if (baseFirmware is not null)
        release = release with { Assets = [.. release.Assets, new(4, PagerUsb.Esp8266FirmwareFileName,
            new FileInfo(baseFirmware).Length, "sha256:" + Hash(baseFirmware), "uploaded")] };
    string root = Path.Combine(settings, "updates");
    var progress = new Progress<string>(_ => { });
    int executableDownloads = 0;
    bool altered = false;
    bool unsafeRedirect = false;
    using var client = new HttpClient(new FakeHttp(request =>
    {
        var uri = request.RequestUri!;
        if (uri.Host == "api.github.com" && uri.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal))
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(release, ProfileStore.Json)) };
        string name = Uri.UnescapeDataString(uri.Segments[^1]);
        if (uri.Host == "github.com" && name == asset.Name)
            return new(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri(unsafeRedirect ? "https://example.invalid/payload" :
                    "https://release-assets.githubusercontent.com/" + asset.Name) }
            };
        if (uri.Host == "release-assets.githubusercontent.com" && name == asset.Name)
        {
            executableDownloads++;
            return new(HttpStatusCode.OK) { Content = altered ? new ByteArrayContent([1, 2, 3]) : new StreamContent(File.OpenRead(exe)) };
        }
        if (uri.Host == "github.com" && name == firmwareName)
            return new(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(firmware)) };
        if (uri.Host == "github.com" && name == firmwareName + ".sha256")
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(sidecar) };
        if (uri.Host == "github.com" && name == PagerUsb.Esp8266FirmwareFileName && baseFirmware is not null)
            return new(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(baseFirmware)) };
        throw new InvalidOperationException($"Unexpected connection to {uri.Host}");
    }));
    var updater = new GitHubUpdates(client);
    try
    {
        var found = await updater.Check(CancellationToken.None);
        Check(GitHubUpdates.ReleaseVersion(found) == version, "Release metadata read failed");
        var update = await updater.Download(found, root, progress, CancellationToken.None);
        Check(Hash(update.Executable) == exeHash && Hash(update.Firmware) == Hash(firmware) &&
            Path.GetDirectoryName(update.Executable) == Path.Combine(root, version.ToString()),
            "Matching executable and firmware must be installed together");
        if (baseFirmware is not null)
            Check(Hash(Path.Combine(Path.GetDirectoryName(update.Executable)!, PagerUsb.Esp8266FirmwareFileName)) == Hash(baseFirmware),
                "The optional headless firmware must be installed beside the Windows executable");
        var repeat = await updater.Download(found, root, progress, CancellationToken.None);
        Check(repeat == update && executableDownloads == 1, "An already verified update must not be downloaded again");
        try
        {
            await updater.Download(found with { Tag = "v0.1.0" }, root, progress, CancellationToken.None);
            throw new Exception("Downgrade accepted");
        }
        catch (InvalidOperationException) { }

        string damaged = Path.Combine(root, "invalid");
        altered = true;
        try
        {
            var smallAsset = asset with { Size = 3 };
            await updater.Download(found with { Assets = [smallAsset, .. found.Assets.Skip(1)] }, damaged, progress, CancellationToken.None);
            throw new Exception("Damaged download accepted");
        }
        catch (InvalidDataException) { }
        Check(!Directory.EnumerateFileSystemEntries(damaged).Any(), "Invalid assets or staging were left behind");
        unsafeRedirect = true;
        try
        {
            await updater.Download(found, damaged, progress, CancellationToken.None);
            throw new Exception("Offsite redirect accepted");
        }
        catch (InvalidDataException) { }
        Console.WriteLine("PASS GitHub updates, asset redirects, SHA-256, matched EXE/ZIP, no downgrades, caching and invalid downloads");
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}

static void TestManualPresentation(string settings)
{
    string configurationPath = Path.Combine(settings, "devices.json");
    string original = File.ReadAllText(configurationPath);
    using var core = new PilotCore(settings, 0);
    var first = core.Devices.Single(pager => pager.Id == id) with { Inside = new(MixerKind.Vmix, 2), Outside = new(MixerKind.Atem, 1) };
    core.Save(first);
    const string secondId = "123456789abd";
    core.Save(new(secondId, "Zweiter Stick", PilotCore.RandomHex(32), new(MixerKind.Atem, 2), new(MixerKind.Vmix, 9)));
    var session = new OperatorSession(core);
    var atem = new MixerState("Verbunden", [new(1, "Cam 1", 1), new(2, "Cam 2", 3)]);
    var vmix = new MixerState("Verbunden", [new(2, "Cam 2", 1), new(9, "Andere Kamera", 3)]);
    session.SetControl(first.Id, new(ManualAtem: true, Atem: 3, Beamer: true, TextMode: 1, ShowLive: true));
    session.Update(atem, vmix, 0);
    var view = core.Statuses().Single(pager => pager.Id == first.Id).View;
    Check(view is { Inside: 1, Outside: 3, Beamer: true, TextMode: 1 } &&
        view.InsideLive.Contains("Andere Kamera") && view.OutsideLive == "",
        "The other input must be hidden only on the layer where the pager itself is live");
    session.SetAllManual(true);
    session.SetControl(first.Id, session.Control(first.Id) with { ManualVmix = false });
    session.Update(MixerState.Off, MixerState.Off, 1);
    Check(core.Statuses().Single(pager => pager.Id == first.Id).View is { Inside: 0, Outside: 3 } &&
        session.Control(secondId) is { ManualAtem: true, ManualVmix: true },
        "Global manual control and per-source exceptions must survive mixer failure");
    session.Start([first.Id], new(Scenario.Manual, 1000, 2, 3, 0));
    session.Update(atem, vmix, 2);
    Check(core.Statuses().Single(pager => pager.Id == first.Id).View is
        { Inside: 3, Outside: 2, Test: true, Beamer: true, InsideLive: "", OutsideLive: "ATEM: Simulation" },
        "Simulation must override manual tally and retain presentation controls");
    session.Stop([first.Id]);
    session.SetControl(first.Id, new(ShowLive: true, ShowOwnTally: false));
    session.Update(atem, vmix, 3);
    view = core.Statuses().Single(pager => pager.Id == first.Id).View;
    Check(view is { Inside: 1, Outside: 1, ShowOwnTally: false } && view.OutsideLive.Contains("Cam 2"),
        "Returning to external sources must restore real source state and the other live input");
    session.Update(atem, new("Verbunden", [new(2, "Cam 2", 3), new(9, "Andere Kamera", 3)]), 4);
    view = core.Statuses().Single(pager => pager.Id == first.Id).View;
    Check(view.InsideLive == "" && view.OutsideLive.Contains("Cam 2"),
        "An external live tally must hide the other inputs even if several inputs are live");
    session.Reset();
    Check(session.Control(first.Id) == new PagerControl(), "Profile reset must clear temporary manual states and beamer");
    // Restore the fixture for subsequent one-device tests.
    File.WriteAllText(configurationPath, original);
    Console.WriteLine("PASS per-source manual override, global switch, layer swapping, beamer, live names and simulation priority");
}

static void TestPresentationProtocol(string settings)
{
    using var core = new PilotCore(settings, 0);
    using var stick = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    string key = core.Devices.Single(pager => pager.Id == id).Key;
    string session = Connect(stick, core, key, new string('f', 32));
    string firmware = $"TP1|firmware|{id}|{session}|{PagerUsb.Version}|presentation";
    Send(stick, core, firmware, Tag(firmware, key));
    core.Tick();
    core.SetPreview(id, new(3, 1, "Chat", "", TextMode: 1, Beamer: true,
        InsideLive: "ATEM: 4 Cam 4", OutsideLive: "vMix: 2 Kamera"));
    string[] frame;
    do { frame = Read(stick, core, key); } while (frame[1] != "presentation");
    Check(frame[5] == "1" && frame[6] == "1" && Encoding.UTF8.GetString(Convert.FromHexString(frame[7])) == "ATEM: 4 Cam 4",
        "Presentation must transport text selection, beamer and live source text on the signed session");
    string receipt = $"TP1|seen|{id}|{session}|{frame[4]}|80";
    Send(stick, core, receipt, Tag(receipt, key));
    core.Tick();
    Check(!core.Statuses().Single(pager => pager.Id == id).PreviewReceived, "State receipt alone cannot confirm the new display");
    receipt = $"TP1|presented|{id}|{session}|{frame[4]}";
    Send(stick, core, receipt, Tag(receipt, key));
    core.Tick();
    Check(core.Statuses().Single(pager => pager.Id == id).PreviewReceived, "Display receipt must confirm the matching presentation");
    Check(frame.Length == 9, "Older presentation firmware must receive its original packet");
    firmware += ",tally-labels";
    Send(stick, core, firmware, Tag(firmware, key));
    core.Tick();
    core.SetPreview(id, new(3, 1, "Chat", "", ShowOwnTally: false));
    do { frame = Read(stick, core, key); } while (frame[1] != "presentation" || frame.Length != 10);
    Check(frame[9] == "0", "The new M5 firmware must receive the switch to hide its own tally labels");
    core.SetPreview(id, Preview.Clear);
    do { frame = Read(stick, core, key); } while (frame[1] != "presentation" || frame.Length != 10 || frame[9] != "1");
    Console.WriteLine("PASS authenticated presentation feature negotiation and separate display confirmation");
}

static async Task TestDisplayPower(string settings)
{
    using var core = new PilotCore(settings, 0);
    using var stick = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    string key = core.Devices.Single(pager => pager.Id == id).Key;
    string session = Connect(stick, core, key, new string('e', 32));
    void Receive(string body) => Send(stick, core, body, Tag(body, key));
    Receive($"TP1|seen|{id}|{session}|1|80");
    Receive($"TP1|firmware|{id}|{session}|0.10.1|presentation,tally-labels");
    core.Tick();
    try
    {
        _ = core.RequestDisplayPower(id);
        throw new Exception("The Core offered display power control to old firmware");
    }
    catch (InvalidOperationException) { }
    Receive($"TP1|firmware|{id}|{session}|{PagerUsb.Version}|display-power");
    core.Tick();

    string[] ReadCommand()
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.ElapsedMilliseconds < 1500)
        {
            var frame = Read(stick, core, key);
            if (frame[1] == "display_cfg") return frame;
        }
        throw new TimeoutException("The Core did not send a display command");
    }

    var query = core.RequestDisplayPower(id);
    var command = ReadCommand();
    Check(command.SequenceEqual(new[] { "TP1", "display_cfg", id, session, "1" }), "Invalid display read command");
    var retry = ReadCommand();
    Check(retry.SequenceEqual(command), "A lost reply must retry the same display command counter");
    string reply = $"TP1|display_settings|{id}|{session}|1|63|0|ok";
    Send(stick, core, reply, new string('0', 64));
    core.Tick();
    Check(!query.IsCompleted, "Unsigned display replies must not complete the request");
    Receive(reply);
    core.Tick();
    Check(await query == new DisplayPowerSettings(63, false, false), "Unexpected display settings readback");

    var change = new DisplayPowerSettings(40, true, true);
    var save = core.RequestDisplayPower(id, change);
    Check(ReadCommand().SequenceEqual(new[] { "TP1", "display_cfg", id, session, "2", "40", "3" }),
        "Display update must include the brightness and both flags");
    Receive(reply);
    Receive($"TP1|display_settings|{id}|{session}|2|9|3|ok");
    core.Tick();
    Check(!save.IsCompleted, "Stale or malformed replies must not confirm a display update");
    Receive($"TP1|display_settings|{id}|{session}|2|40|3|ok");
    core.Tick();
    Check(await save == change, "The stick must confirm the actual saved brightness and flags");
    Console.WriteLine("PASS per-pager display settings, firmware gating, signed readback and idempotent retries");
}

static void TestBasePreview(string settings)
{
    var baseStatus = PagerUsb.ParseStatus($$"""TP {"version":"{{PagerUsb.Version}}","id":"10061c286f4c","board":"esp8266","baseRole":true,"mode":"espnow","radioAddress":"10061c286f4c","channel":6,"radioReady":true,"features":["radio-base","espnow"]}""");
    Check(baseStatus is { Board: "esp8266", Firmware: { } firmware } && RadioBridge.CanConnect(baseStatus) &&
        firmware.AllowsBase("espnow") && !firmware.AllowsBase("ble") && !firmware.AllowsBase("espnow-lr"),
        "ESP8266 relay must advertise only supported radio capabilities");
    using var core = new PilotCore(settings, 0);
    var pager = core.Statuses().First() with
    {
        Transport = "espnow", Connection = Connection.Connected,
        View = new(3, 0, new string('ü', 160), "", Test: true, OutsideSource: "off", TextMode: 1, Beamer: true)
    };
    var entries = Enumerable.Range(0, 5).Select(index => pager with { Name = $"{index} Müller" }).Append(
        pager with { Name = "Other transport", Transport = "ble" }).ToArray();
    string line = RadioBridge.BaseView(entries, "espnow", true);
    Check(line.Length < 1024 && Encoding.UTF8.GetByteCount(line) < 1024, "Base preview must fit the UART command limit");
    using var json = JsonDocument.Parse(line[12..]);
    var first = json.RootElement[0];
    Check(json.RootElement.GetArrayLength() == 4 && first.GetProperty("text").GetString() == "LIVE" &&
        first.GetProperty("badge").GetString() == "TEST + BEAMER" && !first.GetProperty("outsideOn").GetBoolean(),
        "Mini preview must show the matching radio, fallback layer, simulation and beamer");
    using var legacy = JsonDocument.Parse(RadioBridge.BaseView(entries, "espnow", false)[12..]);
    Check(!legacy.RootElement[0].TryGetProperty("text", out _), "Older base firmware must receive the original view");
    Console.WriteLine("PASS headless board capabilities and detailed/legacy M5 base preview");
}

static void TestRadioBridge(string settings)
{
    const string address = "10061c286f4c";
    Check(RadioBridge.Parse("TP LINK " + address + " TP1|hello") is { } parsed && parsed.Address == address &&
        RadioBridge.Parse("TP LINK ffffffffffff TP1|hello") is null, "Serial bridge framing or peer filter failed");
    foreach (string mode in new[] { "espnow", "espnow-lr", "ble" })
    {
        using var core = new PilotCore(settings, port: 0);
        string key = core.Devices.Single().Key;
        var bridge = new FakeRadio(mode);
        core.AttachRadio(bridge);
        const string nonce = "abcdefabcdefabcdefabcdefabcdefaa";
        void Push(string body, string sender = address) => bridge.Inbound.Enqueue(new(sender, body + "|" + Tag(body, key)));
        Push($"TP1|hello|{id}|{nonce}");
        core.Tick();
        var offer = bridge.Outbound.First().Payload.Split('|');
        string session = offer[4];
        string ready = $"TP1|ready|{id}|{nonce}|{session}";
        Push(ready, "10061c286f40");
        core.Tick();
        Check(bridge.Outbound.All(p => !p.Payload.Contains("|state|")), "Different radio peer completed handshake");
        Push(ready);
        core.Tick();
        var state = bridge.Outbound.Last().Payload.Split('|');
        Push($"TP1|link|{id}|{session}|{state[4]}|88|-128|18|4|1");
        core.Tick();
        Check(bridge.Outbound.All(p => p.Address == address) && core.Statuses().Single() is
            { Connection: Connection.Connected, Battery: 88, Rssi: null, Rtt: 18, Firmware.ExactVersion: false } status && status.Transport == mode,
            "Wireless relay must preserve signed tally, receipts and per-mode status");
        Push($"TP1|firmware|{id}|{session}|0.6.1|chat-ack,usb-config,clock");
        core.Tick();
        Check(core.Statuses().Single() is { Firmware.VersionText: "0.6.1", Firmware.Advertised: true, CanAck: true },
            "New radio firmware must be able to report its exact version independently of the bridge");
        var clockTimeout = Stopwatch.StartNew();
        while (bridge.Outbound.All(frame => !frame.Payload.StartsWith($"TP1|clock|{id}|{session}|")) &&
            clockTimeout.ElapsedMilliseconds < 700)
        {
            core.Tick();
            Thread.Sleep(15);
        }
        Check(bridge.Outbound.Any(frame => frame.Payload.StartsWith($"TP1|clock|{id}|{session}|")),
            "Clock must use the authenticated active radio session");
        Push($"TP1|firmware|{id}|{session}|{PagerUsb.Version}|display-power");
        core.Tick();
        var displayChange = core.RequestDisplayPower(id, new(50, true, false));
        string command = $"TP1|display_cfg|{id}|{session}|1|50|1";
        Check(bridge.Outbound.Any(frame => frame.Address == address && frame.Payload == command + "|" + Tag(command, key)),
            "Display update must follow the stick's radio link");
        Push($"TP1|display_settings|{id}|{session}|1|50|1|ok");
        core.Tick();
        Check(displayChange.IsCompletedSuccessfully && displayChange.Result == new DisplayPowerSettings(50, true, false),
            "Display update must confirm through the same radio bridge");
        core.AttachRadio(null);
        Check(bridge.Closed && !core.Statuses().Single().Online, "Detach must invalidate the radio session");
    }
    Console.WriteLine("PASS ESP-NOW, ESP-NOW LR and BLE bridge routing, signed sessions and peer isolation (simulated serial)");
}

static async Task TestRemoteSlots(string settings)
{
    using var core = new PilotCore(settings, 0);
    using var stick = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    string key = core.Devices.Single().Key;
    string wlan = Connect(stick, core, key, new string('e', 32));
    void Udp(string body) => Send(stick, core, body, Tag(body, key));
    Udp($"TP1|link|{id}|{wlan}|1|90|-42|4|20|1");
    Udp($"TP1|firmware|{id}|{wlan}|{PagerUsb.Version}|remote-slots");
    Udp($"TP1|slots|{id}|{wlan}|0|0,udp;1,ble;2,espnow,10061c286f4c,6;3,future-mode");
    core.Tick();
    var status = core.Statuses().Single();
    Check(status is { Online: true, Address: "127.0.0.1", ActiveSlot: 0, Slots.Length: 3 },
        "Authenticated IP and supported profiles must appear in the status");

    DeviceSlot target = status.Slots!.Single(slot => slot.Mode == "ble");
    Task<string> pending = core.SwitchSlot(id, target);
    string[] command;
    do { command = Read(stick, core, key); } while (command[1] != "switch_slot");
    Check(command[3] == wlan && command[5] == "1", "Switch must be scoped to the old session and chosen slot");
    Udp($"TP1|slot_result|{id}|{wlan}|{command[4]}|testing");
    Udp($"TP1|slot_result|{id}|{wlan}|{command[4]}|applied");
    core.Tick();
    Check(!pending.IsCompleted, "The source session cannot confirm a successful switch");

    const string mac = "10061c286f4c", nonce = "0123456789abcdef0123456789abcdef";
    var native = new FakeRadio("ble");
    core.AttachBluetooth(native);
    void Ble(string body) => native.Inbound.Enqueue(new(mac, body + "|" + Tag(body, key)));
    Ble($"TP1|hello|{id}|{nonce}");
    core.Tick();
    string radio = native.Outbound.First(frame => frame.Payload.Contains("|offer|")).Payload.Split('|')[4];
    Ble($"TP1|ready|{id}|{nonce}|{radio}");
    core.Tick();
    Check(core.Statuses().Single().ActiveSlot is null, "Profiles must be refreshed in the new session");
    Ble($"TP1|firmware|{id}|{radio}|{PagerUsb.Version}|remote-slots");
    Ble($"TP1|slots|{id}|{radio}|1|0,udp;1,ble");
    Ble($"TP1|slot_result|{id}|{radio}|{command[4]}|applied");
    Ble($"TP1|link|{id}|{radio}|1|90|-128|4|20|1");
    core.Tick();
    Check(await pending.WaitAsync(TimeSpan.FromSeconds(2)) == "applied" &&
        core.Statuses().Single() is { Transport: "ble", Online: true, Address: "10:06:1C:28:6F:4C", ActiveSlot: 1 },
        "A signed result on the new BLE session must confirm the switch, address and target profile");
    core.AttachRadio(new FakeRadio("espnow"));
    Check(core.Statuses().Single().Online, "Attaching a USB bridge must preserve active native BLE links");
    var rolledBack = core.SwitchSlot(id, new DeviceSlot(0, "udp"));
    string switchId = native.Outbound.Last(frame => frame.Payload.Contains("|switch_slot|")).Payload.Split('|')[4];
    Ble($"TP1|slot_result|{id}|{radio}|{switchId}|rollback");
    core.Tick();
    Check(await rolledBack.WaitAsync(TimeSpan.FromSeconds(2)) == "rollback",
        "The Core must report a rollback without switching its device status");
    core.AttachBluetooth(null);
    Check(core.Statuses().Single() is { Online: false, LastKnownAddress: true }, "Detached stick retains its last address");
    Console.WriteLine("PASS Remote profiles, session-bound migration, rollback and parallel BLE/USB transport");
}

static void TestRadioFragments()
{
    string payload = "TP1|" + new string('a', 896);
    byte[][] frames = RadioFragments.Encode(payload, 0xabcd).ToArray();
    Check(frames.Length == 5 && frames[0].Take(8).SequenceEqual(new byte[] { 0x54, 0x50, 0x52, 0x31, 0xcd, 0xab, 0, 5 }),
        "Windows must use the same little-endian fragment layout as the ESP32");
    var radio = new RadioFragments();
    Check(radio.Receive(frames[1], 1) is null && radio.Receive(frames[0], 2) is null &&
        radio.Receive(frames[1], 360) is null, "Unstarted or expired sequences must be ignored");
    for (int i = 0; i < frames.Length - 1; i++)
        Check(radio.Receive(frames[i], 400 + i) is null, "Incomplete sequences must not reach the Core");
    Check(radio.Receive(frames[^1], 405) == payload && radio.Receive(frames[^1], 406) is null,
        "Five fragments must reassemble exactly once");
    Console.WriteLine("PASS BLE packet layout, fragmentation and incomplete sequences");
}

static async Task TestWifiManagement(string settings)
{
    using var core = new PilotCore(settings, port: 0);
    using var stick = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    string key = core.Devices.Single().Key;
    string session = Connect(stick, core, key, new string('e', 32));
    void SendReport(string body)
    {
        Send(stick, core, body, Tag(body, key));
        core.Tick();
    }
    SendReport($"TP1|link|{id}|{session}|1|90|-42|4|20|1");
    SendReport($"TP1|cfg_info|{id}|{session}|0.6.0|192.168.1.2|5061676572|||idle");
    var change = new WifiChange("Bühne", "192.168.1.20", "Guest", "temporary-secret");
    Task<WifiSettings> request = core.ChangeWifi(id, change);
    string[] command;
    do { command = Read(stick, core, key); } while (command[1] != "cfg");
    Check(command.Length == 9 && command[3] == session, "Config command must bind to the active UDP session");
    byte[] k = HMACSHA256.HashData(Convert.FromHexString(key), "TallyPager WLAN config v1"u8);
    byte[] plaintext = new byte[command[7].Length / 2];
    using (var aes = new AesGcm(k, 16))
        aes.Decrypt(Convert.FromHexString(command[6]), Convert.FromHexString(command[7]), Convert.FromHexString(command[8]),
            plaintext, Encoding.ASCII.GetBytes(string.Join("|", command[2..6])));
    Check(JsonSerializer.Deserialize<WifiChange>(plaintext, ProfileStore.Json) == change &&
        !string.Join("|", command).Contains(change.Password), "Config payload must match the authenticated encryption contract");
    string requestId = command[4];
    SendReport($"TP1|cfg_info|{id}|{session}|0.6.0|192.168.1.2|5061676572||{requestId}|testing");
    Check(!request.IsCompleted, "Testing must not report success");
    SendReport($"TP1|cfg_info|{id}|{new string('f', 32)}|0.6.0|192.168.1.2|5061676572||{requestId}|applied");
    Check(!request.IsCompleted, "Obsolete session must not complete a pending config");
    SendReport($"TP1|cfg_info|{id}|{session}|0.6.0|192.168.1.2|5061676572||{requestId}|rollback");
    Check((await request.WaitAsync(TimeSpan.FromSeconds(1))).Result == "rollback", "Rollback was not reported");
    Console.WriteLine("PASS authenticated Wi-Fi settings, AES-GCM encoding, trial and rollback delivery");
}

static async Task TestWifiDiscovery(string settings)
{
    using var core = new PilotCore(settings, port: 0);
    using var device = new UdpClient(new IPEndPoint(IPAddress.Loopback, PilotCore.ManagementPort));
    string key = core.Devices.Single().Key;
    core.DiscoverWifi(IPAddress.Loopback);
    var challenge = Read(device, core, key);
    Check(challenge is [_, "discover", id, _], "Expected a signed discovery request from the paired PC");
    const string managementSession = "bafebafebafebafebafebafebafebafe";
    void Response(string body)
    {
        Send(device, core, body, Tag(body, key));
        core.Tick();
    }
    Response($"TP1|found|{id}|{new string('f', 32)}|{managementSession}|0.6.0|192.168.3.2|5061676572|||idle");
    Check(core.Statuses().Single().Settings is null, "Unrequested discovery must not expose remote settings");
    Response($"TP1|found|{id}|{challenge[3]}|{managementSession}|0.6.0|192.168.3.2|5061676572|||idle");
    Check(core.Statuses().Single() is { Online: false, Settings.Host: "192.168.3.2" },
        "The management interface must be available even without an active tally session");
    Task<WifiSettings> change = core.ChangeWifi(id, new("Pager", "192.168.3.4", "", ""));
    var wire = Read(device, core, key);
    Check(wire[1] == "cfg" && wire[3] == managementSession, "Config command should use the authenticated management session");
    Response($"TP1|cfg_info|{id}|{managementSession}|0.6.0|192.168.3.4|5061676572||{wire[4]}|applied");
    Check((await change.WaitAsync(TimeSpan.FromSeconds(1))).Result == "applied", "Management response was ignored");
    Console.WriteLine("PASS signed WLAN discovery and configuration without an active tally session");
}

static void TestAcknowledgements(string settings)
{
    using var core = new PilotCore(settings, port: 0);
    string key = core.Devices.Single().Key;
    using var stick = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    using var reconnectedStick = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    string firstSession = Connect(stick, core, key, new string('c', 32));
    var operatorSession = new OperatorSession(core);
    void Update() => operatorSession.Update(MixerState.Off, MixerState.Off, Environment.TickCount64);
    void Report(UdpClient sender, string body)
    {
        Send(sender, core, body, Tag(body, key));
        core.Tick();
    }
    operatorSession.SetText([id], "Position halten", "Taufe", ack: true);
    Update();
    string chatId = operatorSession.Message(id)!.Id;
    var packet = Read(stick, core, key);
    Check(packet.Length == 14 && packet[12] == chatId && packet[13] == "1", "ACK flag and chat ID missing");
    Report(stick, $"TP1|caps|{id}|{firstSession}|ack");
    Report(stick, $"TP1|chat_rcpt|{id}|{firstSession}|{chatId}|shown");
    Check(core.Statuses().Single() is { CanAck: true, ReceivedAt: not null, AcknowledgedAt: null }, "Shown must not ACK");

    string secondSession = Connect(reconnectedStick, core, key, new string('d', 32));
    string oldAck = $"TP1|chat_rcpt|{id}|{firstSession}|{chatId}|ack";
    Report(stick, oldAck);
    Check(core.Statuses().Single().AcknowledgedAt is null, "Old endpoint/session must not ACK");
    string ack = $"TP1|chat_rcpt|{id}|{secondSession}|{chatId}|ack";
    Report(reconnectedStick, ack);
    Check(Read(reconnectedStick, core, key) is [_, "chat_ok", id, _, var received] && received == chatId,
        "ACK must be confirmed after reconnect");
    var original = core.Statuses().Single().AcknowledgedAt;
    Check(original is not null, "ACK must be recorded");
    Report(reconnectedStick, ack);
    Check(core.Statuses().Single().AcknowledgedAt == original, "ACK retransmission must be idempotent");
    operatorSession.SetText([id], "Position halten", null, ack: true, ttl: 5);
    Update();
    Check(operatorSession.Message(id)!.Id != chatId && operatorSession.Text(id).Next == "Taufe", "Resend needs fresh ID and same Next");
    Report(reconnectedStick, ack);
    Check(core.Statuses().Single().AcknowledgedAt is null, "A late ACK must not confirm another message");
    operatorSession.Update(MixerState.Off, MixerState.Off, Environment.TickCount64 + 6000);
    Check(operatorSession.Text(id) == ("", "Taufe") && operatorSession.History.Select(entry => entry.Result)
        .SequenceEqual(new[] { "Quittiert", "Abgelaufen" }), "Expiry must clear only Chat and retain closed outcomes");
    Report(reconnectedStick, $"TP1|diag|{id}|{secondSession}|1|1|201|1513|2500|7|2100");
    Check(core.Diagnostics.Entries.Length == 2, "WLAN and state timeouts must be logged separately");
    Console.WriteLine("PASS authenticated chat receipts, reconnect, duplicate and late ACKs, expiry and diagnostics");
}

static void TestProfiles(string settings)
{
    using var core = new PilotCore(settings, port: 0);
    string address = Path.Combine(settings, "arbiter-address.txt");
    string export = Path.Combine(settings, "export.json");
    File.WriteAllText(address, "localhost:4455");
    var store = new ProfileStore(settings, core.Devices);
    var active = store.Active;
    var role = active.Roles.First(r => store.Bindings[r.Id] == id);
    Check(active.Sources.Arbiter == "localhost:4455" && role.Inside?.Mixer == MixerKind.Off &&
        role.Outside?.Mixer == MixerKind.Atem, "Legacy source, pairing and layer assignments must migrate");
    var group = new TargetGroup(Guid.NewGuid(), "Kameras", [.. active.Roles.Select(r => r.Id)]);
    var text = new QuickText(Guid.NewGuid(), "Position", "Position halten", "Taufe", [], group.Id, true, 120);
    const string webAddress = "http://127.0.0.1:8088";
    store.Update(active with { Sources = active.Sources with { Vmix = webAddress }, Groups = [group], Texts = [text] });
    Check(store.Targets(text, core.Devices.Select(p => p.Id)).Devices.Length == 2, "Group must resolve both sticks");
    var operatorSession = new OperatorSession(core, store);
    operatorSession.SetText([id], "Laufende Anweisung", null);
    store.Export(export, true);
    string json = File.ReadAllText(export);
    Check(!json.Contains(id) && !json.Contains(core.Devices[0].Key) && !json.Contains("Laufende Anweisung") &&
        !File.ReadAllText(Path.Combine(settings, "devices.json")).Contains(core.Devices[0].Key), "Export or local storage leaked device secrets");
    var incoming = ProfileStore.PreviewImport(export);
    store.Import(incoming, false);
    var copy = store.Backup.Profiles.Single(p => p.Id != active.Id);
    Check(copy.Texts.Single().Id != text.Id && copy.Texts.Single().Group == copy.Groups.Single().Id &&
        copy.Groups.Single().Roles.All(roleId => copy.Roles.Any(r => r.Id == roleId)) &&
        store.Unbound(copy).Count() == 2, "Imported copy needs fresh IDs and explicit bindings");
    store.Activate(copy.Id);
    operatorSession.Reset();
    operatorSession.Update(MixerState.Off, MixerState.Off, Environment.TickCount64);
    Check(core.Statuses().All(p => p.View.InsideSource == "off" && p.View.Chat.Length == 0),
        "Switch must clear messages and hide unbound roles");
    var original = store.Backup;
    try { store.Import(incoming with { Version = 999 }, true); throw new Exception("Invalid import accepted"); }
    catch (InvalidDataException) { Check(store.Backup == original, "Invalid import must preserve local state"); }
    store.Import(incoming, true);
    Check(File.Exists(Path.Combine(settings, "profiles-before-import.json")) && store.Active.Id == active.Id &&
        store.Active.Texts.Single().Id == text.Id && store.RoleFor(id)?.Id == role.Id, "Replace must preserve IDs and local bindings");
    var reloaded = new ProfileStore(settings, core.Devices);
    Check(reloaded.Active.Texts.Single().Id == text.Id && reloaded.Active.Sources.Vmix == webAddress,
        "Restore, including a vMix web address, must survive restart");
    Console.WriteLine("PASS profile migration, DPAPI storage, export, group resolution, copy, invalid import, restore and activation");
}

static async Task TestCompanion()
{
    int port;
    using (var reserve = new TcpListener(IPAddress.Loopback, 0))
    {
        reserve.Start();
        port = ((IPEndPoint)reserve.LocalEndpoint).Port;
    }
    int sends = 0;
    string token = PilotCore.RandomHex(32);
    var api = new CompanionApi(token, (command, _) => Task.FromResult(command.Action switch
    {
        "status" => new ApiReply(200, new { sends }),
        "send" => new ApiReply(202, new { queued = Interlocked.Increment(ref sends) }),
        _ => new ApiReply(202, new { Chat = command.Body!.Chat })
    }), port);
    try
    {
        await api.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(5) };
        Check((await client.GetAsync("/api/v1/status")).StatusCode == HttpStatusCode.Unauthorized, "API must require a token");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Check((await client.GetAsync("/api/v1/status")).IsSuccessStatusCode, "Authenticated status failed");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "simulate-retry");
        string path = $"/api/v1/quick-texts/{Guid.NewGuid():D}/send";
        Check((await client.PostAsync(path, null)).StatusCode == HttpStatusCode.Accepted &&
            (await client.PostAsync(path, null)).StatusCode == HttpStatusCode.Accepted && sends == 1, "Retry must queue only once");
        Check((await client.PostAsync($"/api/v1/quick-texts/{Guid.NewGuid():D}/send", null)).StatusCode == HttpStatusCode.Conflict,
            "Idempotency key must be bound to a command");
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        string clear = JsonSerializer.Serialize(new ApiRequest(Guid.NewGuid(), [Guid.NewGuid()], true, false), ProfileStore.Json);
        Check((await client.PostAsync("/api/v1/messages/clear", new StringContent(clear, Encoding.UTF8, "application/json")))
            .StatusCode == HttpStatusCode.Accepted, "Clear contract failed");
        Check((await client.PostAsync("/api/v1/messages/clear", new StringContent("{}", Encoding.UTF8, "application/json")))
            .StatusCode == HttpStatusCode.BadRequest, "Invalid clear targets must be rejected");
        Console.WriteLine("PASS local Companion HTTP start, bearer auth, status, idempotent send and targeted clear");
    }
    finally { await api.CloseAsync(); }
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

static async Task TestVmixWithoutSubscription()
{
    using var end = new CancellationTokenSource(TimeSpan.FromSeconds(9));
    var token = end.Token;
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    using var source = new VmixSource("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
    var server = Task.Run(async () =>
    {
        using (var subscription = await listener.AcceptTcpClientAsync(token))
        using (var reader = new StreamReader(subscription.GetStream(), Encoding.ASCII, leaveOpen: true))
        {
            Check(await reader.ReadLineAsync(token) == "SUBSCRIBE TALLY", "Expected the normal subscribe attempt");
            Check(await reader.ReadLineAsync(token) is null, "Subscription socket should be closed before fallback");
        }
        using var poll = await listener.AcceptTcpClientAsync(token);
        using var stream = poll.GetStream();
        using var commands = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        while (await commands.ReadLineAsync(token) is string command)
        {
            string xml = "<vmix><inputs><input number=\"1\" key=\"one\" title=\"Kamera\"/></inputs></vmix>\r\n";
            string reply = command switch
            {
                "XML" => $"XML {Encoding.UTF8.GetByteCount(xml)} Eingänge\r\n{xml}",
                "TALLY" => "TALLY OK 2\r\n",
                _ => throw new InvalidOperationException("Fallback must use read-only TALLY and XML")
            };
            await stream.WriteAsync(Encoding.UTF8.GetBytes(reply), token);
        }
    }, token);
    while (source.State is not { Status: "Verbunden · TCP-Abfrage", Inputs: [{ Tally: 2, Key: "one" }] })
        await Task.Delay(20, token);
    source.Dispose();
    try { await server; }
    catch (IOException) { }
    Console.WriteLine("PASS vMix ignores SUBSCRIBE but TCP read-only polling reports Preview and preserves the input key");
}

static async Task TestVmixWeb()
{
    using var end = new CancellationTokenSource(TimeSpan.FromSeconds(7));
    var token = end.Token;
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    string address = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
    Check(VmixSource.ValidAddress(address) && !VmixSource.ValidAddress(address + "/api/?Function=Cut") &&
        !VmixSource.ValidAddress(address.Replace("http://", "http://user:secret@", StringComparison.Ordinal)),
        "The web endpoint must accept only a read-only API URL without embedded credentials");
    using var source = new VmixSource(address);
    async Task Reply(string status, string payload)
    {
        using var client = await listener.AcceptTcpClientAsync(token);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        Check(await reader.ReadLineAsync(token) == "GET /api/ HTTP/1.1", "The Web adapter must only read the XML endpoint");
        while (await reader.ReadLineAsync(token) is { Length: > 0 }) { }
        byte[] body = Encoding.UTF8.GetBytes(payload);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), token);
        await stream.WriteAsync(body, token);
    }
    const string inputs = "<vmix><inputs><input number=\"1\" key=\"a\" title=\"Bühne\"/>" +
        "<input number=\"2\" key=\"b\" title=\"Kamera\"/><input number=\"3\" key=\"c\" title=\"Logo\"/></inputs>";
    await Reply("200 OK", inputs + "<active>1</active><preview>2</preview><overlays><overlay>3</overlay></overlays></vmix>");
    while (source.State.Inputs is not [{ Tally: 3, Name: "Bühne", Key: "a" }, { Tally: 2 }, { Tally: 3 }])
        await Task.Delay(20, token);
    await Reply("200 OK", inputs + "<active>2</active><preview>1</preview></vmix>");
    while (source.State.Inputs is not [{ Tally: 2 }, { Tally: 3 }, { Tally: 1 }]) await Task.Delay(20, token);
    await Reply("403 Forbidden", "");
    while (!source.State.Status.Contains("verweigert", StringComparison.Ordinal)) await Task.Delay(20, token);
    Check(source.State.Inputs.Length == 0, "A denied request must clear stale Tally");
    Console.WriteLine("PASS vMix HTTP Web API reads Program, Preview and overlays; subsequent responses and denied access update state");
}

static async Task TestVmixFailure()
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var server = new TcpListener(IPAddress.Loopback, 0);
    server.Start();
    using var source = new VmixSource("127.0.0.1", ((IPEndPoint)server.LocalEndpoint).Port);
    using var silent = await server.AcceptTcpClientAsync(timeout.Token);
    using var silentPoll = await server.AcceptTcpClientAsync(timeout.Token);
    while (!source.State.Status.Contains("Keine gültige Antwort auf XML", StringComparison.Ordinal))
        await Task.Delay(20, timeout.Token);
    Check(source.State.Status.Contains("TCP verbunden", StringComparison.Ordinal) && source.State.Inputs.Length == 0,
        "A connected TCP socket without vMix replies must stay gray and show the failed protocol stage");
    Console.WriteLine("PASS vMix keeps a silent TCP service gray after both subscription and polling fail");
}

file sealed class FakeRadio(string mode) : IRadioBridge
{
    public string Mode { get; } = mode;
    public Queue<RadioPacket> Inbound { get; } = new();
    public List<RadioPacket> Outbound { get; } = [];
    public bool Closed { get; private set; }
    public bool TryRead(out RadioPacket packet) => Inbound.TryDequeue(out packet);
    public bool Send(string address, string payload) { Outbound.Add(new(address, payload)); return true; }
    public void Dispose() => Closed = true;
}

file sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(respond(request));
}
