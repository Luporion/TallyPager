using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TallyPager;

internal sealed record GitHubAsset(long Id, string Name, long Size, string? Digest, string State);
internal sealed record GitHubRelease([property: JsonPropertyName("tag_name")] string Tag,
    bool Draft, bool Prerelease, GitHubAsset[] Assets);
internal sealed record InstalledUpdate(string Executable, string Firmware, string ExeHash);

internal sealed class GitHubUpdates(HttpClient client)
{
    internal const string ExeName = "TallyPager-Windows-x64.exe";
    internal const string Repository = "Luporion/TallyPager";
    internal const string ProjectUrl = "https://github.com/" + Repository + "/releases";
    private const string Api = "https://api.github.com/repos/" + Repository + "/releases/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static HttpClient OpenClient() => new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromMinutes(3) };

    public static string FirmwareName(Version version) => $"TallyPager-Firmware-M5StickC-Plus2-{version}.zip";

    internal static Version ReleaseVersion(GitHubRelease release)
    {
        if (release.Draft || release.Prerelease || release.Tag is null || !release.Tag.StartsWith('v') ||
            !Version.TryParse(release.Tag[1..], out var version) || version.Build < 0 || version.Revision >= 0 ||
            release.Tag != $"v{version}")
            throw new InvalidDataException("GitHub meldet keinen stabilen TallyPager-Release mit einer gültigen Versionsnummer.");
        return version;
    }

    public async Task<GitHubRelease> Check(CancellationToken cancel)
    {
        using var request = Request(new(Api + "latest"), binary: false);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("Auf GitHub ist kein öffentlicher TallyPager-Release verfügbar.");
        response.EnsureSuccessStatusCode();
        using var data = new MemoryStream();
        await Transfer(response, data, maximum: 2_000_000, expected: null, cancel);
        data.Position = 0;
        var release = JsonSerializer.Deserialize<GitHubRelease>(data, Json) ??
            throw new InvalidDataException("Die Release-Antwort von GitHub ist leer.");
        ReleaseVersion(release);
        return release;
    }

    public async Task<InstalledUpdate> Download(GitHubRelease release, string root, IProgress<string> progress,
        CancellationToken cancel)
    {
        var version = ReleaseVersion(release);
        if (version < Version.Parse(PagerUsb.Version))
            throw new InvalidOperationException("Das GitHub-Release ist älter als das gestartete Programm.");
        string firmwareName = FirmwareName(version);
        var exe = RequireAsset(release, ExeName, 250_000_000);
        var firmware = RequireAsset(release, firmwareName, 15_000_000);
        string exeHash = await ExpectedHash(release, exe, cancel);
        string firmwareHash = await ExpectedHash(release, firmware, cancel);
        string destination = Path.Combine(root, version.ToString());
        if (Directory.Exists(destination))
        {
            if (Matches(destination)) return Installed(destination);
            destination = Path.Combine(root, "GitHub", version.ToString());
            if (Directory.Exists(destination))
            {
                if (Matches(destination)) return Installed(destination);
                throw new IOException("Im Zielordner liegen andere Dateien. Lokale Dateien bleiben erhalten.");
            }
        }

        string parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, ".tallypager-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            string executable = Path.Combine(staging, ExeName);
            string package = Path.Combine(staging, firmwareName);
            progress.Report("Windows-Programm wird geladen …");
            await DownloadFile(release.Tag, exe, exeHash, executable, cancel);
            progress.Report("Stick-Firmware wird geladen …");
            await DownloadFile(release.Tag, firmware, firmwareHash, package, cancel);
            progress.Report("Versionen und Prüfsummen werden geprüft …");
            VerifyPair(staging);
            cancel.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            return Installed(destination);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                File.Delete(Path.Combine(staging, ExeName));
                File.Delete(Path.Combine(staging, firmwareName));
                Directory.Delete(staging);
            }
        }

        bool Matches(string directory)
        {
            if (!HashMatches(Path.Combine(directory, ExeName), exeHash) ||
                !HashMatches(Path.Combine(directory, firmwareName), firmwareHash)) return false;
            VerifyPair(directory);
            return true;
        }
        void VerifyPair(string directory)
        {
            if (!ExecutableHasVersion(Path.Combine(directory, ExeName), version))
                throw new InvalidDataException("Die EXE enthält eine andere Produktversion als das GitHub-Release.");
            PagerUsb.LoadFirmware(Path.Combine(directory, firmwareName), null, version.ToString());
        }
        InstalledUpdate Installed(string directory) =>
            new(Path.Combine(directory, ExeName), Path.Combine(directory, firmwareName), exeHash);
    }

    private static bool ExecutableHasVersion(string path, Version expected)
    {
        var version = FileVersionInfo.GetVersionInfo(path);
        return version.FileMajorPart == expected.Major && version.FileMinorPart == expected.Minor &&
            version.FileBuildPart == expected.Build && version.FilePrivatePart == 0;
    }

    private static GitHubAsset RequireAsset(GitHubRelease release, string name, long maxSize)
    {
        var matches = (release.Assets ?? []).Where(item => item?.Name == name).ToArray();
        if (matches is not [var file] || file.Id <= 0 || file.State != "uploaded" || file.Size <= 0 || file.Size > maxSize)
            throw new InvalidDataException($"Das GitHub-Release enthält keine vollständige Datei {name}.");
        return file;
    }

    private async Task<string> ExpectedHash(GitHubRelease release, GitHubAsset asset, CancellationToken cancel)
    {
        if (!string.IsNullOrWhiteSpace(asset.Digest))
        {
            if (asset.Digest.StartsWith("sha256:", StringComparison.Ordinal) && ValidHash(asset.Digest[7..]))
                return asset.Digest[7..];
            throw new InvalidDataException("GitHub meldet eine ungültige SHA-256-Prüfsumme.");
        }
        var sidecar = RequireAsset(release, asset.Name + ".sha256", 1024);
        using var response = await AssetResponse(release.Tag, sidecar, cancel);
        using var data = new MemoryStream();
        await Transfer(response, data, 1024, sidecar.Size, cancel);
        var fields = Encoding.ASCII.GetString(data.ToArray()).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields is not [var hash, var name] || name != asset.Name || !ValidHash(hash))
            throw new InvalidDataException($"Die Prüfsummendatei für {asset.Name} ist ungültig.");
        return hash;
    }

    private static bool ValidHash(string hash) => hash.Length == 64 && hash.All(Uri.IsHexDigit);

    private async Task DownloadFile(string tag, GitHubAsset asset, string expected, string destination, CancellationToken cancel)
    {
        using var response = await AssetResponse(tag, asset, cancel);
        await using (var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            await Transfer(response, file, asset.Size, asset.Size, cancel);
        if (!HashMatches(destination, expected))
            throw new InvalidDataException($"Die SHA-256-Prüfsumme für {asset.Name} stimmt nicht.");
    }

    internal static bool HashMatches(string path, string expected)
    {
        if (!File.Exists(path)) return false;
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<HttpResponseMessage> AssetResponse(string tag, GitHubAsset asset, CancellationToken cancel)
    {
        using var request = Request(new(ProjectUrl + "/download/" + tag + "/" + Uri.EscapeDataString(asset.Name)),
            binary: true, api: false);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (response.StatusCode is HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect or HttpStatusCode.SeeOther)
        {
            var target = response.Headers.Location;
            response.Dispose();
            if (target is not { IsAbsoluteUri: true, Scheme: "https", IsDefaultPort: true, UserInfo.Length: 0 } ||
                target.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new InvalidDataException("Der Download verweist auf kein bekanntes GitHub-Release-Ziel.");
            using var requestToCdn = Request(target, binary: true, api: false);
            response = await client.SendAsync(requestToCdn, HttpCompletionOption.ResponseHeadersRead, cancel);
        }
        if (response.IsSuccessStatusCode) return response;
        using (response) response.EnsureSuccessStatusCode();
        throw new InvalidOperationException();
    }

    private static HttpRequestMessage Request(Uri uri, bool binary, bool api = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("TallyPager/" + PagerUsb.Version);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(binary ? "application/octet-stream" : "application/vnd.github+json"));
        if (api) request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static async Task Transfer(HttpResponseMessage response, Stream output, long maximum, long? expected,
        CancellationToken cancel)
    {
        if (response.Content.Headers.ContentLength is long length && (length > maximum || expected is long size && size != length))
            throw new InvalidDataException("Die Serverdatei hat eine unerwartete Größe.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancel);
        byte[] buffer = new byte[81920];
        long transferred = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancel)) > 0)
        {
            transferred += read;
            if (transferred > maximum) throw new InvalidDataException("Die Serverdatei ist größer als angekündigt.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancel);
        }
        if (expected is long total && transferred != total)
            throw new InvalidDataException("Der Download ist unvollständig.");
    }
}
