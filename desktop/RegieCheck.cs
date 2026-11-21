namespace TallyPager;

internal enum CheckResult { Ready, Warning, Problem }
internal sealed record CheckLine(CheckResult Result, string Target, string Detail);

internal static class RegieCheck
{
    public static CheckLine[] Run(RegieProfile profile, IReadOnlyDictionary<Guid, string> bindings,
        PagerStatus[] devices, MixerState atem, MixerState vmix, ArbiterState arbiter, string? bridgeMode)
    {
        var checks = new List<CheckLine>();
        var pagers = devices.ToDictionary(pager => pager.Id);
        var required = profile.Roles.SelectMany(role => new[] { role.Inside ?? OperatorSession.DefaultInside,
            role.Outside ?? OperatorSession.DefaultOutside }).Select(assignment => Source(assignment.Mixer)).ToHashSet();
        var source = profile.Sources;
        var operational = new Dictionary<MixerKind, bool>
        {
            [MixerKind.Atem] = AddSource(MixerKind.Atem, source.Atem, source.AtemEnabled, atem.Status),
            [MixerKind.Vmix] = AddSource(MixerKind.Vmix, source.Vmix, source.VmixEnabled, vmix.Status),
            [MixerKind.Arbiter] = AddSource(MixerKind.Arbiter, source.Arbiter, source.ArbiterEnabled, arbiter.Status)
        };

        var expectedRadio = new HashSet<string>(StringComparer.Ordinal);
        if (profile.Roles.Length == 0)
            checks.Add(new(CheckResult.Warning, "Kamera-Rollen", "Dieses Profil enthält keine erwarteten Sticks."));
        foreach (var role in profile.Roles)
        {
            void CheckLayer(string layer, Assignment assignment)
            {
                if (assignment.Mixer == MixerKind.Off || !operational[Source(assignment.Mixer)]) return;
                string target = $"{role.Name} · {layer}";
                string label = OperatorSession.Label(assignment.Mixer);
                var input = OperatorSession.FindInput(assignment, atem, vmix, arbiter);
                if (input is null)
                    checks.Add(new(CheckResult.Problem, target, $"{label} · zugeordneter Eingang {assignment.Input} fehlt."));
                else if (input.Tally == 0)
                    checks.Add(new(CheckResult.Warning, target, $"{label} · {input.Name} ist zugeordnet, der Tally-Zustand ist unbekannt."));
                else
                    checks.Add(new(CheckResult.Ready, target, $"{label} · {input.Name}"));
            }

            CheckLayer("Innen", role.Inside ?? OperatorSession.DefaultInside);
            CheckLayer("Außen", role.Outside ?? OperatorSession.DefaultOutside);
            if (!bindings.TryGetValue(role.Id, out string? id) || !pagers.TryGetValue(id, out var pager))
            {
                checks.Add(new(CheckResult.Problem, role.Name, "Der Rolle ist kein gepaarter Stick zugewiesen."));
                continue;
            }
            if (Transport.IsRadio(pager.Transport) || pager.Transport == Transport.Hotspot) expectedRadio.Add(pager.Transport);
            var result = pager.Connection switch
            {
                Connection.Connected => CheckResult.Ready,
                Connection.Delayed => CheckResult.Warning,
                _ => CheckResult.Problem
            };
            string connection = pager.Connection switch
            {
                Connection.Connected => "Verbunden", Connection.Delayed => "Verzögert", _ => "Getrennt"
            };
            string rtt = pager.Rtt is int ms ? $" · RTT {ms} ms" : "";
            checks.Add(new(result, role.Name, $"{pager.Name} · {connection} · {Transport.Name(pager.Transport)}{rtt}" +
                (pager.View.Test ? " · Simulation aktiv" : "")));
            if (pager.View.Test)
                checks.Add(new(CheckResult.Warning, $"{role.Name} · Simulation", "Auf dem Stick läuft ein Prüfsignal."));
            var firmware = pager.Firmware;
            bool complete = firmware?.Features is not null && firmware.Missing.Length == 0 && firmware.Unknown.Length == 0;
            checks.Add(new(complete ? CheckResult.Ready : CheckResult.Warning, $"{role.Name} · Firmware",
                firmware?.Description.Replace("\n", " · ", StringComparison.Ordinal) ?? "Firmware und Funktionen noch nicht gemeldet."));
        }
        foreach (string mode in expectedRadio.Order(StringComparer.Ordinal))
        {
            string label = Transport.Name(mode);
            bool available = Transport.Serves(bridgeMode, mode);
            checks.Add(new(available ? CheckResult.Ready : CheckResult.Problem, "USB-Funkbasis",
                available ? $"{label} · verbunden" : bridgeMode is null ? $"{label} · Basis fehlt." :
                $"{label} wird erwartet. Die Basis läuft mit {Transport.Name(bridgeMode)}."));
        }
        if (expectedRadio.Count == 0 && bridgeMode is not null)
            checks.Add(new(CheckResult.Ready, "USB-Funkbasis", $"{Transport.Name(bridgeMode)} · verbunden"));
        return checks.ToArray();

        bool AddSource(MixerKind kind, string address, bool enabled, string status)
        {
            bool needed = required.Contains(kind);
            if (!needed && (!enabled || address.Length == 0)) return false;
            bool online = enabled && address.Length > 0 && status.StartsWith("Verbunden", StringComparison.Ordinal);
            string detail = address.Length == 0 ? "Verbindungsadresse fehlt im aktiven Profil." :
                !enabled ? "Die Quelle ist im aktiven Profil deaktiviert." : status;
            checks.Add(new(online ? CheckResult.Ready : needed ? CheckResult.Problem : CheckResult.Warning,
                OperatorSession.Label(kind), detail));
            return online;
        }
    }

    private static MixerKind Source(MixerKind kind) =>
        kind is MixerKind.ArbiterAtem or MixerKind.ArbiterVmix ? MixerKind.Arbiter : kind;
}
