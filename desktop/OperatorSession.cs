namespace TallyPager;

internal enum Scenario
{
    Manual, VmixOnOff, VmixOffline, Alternating, PagerOffline, PreviewThenProgram, PagerCheck
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
            Scenario.PagerCheck => step switch { 0 => (2, 1, false), 1 => (1, 3, false), _ => (3, 2, false) },
            _ => (Atem, Vmix, false)
        };
    }
}

internal sealed record ActiveChat(string Id, bool RequireAck, long? Deadline);
internal sealed record ClosedChat(string Device, string Id, string Result, DateTimeOffset Time);
internal sealed class OperatorSession(PilotCore core, ProfileStore? profiles = null)
{
    private readonly Dictionary<string, (string Chat, string Next)> texts = [];
    private readonly Dictionary<string, Simulation> simulations = [];
    private readonly Dictionary<string, ActiveChat> messages = [];
    private readonly Queue<ClosedChat> closed = new();
    public ClosedChat[] History => closed.ToArray();
    internal static Assignment DefaultInside { get; } = new(MixerKind.Atem, 1);
    internal static Assignment DefaultOutside { get; } = new(MixerKind.Vmix, 1);
    public bool IsTesting(string id) => simulations.ContainsKey(id);
    public (string Chat, string Next) Text(string id) => texts.GetValueOrDefault(id, ("", ""));

    public void SetText(IEnumerable<string> ids, string? chat, string? next, bool ack = false, int? ttl = null)
    {
        foreach (string id in ids)
        {
            var previous = Text(id);
            texts[id] = (chat ?? previous.Chat, next ?? previous.Next);
            if (chat is null) continue;
            Close(id, chat.Length > 0 ? "Ersetzt" : "Geleert");
            if (chat.Length > 0)
                messages[id] = new(Guid.NewGuid().ToString("N"), ack, (ttl ?? (ack ? 120 : null)) is int duration
                    ? Environment.TickCount64 + duration * 1000L : null);
        }
    }

    public ActiveChat? Message(string id) => messages.GetValueOrDefault(id);
    public void Reset()
    {
        foreach (string id in messages.Keys.ToArray()) Close(id, "Profilwechsel");
        simulations.Clear();
        texts.Clear();
    }

    private void Close(string id, string result)
    {
        if (!messages.Remove(id, out var message)) return;
        var status = core.Statuses().FirstOrDefault(p => p.Id == id && p.View.ChatId == message.Id);
        if (status?.AcknowledgedAt is not null) result = "Quittiert";
        closed.Enqueue(new(id, message.Id, result, DateTimeOffset.UtcNow));
        if (closed.Count > 100) closed.Dequeue();
    }

    public static string Delivery(PagerStatus pager) => pager.View.ChatId.Length == 0 ? "—" :
        pager.AcknowledgedAt is not null ? "Quittiert" :
        pager.View.RequireAck && pager.Online && !pager.CanAck ? "Quittierung fehlt" :
        pager.ReceivedAt is not null ? "Empfangen" : pager.SentAt is not null ? "Gesendet" : "Ausstehend";

    public void Start(IEnumerable<string> ids, Simulation simulation)
    {
        foreach (string id in ids) simulations[id] = simulation;
    }

    public bool StartPagerCheck(string id, long now)
    {
        return simulations.TryAdd(id, new(Scenario.PagerCheck, 2000, 0, 0, now));
    }

    public void Stop(IEnumerable<string> ids)
    {
        foreach (string id in ids) simulations.Remove(id);
    }

    public void Update(MixerState atem, MixerState vmix, long now, ArbiterState? arbiter = null)
    {
        foreach (var pager in core.Devices)
        {
            var role = profiles?.RoleFor(pager.Id);
            Assignment hidden = new(MixerKind.Off, 0);
            var inside = profiles is null ? pager.Inside ?? DefaultInside : role is null ? hidden : role.Inside ?? DefaultInside;
            var outside = profiles is null ? pager.Outside ?? DefaultOutside : role is null ? hidden : role.Outside ?? DefaultOutside;
            if (Message(pager.Id) is { Deadline: long deadline } && now >= deadline)
            {
                Close(pager.Id, "Abgelaufen");
                texts[pager.Id] = ("", Text(pager.Id).Next);
            }
            var (chat, next) = Text(pager.Id);
            var message = Message(pager.Id);
            string messageId = message?.Id ?? "";
            bool ack = message?.RequireAck == true;
            string insideSource = Label(inside.Mixer);
            string outsideSource = Label(outside.Mixer);
            simulations.TryGetValue(pager.Id, out var simulation);
            if (simulation is { Scenario: Scenario.PagerCheck } && now - simulation.Started >= 6000)
            {
                simulations.Remove(pager.Id);
                simulation = null;
            }
            if (simulation is not null)
            {
                var (a, v, pause) = simulation.State(now);
                if (simulation.Scenario == Scenario.PagerCheck)
                {
                    core.SetPreview(pager.Id, new(a, v, chat, next, true, ChatId: messageId, RequireAck: ack));
                    continue;
                }
                int Simulate(Assignment assignment) => assignment.Mixer switch
                {
                    MixerKind.Atem or MixerKind.ArbiterAtem => a,
                    MixerKind.Vmix or MixerKind.ArbiterVmix => v, _ => 0
                };
                core.SetPreview(pager.Id, new(Simulate(inside), Simulate(outside), chat, next, true,
                    insideSource, outsideSource, messageId, ack), pause);
            }
            else core.SetPreview(pager.Id, new(Resolve(inside, atem, vmix, arbiter), Resolve(outside, atem, vmix, arbiter),
                chat, next, false, insideSource, outsideSource, messageId, ack));
        }
    }

    internal static string Label(MixerKind kind) => kind switch
    {
        MixerKind.Atem or MixerKind.ArbiterAtem => "ATEM",
        MixerKind.Vmix or MixerKind.ArbiterVmix => "vMix",
        MixerKind.Arbiter => "Tally Arbiter",
        _ => "off"
    };

    internal static int Resolve(Assignment assignment, MixerState atem, MixerState vmix, ArbiterState? arbiter) =>
        FindInput(assignment, atem, vmix, arbiter)?.Tally ?? 0;

    internal static MixerInput? FindInput(Assignment assignment, MixerState atem, MixerState vmix, ArbiterState? arbiter) => assignment.Mixer switch
    {
        MixerKind.Atem => atem.Inputs.FirstOrDefault(input => input.Number == assignment.Input),
        MixerKind.Vmix when assignment.Key is { Length: > 0 } key =>
            vmix.Inputs.FirstOrDefault(input => string.Equals(input.Key, key, StringComparison.OrdinalIgnoreCase)),
        MixerKind.Vmix => vmix.Inputs.FirstOrDefault(input => input.Number == assignment.Input),
        MixerKind.Arbiter or MixerKind.ArbiterAtem or MixerKind.ArbiterVmix =>
            arbiter?.For(assignment.Mixer).FirstOrDefault(input => input.Key == assignment.Key),
        _ => null
    };
}
