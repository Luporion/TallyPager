namespace TallyPager;

internal sealed class FirmwareInfo
{
    public const string Usb = "usb-config";
    public const string Acknowledge = "chat-ack";
    public const string Wifi = "wifi-config";
    public const string RadioBase = "radio-base";
    public const string RemoteSlots = "remote-slots";
    public const string TallyLabels = "tally-labels";
    public const string DisplayPower = "display-power";
    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        [Usb] = "USB-Einrichtung",
        [Acknowledge] = "Chat quittieren",
        ["link-stats"] = "RTT und Verbindungswerte",
        ["diagnostics"] = "Erweiterte Diagnose",
        [Wifi] = "WLAN-Einstellungen",
        ["wifi-discovery"] = "WLAN-Suche",
        [RadioBase] = "Funkbasis",
        ["espnow"] = "ESP-NOW",
        ["espnow-lr"] = "ESP-NOW LR",
        ["ble"] = "BLE",
        ["usb-read-settings"] = "Stick-Einstellungen per USB auslesen",
        ["clock"] = "Uhrzeit",
        ["device-slots"] = "Geräteprofile am Stick",
        ["base-preview"] = "Mini-Anzeige auf der Funkbasis",
        ["base-preview-details"] = "Mini-Vorschau mit Text und Verbindungsstatus",
        [RemoteSlots] = "Geräteprofil über Funk wechseln",
        ["presentation"] = "Tally-Großtext, Beamer und Live-Inputs",
        [TallyLabels] = "Eigene Tally-Texte ein- und ausblenden",
        [DisplayPower] = "Helligkeit und automatisches Dimmen fernsteuern"
    };
    private static readonly string[] V04 = [Usb, "link-stats"];
    private static readonly string[] V05 = [.. V04, Acknowledge, "diagnostics"];
    private static readonly string[] V06 = [.. V05, Wifi, "wifi-discovery", RadioBase, "espnow", "espnow-lr", "ble"];
    private static readonly Version Ui = Version.Parse(PagerUsb.Version);

    private readonly string[]? features;
    public string VersionText { get; }
    public Version VersionNumber { get; }
    public bool Advertised { get; }
    public bool ExactVersion { get; }
    public IReadOnlyList<string>? Features => features;

    private FirmwareInfo(string version, Version number, bool advertised, bool exact, string[]? features)
    {
        VersionText = version;
        VersionNumber = number;
        Advertised = advertised;
        ExactVersion = exact;
        this.features = features;
    }

    public static FirmwareInfo? Read(string? reported, IEnumerable<string>? advertised = null)
    {
        if (reported is null || reported.Length > 24 || !Version.TryParse(reported, out var version) ||
            version.Build < 0 || version.Revision >= 0) return null;
        if (advertised is null)
        {
            string[]? supported = reported switch
            {
                "0.3.0" or "0.4.0" => V04,
                "0.5.0" => V05,
                "0.6.0" => V06,
                _ => null
            };
            return new(reported, version, false, true, supported);
        }
        string[] features = advertised.Take(65).ToArray();
        if (features.Length > 64 || features.Any(id => id is null || id.Length is < 1 or > 40 ||
            id.Any(ch => ch is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.')))) return null;
        return new(reported, version, true, true, features.Distinct(StringComparer.Ordinal).ToArray());
    }

    public static FirmwareInfo LegacyRadio { get; } = new("0.6.0", new(0, 6, 0), false, false, null);
    public bool Supports(string feature) => features?.Contains(feature, StringComparer.Ordinal) == true;
    public bool AllowsUsbMode(string mode) => Supports(Usb) && (mode == "udp" || Supports(mode));
    public bool AllowsBase(string mode) => Transport.IsRadio(mode) && Supports(RadioBase) && Supports(mode);
    public bool Older => ExactVersion && VersionNumber < Ui;
    public bool Newer => ExactVersion && VersionNumber > Ui;
    public string ShortVersion => !ExactVersion ? $"≥ {VersionText}" :
        Older ? VersionText + " · älter" : Newer ? VersionText + " · neuer" : VersionText;

    public string[] Missing => features is null ? [] : Known.Where(pair => !Supports(pair.Key)).Select(pair => pair.Value).ToArray();
    public string[] Available => features is null ? [] : Known.Where(pair => Supports(pair.Key)).Select(pair => pair.Value).ToArray();
    public string[] Unknown => features is null ? [] : features.Where(id => !Known.ContainsKey(id)).ToArray();

    public string Description
    {
        get
        {
            string detail = "Firmware " + ShortVersion;
            if (!ExactVersion) return detail + " · genaue Version per USB ablesen";
            if (features is null) return detail + " · Funktionsliste nicht gemeldet";
            var missing = Missing;
            var unknown = Unknown;
            if (missing.Length > 0) detail += "\nFehlend · " + string.Join(", ", missing);
            if (unknown.Length > 0) detail += "\nDieser App unbekannt · " + string.Join(", ", unknown);
            if (missing.Length == 0 && unknown.Length == 0) detail += " · alle bekannten Funktionen verfügbar";
            return detail;
        }
    }
}
