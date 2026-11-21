namespace TallyPager;

internal sealed record FirmwareChoice(FirmwareTarget Target, Version Version, string? LocalPath = null, GitHubRelease? Release = null)
{
    public override string ToString() => $"{Version} · {(LocalPath is null ? "GitHub" : "Lokal")}" +
        (Target == FirmwareTarget.Esp8266 && Version >= new Version(0, 15, 0) ? " · mit Mumble" : "");
}

internal static class FirmwareCatalog
{
    public static FirmwareTarget[] CompatibleTargets(FirmwareTarget hardware) => hardware == FirmwareTarget.Esp8266
        ? [FirmwareTarget.Esp8266] : [FirmwareTarget.Pager, FirmwareTarget.Hotspot];

    public static FirmwareTarget? InstalledTarget(UsbStatus? status) => status?.Board switch
    {
        "esp8266" => FirmwareTarget.Esp8266,
        "m5stick-cplus2" => status.Mode == Transport.Hotspot ? FirmwareTarget.Hotspot : FirmwareTarget.Pager,
        _ => null
    };

    public static string Name(FirmwareTarget target) => target switch
    {
        FirmwareTarget.Pager => "M5StickC Plus2 · Pager und Funkbasis",
        FirmwareTarget.Hotspot => "M5StickC Plus2 · WLAN-Brücke",
        FirmwareTarget.Esp8266 => "ESP8266MOD · USB-Funkbasis",
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };

    public static string FileName(FirmwareTarget target, Version version) => target switch
    {
        FirmwareTarget.Pager => GitHubUpdates.FirmwareName(version),
        FirmwareTarget.Hotspot => GitHubUpdates.HotspotFirmwareName(version),
        FirmwareTarget.Esp8266 => $"TallyPager-Firmware-ESP8266-{version}.zip",
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };

    public static FirmwareChoice FromFile(string path, bool verify = false)
    {
        var (target, version) = PagerUsb.InspectFirmware(path, verify);
        return new(target, version, LocalPath: path);
    }

    public static FirmwareChoice[] FindLocal(string appDirectory, string cache, params string[] releaseRoots)
    {
        return new[] { appDirectory, cache }.Concat(releaseRoots.SelectMany(ReleaseFolders))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(FirmwareFiles).Select(TryInspect).OfType<FirmwareChoice>().ToArray();
    }

    public static FirmwareChoice[] ForTarget(FirmwareTarget target, IEnumerable<FirmwareChoice> options) => options
        .Where(option => option.Target == target)
        .OrderByDescending(option => option.Version)
        .ThenBy(option => option.LocalPath is null)
        .DistinctBy(option => option.Version)
        .ToArray();

    private static string[] FirmwareFiles(string directory)
    {
        try { return Directory.GetFiles(directory, "TallyPager-Firmware-*.zip"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> ReleaseFolders(string root)
    {
        foreach (string parent in new[] { root, Path.Combine(root, "GitHub") })
        {
            string[] children;
            try { children = Directory.GetDirectories(parent); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            foreach (string child in children.Order(StringComparer.OrdinalIgnoreCase))
                if (Version.TryParse(Path.GetFileName(child).Split('-', 2)[0], out _)) yield return child;
        }
    }

    private static FirmwareChoice? TryInspect(string path)
    {
        try { return FromFile(path, verify: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }
}
