namespace TallyPager;

internal enum Scenario
{
    Manual, VmixOnOff, VmixOffline, Alternating, PagerOffline, PreviewThenProgram
}

internal sealed record Simulation(Scenario Scenario, int PeriodMs, int Atem, int Vmix, long Started)
{
    public (int Atem, int Vmix, bool Pause) State(long now)
    {
        long step = Math.Max(0, now - Started) / PeriodMs;
        bool first = step % 2 == 0;
        return Scenario switch
        {
            Scenario.VmixOnOff => (Atem, first ? 3 : 1, false),
            Scenario.VmixOffline => (Atem, first ? 3 : 0, false),
            Scenario.Alternating => (first ? 3 : 2, first ? 2 : 3, false),
            Scenario.PagerOffline => (Atem, Vmix, !first),
            Scenario.PreviewThenProgram => ((step % 3) switch { 0 => 2, 1 => 3, _ => 1 }, Vmix, false),
            _ => (Atem, Vmix, false)
        };
    }
}

internal sealed class OperatorSession(PilotCore core)
{
    private readonly Dictionary<string, (string Chat, string Next)> texts = [];
    private readonly Dictionary<string, Simulation> simulations = [];
    internal static Assignment DefaultInside { get; } = new(MixerKind.Atem, 1);
    internal static Assignment DefaultOutside { get; } = new(MixerKind.Vmix, 1);
    public bool IsTesting(string id) => simulations.ContainsKey(id);
    public (string Chat, string Next) Text(string id) => texts.GetValueOrDefault(id, ("", ""));

    public void SetText(IEnumerable<string> ids, string chat, string next)
    {
        foreach (string id in ids) texts[id] = (chat, next);
    }

    public void Start(IEnumerable<string> ids, Simulation simulation)
    {
        foreach (string id in ids) simulations[id] = simulation;
    }

    public void Stop(IEnumerable<string> ids)
    {
        foreach (string id in ids) simulations.Remove(id);
    }

    public void Update(MixerState atem, MixerState vmix, long now)
    {
        foreach (var pager in core.Devices)
        {
            var inside = pager.Inside ?? DefaultInside;
            var outside = pager.Outside ?? DefaultOutside;
            var (chat, next) = Text(pager.Id);
            if (simulations.TryGetValue(pager.Id, out var simulation))
            {
                var (a, v, pause) = simulation.State(now);
                int Simulate(Assignment assignment) => assignment.Mixer switch
                {
                    MixerKind.Atem => a, MixerKind.Vmix => v, _ => 0
                };
                core.SetPreview(pager.Id, new(Simulate(inside), Simulate(outside), chat, next, Test: true), pause);
            }
            else core.SetPreview(pager.Id, new(Resolve(inside, atem, vmix), Resolve(outside, atem, vmix), chat, next));
        }
    }

    internal static int Resolve(Assignment assignment, MixerState atem, MixerState vmix) => assignment.Mixer switch
    {
        MixerKind.Atem => atem.Inputs.FirstOrDefault(input => input.Number == assignment.Input)?.Tally ?? 0,
        MixerKind.Vmix when assignment.Key is { Length: > 0 } key =>
            vmix.Inputs.FirstOrDefault(input => string.Equals(input.Key, key, StringComparison.OrdinalIgnoreCase))?.Tally ?? 0,
        MixerKind.Vmix => vmix.Inputs.FirstOrDefault(input => input.Number == assignment.Input)?.Tally ?? 0,
        _ => 0
    };
}
