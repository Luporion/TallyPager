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

    public void SetText(IEnumerable<string> ids, string? chat, string? next)
    {
        foreach (string id in ids)
        {
            var previous = Text(id);
            texts[id] = (chat ?? previous.Chat, next ?? previous.Next);
        }
    }

    public void Start(IEnumerable<string> ids, Simulation simulation)
    {
        foreach (string id in ids) simulations[id] = simulation;
    }

    public void Stop(IEnumerable<string> ids)
    {
        foreach (string id in ids) simulations.Remove(id);
    }

    public void Update(MixerState atem, MixerState vmix, long now, ArbiterState? arbiter = null)
    {
        foreach (var pager in core.Devices)
        {
            var inside = pager.Inside ?? DefaultInside;
            var outside = pager.Outside ?? DefaultOutside;
            var (chat, next) = Text(pager.Id);
            string insideSource = Label(inside.Mixer);
            string outsideSource = Label(outside.Mixer);
            if (simulations.TryGetValue(pager.Id, out var simulation))
            {
                var (a, v, pause) = simulation.State(now);
                int Simulate(Assignment assignment) => assignment.Mixer switch
                {
                    MixerKind.Atem or MixerKind.ArbiterAtem => a,
                    MixerKind.Vmix or MixerKind.ArbiterVmix => v, _ => 0
                };
                core.SetPreview(pager.Id, new(Simulate(inside), Simulate(outside), chat, next, true,
                    insideSource, outsideSource), pause);
            }
            else core.SetPreview(pager.Id, new(Resolve(inside, atem, vmix, arbiter), Resolve(outside, atem, vmix, arbiter),
                chat, next, false, insideSource, outsideSource));
        }
    }

    internal static string Label(MixerKind kind) => kind switch
    {
        MixerKind.Atem or MixerKind.ArbiterAtem => "ATEM",
        MixerKind.Vmix or MixerKind.ArbiterVmix => "vMix",
        MixerKind.Arbiter => "Tally Arbiter",
        _ => "off"
    };

    internal static int Resolve(Assignment assignment, MixerState atem, MixerState vmix, ArbiterState? arbiter) => assignment.Mixer switch
    {
        MixerKind.Atem => atem.Inputs.FirstOrDefault(input => input.Number == assignment.Input)?.Tally ?? 0,
        MixerKind.Vmix when assignment.Key is { Length: > 0 } key =>
            vmix.Inputs.FirstOrDefault(input => string.Equals(input.Key, key, StringComparison.OrdinalIgnoreCase))?.Tally ?? 0,
        MixerKind.Vmix => vmix.Inputs.FirstOrDefault(input => input.Number == assignment.Input)?.Tally ?? 0,
        MixerKind.Arbiter or MixerKind.ArbiterAtem or MixerKind.ArbiterVmix =>
            arbiter?.For(assignment.Mixer).FirstOrDefault(input => input.Key == assignment.Key)?.Tally ?? 0,
        _ => 0
    };
}
