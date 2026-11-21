using System.Diagnostics;
using System.IO.Ports;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TallyPager;

internal sealed record UsbStatus(string Version, string Id, bool Configured, bool Wifi, string Ip,
    bool Core, int Inside, int Outside, string Cue, string Next, string Mode = "udp",
    bool BaseRole = false, string RadioAddress = "", int Channel = 6, bool RadioReady = false,
    string[]? Features = null, int Slot = 0, string Board = "m5stick-cplus2",
    string HotspotSsid = "", string HotspotPassword = "")
{
    public FirmwareInfo? Firmware => FirmwareInfo.Read(Version, Features);
    public string BoardName => Board switch
    {
        "m5stick-cplus2" => Mode == Transport.Hotspot ? "M5StickC Plus2 · WLAN-Brücke" : "M5StickC Plus2",
        "esp8266" => "ESP8266MOD · Funkbasis ohne Display",
        _ => $"Anderes Gerät ({Board})"
    };
}
internal sealed record UsbSettings(string Id, string Name, string Host, string Ssid, string Password,
    string Mode, string Gateway, int Channel, int Slot);
internal enum FirmwareTarget { Pager, Esp8266, Hotspot }
internal sealed record UsbHardware(FirmwareTarget? Target, string Description);

internal static class PagerUsb
{
    public const string Version = "0.13.0";
    public const string Esp8266FirmwareFileName = "TallyPager-Firmware-ESP8266-" + Version + ".zip";
    public const string HotspotFirmwareFileName = "TallyPager-Firmware-WLAN-Bruecke-M5StickC-Plus2-" + Version + ".zip";
    private const int StandardBaud = 115200, HotspotBaud = 460800;
    private const string StatusPrefix = "TP ";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static UsbStatus ReadStatus(string port, TimeSpan timeout)
    {
        using var serial = Open(port);
        return ReadStatus(serial, timeout);
    }

    internal static UsbStatus ReadStatus(SerialPort serial, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            serial.WriteLine("\nTP STATUS");
            var reply = ReadReply(serial, TimeSpan.FromMilliseconds(420), "TP {");
            if (reply?.StartsWith("TP {", StringComparison.Ordinal) == true)
            {
                if (ParseStatus(reply) is { } status) return status;
            }
            Thread.Sleep(120);
            serial.BaudRate = serial.BaudRate == StandardBaud ? HotspotBaud : StandardBaud;
            serial.DiscardInBuffer();
        }
        throw new TimeoutException("Die TallyPager-Firmware antwortet nicht am ausgewählten USB-Port.");
    }

    internal static UsbStatus? ParseStatus(string line)
    {
        if (line.Length > 8192 || !line.StartsWith("TP {", StringComparison.Ordinal)) return null;
        try
        {
            var status = JsonSerializer.Deserialize<UsbStatus>(line[StatusPrefix.Length..], Json);
            return status is { Id.Length: 12, Firmware: not null } &&
                status.Id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ? status : null;
        }
        catch (JsonException) { return null; }
    }

    public static UsbStatus Configure(string port, Pager pager, string host, string ssid, string password,
        string mode = "udp", string gateway = "", int channel = 6, int? slot = null)
    {
        using (var serial = Open(port))
        {
            var current = ReadStatus(serial, TimeSpan.FromSeconds(4));
            if (current.Id != pager.Id) throw new InvalidOperationException("Am USB-Port antwortet inzwischen ein anderer Stick.");
            serial.WriteLine(ConfigCommand(current, pager, host, ssid, password, mode, gateway, channel, slot));
            if (ReadReply(serial, TimeSpan.FromSeconds(4), "TP OK", "TP ERROR ") != "TP OK")
                throw new InvalidOperationException("Der Stick hat die Einstellungen nicht bestätigt.");
        }
        Thread.Sleep(1400);
        var status = ReadStatus(port, TimeSpan.FromSeconds(8));
        return status is { Configured: true, BaseRole: false } && status.Id == pager.Id && status.Mode == mode &&
            (slot is null || status.Slot == slot) ? status :
            throw new InvalidOperationException("Die Konfiguration konnte nach dem Neustart nicht bestätigt werden.");
    }

    internal static string ConfigCommand(UsbStatus current, Pager pager, string host, string ssid, string password,
        string mode, string gateway, int channel, int? slot = null)
    {
        var firmware = current.Firmware;
        if (firmware?.AllowsUsbMode(mode) != true || current.BaseRole && !firmware.Supports(FirmwareInfo.RadioBase))
            throw new InvalidOperationException($"Firmware {current.Version} unterstützt die gewählte USB-Einrichtung nicht.");
        if (slot is not null && (slot is < 0 or > 3 || !firmware.Supports("device-slots")))
            throw new InvalidOperationException("Firmware unterstützt dieses Geräteprofil nicht.");
        object payload = slot.HasValue ? new { host, pager.Name, ssid, password, pager.Key, mode, gateway, channel, slot } :
            firmware.Supports(FirmwareInfo.RadioBase) || mode != "udp" ?
            new { host, pager.Name, ssid, password, pager.Key, mode, gateway, channel } :
            new { host, pager.Name, ssid, password, pager.Key };
        return "TP CONFIG " + JsonSerializer.Serialize(payload, Json);
    }

    public static UsbSettings ReadSettings(string port)
    {
        using var serial = Open(port);
        var current = ReadStatus(serial, TimeSpan.FromSeconds(4));
        if (current.Firmware?.Supports("usb-read-settings") != true)
            throw new InvalidOperationException("Stick-Einstellungen lassen sich ab Firmware 0.7.0 per USB auslesen.");
        serial.WriteLine("TP READ SETTINGS");
        const string prefix = "TP SETTINGS ";
        var line = ReadReply(serial, TimeSpan.FromSeconds(4), prefix, "TP ERROR read");
        if (line?.StartsWith(prefix, StringComparison.Ordinal) == true && line.Length < 4096)
        {
            var data = JsonSerializer.Deserialize<UsbSettings>(line[prefix.Length..], Json);
            if (data?.Id == current.Id) return data;
        }
        throw new InvalidOperationException("Der Stick hat keine gespeicherten Pager-Einstellungen gemeldet.");
    }

    public static UsbStatus ConfigureBase(string port, string id, string mode, int channel)
    {
        if (Transport.Serves(mode, Transport.Hotspot) && channel is not (1 or 6 or 11))
            throw new ArgumentException("Für die WLAN-Brücke Kanal 1, 6 oder 11 wählen.");
        return SetRole(port, id, status => status.Firmware?.AllowsBase(mode) == true,
            "TP BASE " + JsonSerializer.Serialize(new { mode, channel }, Json),
            status => status.BaseRole && status.RadioReady && status.Mode == mode && status.Channel == channel);
    }

    public static UsbStatus RestorePager(string port, string id) =>
        SetRole(port, id, status => status.Board == "m5stick-cplus2" && status.BaseRole && status.Firmware?.Supports(FirmwareInfo.RadioBase) == true,
            "TP PAGER", status => status.Configured && !status.BaseRole);

    public static UsbStatus ConfigureHotspotChannel(string port, string id, int channel)
    {
        if (channel is not (1 or 6 or 11)) throw new ArgumentOutOfRangeException(nameof(channel));
        return SetRole(port, id, status => status.Mode == Transport.Hotspot && status.Firmware?.AllowsBase(Transport.Hotspot) == true,
            FormattableString.Invariant($"TP AP {channel}"),
            status => status.Mode == Transport.Hotspot && status.RadioReady && status.Channel == channel);
    }

    private static UsbStatus SetRole(string port, string id, Func<UsbStatus, bool> supports, string command, Func<UsbStatus, bool> ready)
    {
        using (var serial = Open(port))
        {
            var current = ReadStatus(serial, TimeSpan.FromSeconds(4));
            if (current.Id != id || !supports(current))
                throw new InvalidOperationException("Der Stick am USB-Port unterstützt diese Rollenänderung nicht.");
            serial.WriteLine(command);
            if (ReadReply(serial, TimeSpan.FromSeconds(4), "TP OK", "TP ERROR ") != "TP OK")
                throw new InvalidOperationException("Der Stick hat die Rolle nicht bestätigt.");
        }
        Thread.Sleep(1400);
        var status = ReadStatus(port, TimeSpan.FromSeconds(8));
        return status.Id == id && ready(status) ? status :
            throw new InvalidOperationException("Die gewünschte Rolle ist am Stick nicht betriebsbereit.");
    }

    internal static readonly (string Offset, string Name, long Limit)[] Images =
        [("0x1000", "bootloader.bin", 0x7000), ("0x8000", "partitions.bin", 0x1000),
         ("0xe000", "boot_app0.bin", 0x2000), ("0x10000", "firmware.bin", 0x330000)];

    private sealed record FirmwareManifest(string Board, string Version, Dictionary<string, string> Sha256);
    private static readonly (string Offset, string Name, long Limit)[] Esp8266Images =
        [("0x0", "firmware.bin", 0xff000)];

    public static async Task Flash(string port, string package, IProgress<string> progress, FirmwareTarget target, Version version)
    {
        bool esp8266 = target == FirmwareTarget.Esp8266;
        string directory = Path.Combine(PilotCore.DataDirectory, "bundle", Version, FirmwareBoard(target));
        Directory.CreateDirectory(directory);
        WriteVerifiedResource(directory, "esptool.exe");
        LoadFirmware(package, directory, version.ToString(), target);
        var start = Flasher(directory);
        string[] common = ["--chip", esp8266 ? "esp8266" : "esp32", "--port", port];
        bool correctChip = false;
        await Run(start, [..common, esp8266 ? "flash-id" : "chip-id"], line =>
        {
            if (line.Contains(esp8266 ? "Detected flash size: 4MB" : "ESP32-PICO-V3-02", StringComparison.Ordinal))
                correctChip = true;
        });
        if (!correctChip)
            throw new InvalidOperationException(esp8266 ? "Am Port wird ein ESP8266 mit 4 MB Flash benötigt." :
                "Der ESP32-Chip passt nicht zum M5StickC Plus2.");
        progress.Report("Firmware wird geschrieben und überprüft …");
        var images = esp8266 ? Esp8266Images : Images;
        await Run(start, [..common, "--baud", esp8266 ? "115200" : "460800", "write-flash", "--flash-mode", "dio",
            "--flash-freq", esp8266 ? "40m" : "80m", "--flash-size", esp8266 ? "4MB" : "8MB",
            .. images.SelectMany(image => new[] { image.Offset, image.Name })], line =>
            {
                if (line.Contains('%') || line.Contains("verified", StringComparison.OrdinalIgnoreCase))
                    progress.Report(line.Trim());
            });
    }

    public static async Task<UsbHardware> IdentifyHardware(string port)
    {
        string directory = Path.Combine(PilotCore.DataDirectory, "bundle", Version, "probe");
        Directory.CreateDirectory(directory);
        WriteVerifiedResource(directory, "esptool.exe");
        string chip = "";
        string capacity = "";
        await Run(Flasher(directory), ["--port", port, "flash-id"], line =>
        {
            if (line.StartsWith("Chip type:", StringComparison.Ordinal)) chip = line["Chip type:".Length..].Trim();
            if (line.StartsWith("Detected flash size:", StringComparison.Ordinal))
                capacity = line["Detected flash size:".Length..].Trim();
        });
        if (chip.StartsWith("ESP8266", StringComparison.Ordinal))
            return capacity == "4MB" ? new(FirmwareTarget.Esp8266, "ESP8266MOD · 4 MB Flash")
                : new(null, $"ESP8266 · {capacity} Flash\nDas ESP8266-Firmwarepaket benötigt 4 MB.");
        if (chip.StartsWith("ESP32-PICO-V3-02", StringComparison.Ordinal))
            return new(FirmwareTarget.Pager, "M5StickC Plus2 · ESP32-PICO-V3-02");
        return new(null, $"Anderes Gerät · {(chip.Length == 0 ? "Chip unbekannt" : chip)}\nFirmwarepakete verfügbar für M5StickC Plus2 und ESP8266 mit 4 MB.");
    }

    private static ProcessStartInfo Flasher(string directory) => new(Path.Combine(directory, "esptool.exe"))
    {
        WorkingDirectory = directory, CreateNoWindow = true, UseShellExecute = false,
        RedirectStandardOutput = true, RedirectStandardError = true
    };

    private static string FirmwareBoard(FirmwareTarget target) => target switch
    {
        FirmwareTarget.Hotspot => "m5stick-cplus2-hotspot",
        FirmwareTarget.Esp8266 => "esp8266",
        _ => "m5stick-cplus2"
    };

    internal static (FirmwareTarget Target, Version Version) InspectFirmware(string path, bool verify = false)
    {
        using var zip = ZipFile.OpenRead(path);
        return ReadFirmware(zip, verify, null);
    }

    internal static void LoadFirmware(string path, string? directory, string expectedVersion = Version, FirmwareTarget target = FirmwareTarget.Pager)
    {
        using var zip = ZipFile.OpenRead(path);
        ReadFirmware(zip, true, directory, target, expectedVersion);
    }

    private static (FirmwareTarget Target, Version Version) ReadFirmware(ZipArchive zip, bool verify, string? directory,
        FirmwareTarget? expectedTarget = null, string? expectedVersion = null)
    {
        var manifestEntry = zip.GetEntry("manifest.json");
        if (manifestEntry is null || manifestEntry.Length is < 1 or > 4096)
            throw new InvalidDataException("Im Firmwarepaket fehlt das Manifest.");
        using var manifestStream = manifestEntry.Open();
        FirmwareManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<FirmwareManifest>(manifestStream, Json); }
        catch (JsonException error) { throw new InvalidDataException("Das Firmware-Manifest ist ungültig.", error); }
        if (manifest?.Sha256 is null || !System.Version.TryParse(manifest.Version, out var version) ||
            version.Build < 0 || version.Revision >= 0 || version.ToString() != manifest.Version)
            throw new InvalidDataException("Das Firmwarepaket hat keine gültige TallyPager-Version.");
        FirmwareTarget target = manifest.Board switch
        {
            "m5stick-cplus2" => FirmwareTarget.Pager,
            "m5stick-cplus2-hotspot" => FirmwareTarget.Hotspot,
            "esp8266" => FirmwareTarget.Esp8266,
            _ => throw new InvalidDataException("Das Firmwarepaket nennt kein unterstütztes Zielgerät.")
        };
        if (expectedTarget is not null && expectedTarget != target || expectedVersion is not null && expectedVersion != manifest.Version)
            throw new InvalidDataException("Firmwarepaket und ausgewählte Firmware stimmen bei Gerät, Rolle oder Version nicht überein.");
        foreach (var image in target == FirmwareTarget.Esp8266 ? Esp8266Images : Images)
        {
            var entry = zip.GetEntry(image.Name);
            if (entry is null || entry.Length < 1 || entry.Length > image.Limit ||
                !manifest.Sha256.TryGetValue(image.Name, out string? expected) || expected is not { Length: 64 } ||
                !expected.All(Uri.IsHexDigit))
                throw new InvalidDataException("Fehlendes oder ungültiges Firmware-Image: " + image.Name);
            if (!verify) continue;
            // Only read the known images. ZIP paths never control the output location.
            using var stream = entry.Open();
            byte[] bytes = new byte[checked((int)entry.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1 ||
                !Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Firmware-Prüfsumme stimmt nicht: " + image.Name);
            if (directory is not null) File.WriteAllBytes(Path.Combine(directory, image.Name), bytes);
        }
        return (target, version);
    }

    private static async Task Run(ProcessStartInfo start, string[] arguments, Action<string> output)
    {
        start.ArgumentList.Clear();
        foreach (string item in arguments) start.ArgumentList.Add(item);
        var messages = new List<string>();
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is { } line)
            {
                lock (messages) messages.Add(line);
                output(line);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is { } line) lock (messages) messages.Add(line);
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("Das Flashwerkzeug antwortet nicht. USB-Verbindung prüfen und erneut versuchen.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Flashwerkzeug meldet einen Fehler.\n" +
                string.Join("\n", messages.TakeLast(6)));
    }

    private static void WriteVerifiedResource(string directory, string file)
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("Bundle." + file) ??
            throw new InvalidOperationException("Im Release fehlt " + file);
        byte[] expected = SHA256.HashData(source);
        source.Position = 0;
        string path = Path.Combine(directory, file);
        if (File.Exists(path))
        {
            using var existing = File.OpenRead(path);
            if (SHA256.HashData(existing).AsSpan().SequenceEqual(expected)) return;
        }
        string temporary = path + ".tmp";
        using (var target = File.Create(temporary)) source.CopyTo(target);
        using (var target = File.OpenRead(temporary))
        {
            if (!SHA256.HashData(target).AsSpan().SequenceEqual(expected))
                throw new IOException("Prüfsumme beim Entpacken fehlgeschlagen: " + file);
        }
        File.Move(temporary, path, overwrite: true);
    }

    internal static SerialPort Open(string port)
    {
        var serial = new SerialPort(port, StandardBaud) { Encoding = Encoding.UTF8, ReadTimeout = 150, WriteTimeout = 1000 };
        try
        {
            serial.Open();
            serial.DiscardInBuffer();
            return serial;
        }
        catch { serial.Dispose(); throw; }
    }

    private static string? ReadReply(SerialPort port, TimeSpan timeout, params string[] prefixes)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            try
            {
                string line = port.ReadLine().Trim();
                if (prefixes.Any(prefix => line.StartsWith(prefix, StringComparison.Ordinal))) return line;
            }
            catch (TimeoutException) { }
        }
        return null;
    }
}
