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
    string[]? Features = null, int Slot = 0)
{
    public FirmwareInfo? Firmware => FirmwareInfo.Read(Version, Features);
}
internal sealed record UsbSettings(string Id, string Name, string Host, string Ssid, string Password,
    string Mode, string Gateway, int Channel, int Slot);

internal static class PagerUsb
{
    public const string Version = "0.8.0";
    public const string FirmwareFileName = "TallyPager-Firmware-M5StickC-Plus2-" + Version + ".zip";
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
            serial.WriteLine("TP STATUS");
            var reply = ReadReply(serial, TimeSpan.FromMilliseconds(420), "TP {");
            if (reply?.StartsWith("TP {", StringComparison.Ordinal) == true)
            {
                if (ParseStatus(reply) is { } status) return status;
            }
            Thread.Sleep(120);
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
        return SetRole(port, id, status => status.Firmware?.AllowsBase(mode) == true,
            "TP BASE " + JsonSerializer.Serialize(new { mode, channel }, Json),
            status => status.BaseRole && status.RadioReady && status.Mode == mode && status.Channel == channel);
    }

    public static UsbStatus RestorePager(string port, string id) =>
        SetRole(port, id, status => status.BaseRole && status.Firmware?.Supports(FirmwareInfo.RadioBase) == true,
            "TP PAGER", status => status.Configured && !status.BaseRole);

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

    public static async Task Flash(string port, string package, IProgress<string> progress)
    {
        string directory = Path.Combine(PilotCore.DataDirectory, "bundle", Version);
        Directory.CreateDirectory(directory);
        WriteVerifiedResource(directory, "esptool.exe");
        LoadFirmware(package, directory);
        var start = new ProcessStartInfo(Path.Combine(directory, "esptool.exe"))
        {
            WorkingDirectory = directory, CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        string[] common = ["--chip", "esp32", "--port", port];
        bool correctChip = false;
        await Run(start, [..common, "chip-id"], line =>
        {
            if (line.Contains("ESP32-PICO-V3-02", StringComparison.Ordinal)) correctChip = true;
        });
        if (!correctChip)
            throw new InvalidOperationException("Der ESP32-Chip passt nicht zum M5StickC Plus2.");
        progress.Report("Firmware wird geschrieben und überprüft …");
        await Run(start, [..common, "--baud", "460800", "write-flash", "--flash-mode", "dio",
            "--flash-freq", "80m", "--flash-size", "8MB",
            .. Images.SelectMany(image => new[] { image.Offset, image.Name })], line =>
            {
                if (line.Contains('%') || line.Contains("verified", StringComparison.OrdinalIgnoreCase))
                    progress.Report(line.Trim());
            });
    }

    internal static void LoadFirmware(string path, string? directory, string expectedVersion = Version)
    {
        using var zip = ZipFile.OpenRead(path);
        var manifestEntry = zip.GetEntry("manifest.json");
        if (manifestEntry is null || manifestEntry.Length is < 1 or > 4096)
            throw new InvalidDataException("Im Firmwarepaket fehlt das Manifest.");
        using var manifestStream = manifestEntry.Open();
        var manifest = JsonSerializer.Deserialize<FirmwareManifest>(manifestStream, Json);
        if (manifest is not { Board: "m5stick-cplus2", Sha256: not null } || manifest.Version != expectedVersion)
            throw new InvalidDataException($"Bitte das M5StickC-Plus2-Firmwarepaket für TallyPager {expectedVersion} auswählen.");

        foreach (var image in Images)
        {
            var entry = zip.GetEntry(image.Name);
            if (entry is null || entry.Length < 1 || entry.Length > image.Limit ||
                !manifest.Sha256.TryGetValue(image.Name, out string? expected))
                throw new InvalidDataException("Fehlendes oder ungültiges Firmware-Image: " + image.Name);
            // Only read the four known files. ZIP paths never control the output location.
            using var stream = entry.Open();
            byte[] bytes = new byte[checked((int)entry.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1 ||
                !Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Firmware-Prüfsumme stimmt nicht: " + image.Name);
            if (directory is not null) File.WriteAllBytes(Path.Combine(directory, image.Name), bytes);
        }
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
        var serial = new SerialPort(port, 115200) { Encoding = Encoding.UTF8, ReadTimeout = 150, WriteTimeout = 1000 };
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
