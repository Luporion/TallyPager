using System.Text.Json;
using System.Text.Json.Serialization;

namespace TallyPager;

internal sealed record SourceAddresses(string Atem = "", string Vmix = "", string Arbiter = "",
    bool AtemEnabled = true, bool VmixEnabled = true, bool ArbiterEnabled = true);
internal sealed record CameraRole(Guid Id, string Name, Assignment? Inside, Assignment? Outside, int? MiddleCamera = null)
{
    public override string ToString() => Name;
}
internal sealed record TargetGroup(Guid Id, string Name, Guid[] Roles)
{
    public override string ToString() => Name;
}
internal sealed record QuickText(Guid Id, string Name, string? Chat, string? Next, Guid[] Roles, Guid? Group,
    bool RequireAck, int? TtlSeconds)
{
    public override string ToString() => Name;
}
internal sealed record RegieProfile(Guid Id, string Name, SourceAddresses Sources, CameraRole[] Roles,
    TargetGroup[] Groups, QuickText[] Texts)
{
    public override string ToString() => Name;
}
internal sealed record ProfileBackup(int Version, Guid Active, RegieProfile[] Profiles);
internal sealed record LocalProfiles(ProfileBackup Backup, Dictionary<Guid, string> Bindings);

internal sealed class ProfileStore
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly string file;
    private LocalProfiles data;
    public ProfileBackup Backup => data.Backup;
    public RegieProfile Active => Backup.Profiles.Single(p => p.Id == Backup.Active);
    public IReadOnlyDictionary<Guid, string> Bindings => data.Bindings;
    public IEnumerable<Guid> Unbound(RegieProfile profile) => profile.Roles.Select(r => r.Id).Where(id => !Bindings.ContainsKey(id));

    public ProfileStore(string directory, IEnumerable<Pager> legacy)
    {
        file = Path.Combine(directory, "profiles.json");
        if (File.Exists(file))
        {
            data = Read<LocalProfiles>(file);
            Validate(data.Backup);
            return;
        }
        var bindings = new Dictionary<Guid, string>();
        var roles = legacy.Select(p =>
        {
            var id = Guid.NewGuid();
            bindings[id] = p.Id;
            return new CameraRole(id, p.Name, p.Inside, p.Outside);
        }).ToArray();
        string oldAddress = Path.Combine(directory, "arbiter-address.txt");
        var standard = new RegieProfile(Guid.NewGuid(), "Standard",
            new(Arbiter: File.Exists(oldAddress) ? File.ReadAllText(oldAddress).Trim() : ""), roles, [], []);
        data = new(new(1, standard.Id, [standard]), bindings);
        Write(file, data);
    }

    public CameraRole? RoleFor(string device) => Active.Roles.FirstOrDefault(r => data.Bindings.GetValueOrDefault(r.Id) == device);
    public (string[] Devices, string[] Missing) Targets(QuickText text, IEnumerable<string> knownDevices)
    {
        var roles = text.Group is Guid group ? Active.Groups.Single(g => g.Id == group).Roles : text.Roles;
        var known = knownDevices.ToHashSet(StringComparer.Ordinal);
        var devices = new List<string>();
        var missing = new List<string>();
        foreach (var role in Active.Roles.Where(r => roles.Contains(r.Id)))
        {
            if (Bindings.TryGetValue(role.Id, out var id) && known.Contains(id)) devices.Add(id);
            else missing.Add(role.Name);
        }
        return (devices.Distinct().ToArray(), missing.ToArray());
    }

    public void Update(RegieProfile profile) => Commit(data with
    {
        Backup = Backup with { Profiles = Backup.Profiles.Select(p => p.Id == profile.Id ? profile : p).ToArray() }
    });

    public void Activate(Guid id) => Commit(data with { Backup = Backup with { Active = id } });

    public void Duplicate(string name)
    {
        var copy = Copy(Active) with { Name = name };
        var bindings = new Dictionary<Guid, string>(Bindings);
        for (int i = 0; i < copy.Roles.Length; i++)
            if (Bindings.TryGetValue(Active.Roles[i].Id, out var id)) bindings[copy.Roles[i].Id] = id;
        Commit(new(Backup with { Profiles = [.. Backup.Profiles, copy] }, bindings));
    }

    public void Bind(string device, CameraRole role)
    {
        var bindings = new Dictionary<Guid, string>(Bindings);
        foreach (var existing in Active.Roles.Where(r => bindings.GetValueOrDefault(r.Id) == device))
            bindings.Remove(existing.Id);
        bindings[role.Id] = device;
        var roles = Active.Roles.Where(r => r.Id != role.Id).Append(role).ToArray();
        Commit(new(Backup with
        {
            Profiles = Backup.Profiles.Select(p => p.Id == Active.Id ? p with { Roles = roles } : p).ToArray()
        }, bindings));
    }

    public void Export(string path, bool onlyActive) =>
        Write(path, onlyActive ? Backup with { Profiles = [Active] } : Backup);

    public static ProfileBackup PreviewImport(string path)
    {
        var incoming = Read<ProfileBackup>(path);
        Validate(incoming);
        return incoming;
    }

    public void Import(ProfileBackup incoming, bool replace)
    {
        Validate(incoming);
        if (!replace)
        {
            var copies = incoming.Profiles.Select(p => Copy(p) with
                { Name = p.Name.Length <= 71 ? p.Name + " (Import)" : p.Name }).ToArray();
            Commit(data with { Backup = Backup with { Profiles = [.. Backup.Profiles, .. copies] } });
            return;
        }
        var retained = incoming.Profiles.SelectMany(p => p.Roles).Select(r => r.Id).ToHashSet();
        var bindings = Bindings.Where(pair => retained.Contains(pair.Key)).ToDictionary();
        string previous = Path.Combine(Path.GetDirectoryName(file)!, "profiles-before-import.json");
        Write(previous, data);
        Commit(new(incoming, bindings));
    }

    private void Commit(LocalProfiles next)
    {
        Validate(next.Backup);
        Write(file, next);
        data = next;
    }

    private static RegieProfile Copy(RegieProfile original)
    {
        var roles = original.Roles.ToDictionary(r => r.Id, _ => Guid.NewGuid());
        var groups = original.Groups.ToDictionary(g => g.Id, _ => Guid.NewGuid());
        return original with
        {
            Id = Guid.NewGuid(),
            Roles = original.Roles.Select(r => r with { Id = roles[r.Id] }).ToArray(),
            Groups = original.Groups.Select(g => g with { Id = groups[g.Id], Roles = g.Roles.Select(r => roles[r]).ToArray() }).ToArray(),
            Texts = original.Texts.Select(t => t with
            {
                Id = Guid.NewGuid(), Roles = t.Roles.Select(r => roles[r]).ToArray(),
                Group = t.Group is Guid group ? groups[group] : null
            }).ToArray()
        };
    }

    private static void Validate(ProfileBackup backup)
    {
        if (backup.Version != 1) throw new InvalidDataException($"Sicherungsformat {backup.Version} wird nicht unterstützt.");
        if (backup.Profiles is not { Length: > 0 and <= 100 } profiles ||
            profiles.Any(p => p is null || !NameOk(p.Name) || p.Sources is null) ||
            !profiles.Any(p => p.Id == backup.Active))
            throw new InvalidDataException("Ungültige Profile oder aktives Profil fehlt.");
        Unique(profiles.Select(p => p.Id));
        List<Guid> ids = [];
        foreach (var profile in profiles)
        {
            if (profile.Roles is not { Length: <= 200 } roles || profile.Groups is not { Length: <= 200 } groups ||
                profile.Texts is not { Length: <= 200 } texts ||
                new[] { profile.Sources.Atem, profile.Sources.Vmix, profile.Sources.Arbiter }
                    .Any(value => value is null || value.Length > 256 || value.Any(char.IsControl)) ||
                (profile.Sources.Atem.Length > 0 && (!System.Net.IPAddress.TryParse(profile.Sources.Atem, out var ip) ||
                    ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)) ||
                (profile.Sources.Vmix.Length > 0 && !VmixSource.ValidAddress(profile.Sources.Vmix)) ||
                (profile.Sources.Arbiter.Length > 0 && TallyArbiterSource.Endpoint(profile.Sources.Arbiter) is null))
                throw new InvalidDataException("Ungültige Profilfelder.");
            if (roles.Any(r => r is null || !NameOk(r.Name) || !AssignmentOk(r.Inside) ||
                !AssignmentOk(r.Outside) || r.MiddleCamera is < 1 or > 99))
                throw new InvalidDataException("Ungültige Kamera-Rolle.");
            var roleIds = roles.Select(r => r.Id).ToHashSet();
            if (groups.Any(g => g is null || !NameOk(g.Name) || g.Roles is not { Length: > 0 and <= 200 } ||
                !g.Roles.All(roleIds.Contains)))
                throw new InvalidDataException("Eine Gruppe enthält unbekannte Kamera-Rollen.");
            var groupIds = groups.Select(g => g.Id).ToHashSet();
            if (texts.Any(t => t is null || !NameOk(t.Name) || t.Roles is not { Length: <= 200 } ||
                !t.Roles.All(roleIds.Contains) || (t.Group is Guid g ? !groupIds.Contains(g) || t.Roles.Length != 0 : t.Roles.Length == 0) ||
                !TextOk(t.Chat, 160) || !TextOk(t.Next, 80) || (t.Chat is null && t.Next is null) ||
                (t.RequireAck && string.IsNullOrWhiteSpace(t.Chat)) || t.TtlSeconds is < 5 or > 3600))
                throw new InvalidDataException("Ungültiger Schnelltext oder Ziel fehlt.");
            ids.AddRange(roles.Select(r => r.Id));
            ids.AddRange(groups.Select(g => g.Id));
            ids.AddRange(texts.Select(t => t.Id));
        }
        Unique(ids);
    }
    private static void Unique(IEnumerable<Guid> ids)
    {
        var unique = new HashSet<Guid>();
        if (ids.Any(id => id == Guid.Empty || !unique.Add(id))) throw new InvalidDataException("Leere oder doppelte ID in der Sicherung.");
    }
    private static bool NameOk(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 80 && !text.Any(char.IsControl);
    internal static bool TextOk(string? text, int limit) => text is null ||
        (System.Text.Encoding.UTF8.GetByteCount(text) <= limit && !text.Any(char.IsControl));
    private static bool AssignmentOk(Assignment? entry) => entry is null ||
        (Enum.IsDefined(entry.Mixer) && entry.Input is >= 0 and <= 65535 &&
         (entry.Key is null || entry.Key.Length is > 0 and <= 128 && !entry.Key.Any(char.IsControl)));

    private static T Read<T>(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 4_000_000) throw new InvalidDataException("Die Sicherung ist zu groß.");
        return JsonSerializer.Deserialize<T>(stream, Json) ?? throw new InvalidDataException("Die Sicherung ist leer.");
    }

    internal static void Write<T>(string path, T value, JsonSerializerOptions? options = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, options ?? Json));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
