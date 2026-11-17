using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TallyPager;

internal sealed record WifiSettings(string Version, IPAddress StickIp, string Host, string Name, string Ssid,
    string RequestId, string Result);
internal sealed record WifiChange(string Name, string Host, string Ssid, string Password);

internal static class WifiManagement
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool ValidHost(string value) => IPAddress.TryParse(value, out var host) &&
        host.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(host) && !host.Equals(IPAddress.Any) &&
        !host.Equals(IPAddress.Broadcast) && (host.GetAddressBytes()[0] is > 0 and < 224);

    public static IEnumerable<IPAddress> BroadcastAddresses()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork && item.PrefixLength is > 0 and < 31)
            .Select(item =>
            {
                byte[] mask = item.IPv4Mask.GetAddressBytes();
                byte[] address = item.Address.GetAddressBytes();
                for (int i = 0; i < 4; i++) address[i] |= (byte)~mask[i];
                return new IPAddress(address);
            }).Distinct();
    }

    public static bool Valid(WifiChange change) => !string.IsNullOrWhiteSpace(change.Name) && ValidHost(change.Host) &&
        ProfileStore.TextOk(change.Name, 48) && ProfileStore.TextOk(change.Ssid, 32) &&
        ProfileStore.TextOk(change.Password, 64) && (change.Ssid.Length != 0 || change.Password.Length == 0);

    internal static string Command(string device, string key, string session, string request, uint counter, WifiChange change)
    {
        if (!Valid(change)) throw new ArgumentException("Name, IPv4-Adresse oder WLAN-Daten prüfen.");
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(change, Json);
        byte[] cipher = new byte[plaintext.Length];
        byte[] tag = new byte[16];
        byte[] encryptionKey = HMACSHA256.HashData(Convert.FromHexString(key), "TallyPager WLAN config v1"u8);
        string associated = $"{device}|{session}|{request}|{counter.ToString(CultureInfo.InvariantCulture)}";
        try
        {
            if (plaintext.Length > 340) throw new ArgumentException("Die WLAN-Einstellungen sind zu lang.");
            using var aes = new AesGcm(encryptionKey, 16);
            aes.Encrypt(nonce, plaintext, cipher, tag, Encoding.ASCII.GetBytes(associated));
            return $"TP1|cfg|{associated}|{Hex(nonce)}|{Hex(cipher)}|{Hex(tag)}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptionKey);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal static WifiSettings? Parse(string[] fields, IPAddress device)
    {
        if (fields is not [_, "cfg_info", _, _, var version, var host, var name, var ssid, var request, var result] ||
            !System.Version.TryParse(version, out _) || !ValidHost(host) ||
            request.Length != 0 && !Guid.TryParseExact(request, "N", out _) ||
            result is not ("idle" or "testing" or "applied" or "rollback" or "failed") ||
            name.Length is < 2 or > 96 || ssid.Length > 64) return null;
        try
        {
            return new(version, device, host, StrictUtf8.GetString(Convert.FromHexString(name)),
                StrictUtf8.GetString(Convert.FromHexString(ssid)), request, result);
        }
        catch (Exception error) when (error is FormatException or DecoderFallbackException) { return null; }
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
