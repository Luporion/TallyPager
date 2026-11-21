using System.IO.Ports;
using System.Threading.Channels;

namespace TallyPager;

internal interface IRadioBridge : IDisposable
{
    string Mode { get; }
    bool TryRead(out RadioPacket packet);
    bool Send(string address, string payload);
}
internal readonly record struct RadioPacket(string Address, string Payload);

internal sealed class RadioBridge : IRadioBridge
{
    private const string Prefix = "TP LINK ";
    private readonly SerialPort serial;
    private readonly Channel<RadioPacket> inbound = Channel.CreateBounded<RadioPacket>(64);
    private readonly Channel<string> outbound = Channel.CreateBounded<string>(24);
    private volatile bool closed;
    private volatile string? failure;
    public string? Failure => failure;
    public UsbStatus Status { get; }
    public string Mode => Status.Mode;
    public string Port => serial.PortName;

    private RadioBridge(SerialPort serial, UsbStatus status)
    {
        this.serial = serial;
        Status = status;
        serial.WriteTimeout = 200;
        _ = Task.Run(Pump);
    }

    public static RadioBridge Connect(string port)
    {
        var serial = PagerUsb.Open(port);
        try
        {
            var status = PagerUsb.ReadStatus(serial, TimeSpan.FromSeconds(5));
            if (!CanConnect(status))
                throw new InvalidOperationException("Am USB-Port antwortet keine betriebsbereite TallyPager-Funkbasis.");
            return new(serial, status);
        }
        catch { serial.Dispose(); throw; }
    }

    internal static bool CanConnect(UsbStatus status) => status.BaseRole && status.RadioReady &&
        status.Firmware?.AllowsBase(status.Mode) == true;

    public bool TryRead(out RadioPacket packet) => inbound.Reader.TryRead(out packet);
    public bool Send(string address, string payload) => !closed && failure is null &&
        outbound.Writer.TryWrite(Prefix + address + " " + payload);

    public void SendBaseView(PagerStatus[] pagers)
    {
        if (closed || failure is not null || Status.Firmware?.Supports("base-preview") != true) return;
        outbound.Writer.TryWrite(BaseView(pagers, Mode, Status.Firmware.Supports("base-preview-details")));
    }

    internal static string BaseView(PagerStatus[] pagers, string mode, bool details)
    {
        var selected = pagers.Where(p => p.Transport == mode).OrderByDescending(p => p.Online).Take(4);
        var rows = selected.Select(pager =>
        {
            var view = pager.View;
            int inside = pager.Online ? view.Inside : 0;
            int outside = pager.Online ? view.Outside : 0;
            string name = Compact(pager.Name, 17);
            if (!details) return (object)new { name, inside, outside };
            bool insideOn = view.InsideSource != "off", outsideOn = view.OutsideSource != "off";
            int tally = view.TextMode == 1 ? (outsideOn ? outside : inside) : (insideOn ? inside : outside);
            string text = view.TextMode == 0 ? Compact(view.Chat, 17) :
                tally switch { 3 => "LIVE", 2 => "PREVIEW", 1 => "SAFE", _ => "?" };
            string badge = !pager.Online ? "" : (view.Test, view.Beamer) switch
            {
                (true, true) => "TEST + BEAMER", (true, false) => "TEST", (false, true) => "BEAMER", _ => ""
            };
            return new { name, inside, outside, text, badge, online = pager.Online, insideOn, outsideOn };
        });
        return "TP BASEVIEW " + System.Text.Json.JsonSerializer.Serialize(rows, BaseViewJson);
    }

    private static readonly System.Text.Json.JsonSerializerOptions BaseViewJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string Compact(string text, int length) =>
        string.Concat(text.Take(length).Select(ch => ch is >= ' ' and <= '~' or 'ä' or 'ö' or 'ü' or 'Ä' or 'Ö' or 'Ü' or 'ß'
            ? ch : ' '));

    private async Task Pump()
    {
        const int limit = 921;
        var line = new System.Text.StringBuilder(limit);
        bool overflow = false;
        try
        {
            while (!closed)
            {
                for (int i = 0; i < 4 && outbound.Reader.TryRead(out var send); i++)
                    serial.WriteLine(send);
                foreach (char ch in serial.ReadExisting())
                {
                    if (ch == '\n')
                    {
                        if (!overflow && Parse(line.ToString().TrimEnd('\r'), Mode) is { } packet)
                            inbound.Writer.TryWrite(packet);
                        overflow = false;
                        line.Clear();
                    }
                    else if (line.Length < limit) line.Append(ch);
                    else overflow = true;
                }
                await Task.Delay(5).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or UnauthorizedAccessException)
        {
            if (!closed) failure = error.Message;
        }
        finally { serial.Dispose(); }
    }

    internal static RadioPacket? Parse(string line, string mode = "espnow") =>
        line.Length is > 21 and <= 921 && line.StartsWith(Prefix, StringComparison.Ordinal) &&
        line[20] == ' ' && Transport.ValidEndpoint(mode, line[8..20]) && line.AsSpan(21).StartsWith("TP1|", StringComparison.Ordinal)
        ? new(line[8..20], line[21..]) : null;

    public void Dispose() { closed = true; serial.Dispose(); }
}

internal static class Transport
{
    public const string Hotspot = "hotspot";
    public const string HotspotIp = "192.168.4.1";
    public static string PagerMode(string transport) => transport == Hotspot ? "udp" : transport;
    public static bool ValidEndpoint(string mode, string address) => mode == Hotspot ? HotspotAddress(address) is not null : IsMac(address);
    public static System.Net.IPAddress? HotspotAddress(string? address)
    {
        if (address is not { Length: 12 } || !address.StartsWith("c0a804", StringComparison.Ordinal) ||
            address.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) return null;
        byte[] bytes = Convert.FromHexString(address);
        return bytes[3] is >= 2 and <= 254 && (bytes[4] != 0 || bytes[5] != 0)
            ? new System.Net.IPAddress(bytes.AsSpan(0, 4)) : null;
    }

    public static string? DisplayAddress(string? address) => address is not null && IsMac(address)
        ? Convert.ToHexString(Convert.FromHexString(address)).Insert(10, ":").Insert(8, ":").Insert(6, ":")
            .Insert(4, ":").Insert(2, ":")
        : null;
    public static bool IsRadio(string mode) => mode is "espnow" or "espnow-lr" or "ble";
    public static bool IsMac(string address) => address.Length == 12 &&
        address.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
        "02468ace".Contains(address[1]) && address != "000000000000";
    public static string Name(string mode) => mode switch
    {
        "espnow" => "ESP-NOW", "espnow-lr" => "ESP-NOW LR", "ble" => "BLE", Hotspot => "WLAN-Brücke", _ => "WLAN + UDP"
    };
}
