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
                        if (!overflow && Parse(line.ToString().TrimEnd('\r')) is { } packet)
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

    internal static RadioPacket? Parse(string line) =>
        line.Length is > 21 and <= 921 && line.StartsWith(Prefix, StringComparison.Ordinal) &&
        line[20] == ' ' && Transport.IsMac(line[8..20]) && line.AsSpan(21).StartsWith("TP1|")
        ? new(line[8..20], line[21..]) : null;

    public void Dispose() { closed = true; serial.Dispose(); }
}

internal static class Transport
{
    public static bool IsRadio(string mode) => mode is "espnow" or "espnow-lr" or "ble";
    public static bool IsMac(string address) => address.Length == 12 &&
        address.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
        "02468ace".Contains(address[1]) && address != "000000000000";
    public static string Name(string mode) => mode switch
    {
        "espnow" => "ESP-NOW", "espnow-lr" => "ESP-NOW LR", "ble" => "BLE", _ => "WLAN + UDP"
    };
}
