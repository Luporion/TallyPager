namespace TallyPager;

internal sealed partial class MainWindow
{
    private readonly TableLayoutPanel manualTable = new()
    {
        AutoSize = true, Dock = DockStyle.Top, ColumnCount = 6, RowCount = 1, Margin = new Padding(0)
    };
    private readonly CheckBox allManual = new()
    {
        Text = "ATEM / vMix für alle manuell", AutoSize = true, AutoCheck = false, ThreeState = true
    };
    private readonly Dictionary<string, ManualRow> manualRows = [];
    private bool refreshingManual;
    private sealed record ManualRow(Label Name, CheckBox Atem, CheckBox Vmix, Button[] AtemStates,
        Button[] VmixStates, Button[] BeamerStates, ComboBox Display, CheckBox Live);

    private void AddManualTally(TableLayoutPanel parent)
    {
        Heading(parent, "Tally und Anzeige");
        Full(parent, allManual);
        allManual.Click += (_, _) =>
        {
            session.SetAllManual(allManual.CheckState != CheckState.Checked);
            RefreshManualTally();
        };
        Full(parent, new Label
        {
            AutoSize = true, MaximumSize = new Size(820, 0),
            Text = "„Manuell“ ersetzt die jeweilige Mixerquelle für diesen Stick. Beamer wird direkt geschaltet. " +
                "Innen/Außen folgen der Quellenzuordnung. Displayfunktionen benötigen Firmware 0.9.0. Simulationen haben Vorrang."
        });
        foreach (int width in new[] { 105, 205, 205, 112, 128, 88 })
            manualTable.ColumnStyles.Add(new(SizeType.Absolute, width));
        string[] headers = ["TallyPager", "ATEM", "vMix", "Beamer", "Großer Text", "Live-Inputs"];
        manualTable.RowStyles.Add(new(SizeType.AutoSize));
        for (int i = 0; i < headers.Length; i++)
            manualTable.Controls.Add(new Label { Text = headers[i], AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, i, 0);
        Full(parent, manualTable);
        RefreshManualTally();
    }

    private void RefreshManualTally()
    {
        refreshingManual = true;
        try
        {
            var statuses = core.Statuses();
            foreach (var pager in statuses)
            {
                if (!manualRows.TryGetValue(pager.Id, out var row))
                {
                    string id = pager.Id;
                    var name = new Label { AutoSize = true, MaximumSize = new Size(100, 0) };
                    var a = new CheckBox { Text = "Manuell", AutoSize = true };
                    var v = new CheckBox { Text = "Manuell", AutoSize = true };
                    void Change(Func<PagerControl, PagerControl> update)
                    {
                        if (refreshingManual) return;
                        session.SetControl(id, update(session.Control(id)));
                        RefreshManualTally();
                    }
                    a.CheckedChanged += (_, _) => Change(current => current with { ManualAtem = a.Checked });
                    v.CheckedChanged += (_, _) => Change(current => current with { ManualVmix = v.Checked });
                    Button[] States(bool beamer, Action<int> set) => (beamer ? new[] { 3, 1 } : new[] { 3, 2, 1 })
                        .Select(state =>
                        {
                            var button = new Button { Text = StatesText(state), Size = new Size(state == 2 ? 70 : 50, 27),
                                Margin = new Padding(0, 0, 2, 0), Padding = Padding.Empty };
                            button.Click += (_, _) => set(state);
                            return button;
                        }).ToArray();
                    var ab = States(false, state => Change(current => current with { Atem = state }));
                    var vb = States(false, state => Change(current => current with { Vmix = state }));
                    var bb = States(true, state => Change(current => current with { Beamer = state == 3 }));
                    Control MixerCell(CheckBox toggle, Button[] buttons)
                    {
                        var cell = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
                        cell.Controls.Add(toggle);
                        cell.Controls.Add(InlineButtons(buttons));
                        return cell;
                    }
                    var display = Choices();
                    display.Width = 122;
                    display.Items.AddRange(["Regiechat", "Tally außen", "Tally innen"]);
                    display.SelectedIndexChanged += (_, _) => Change(current => current with { TextMode = display.SelectedIndex });
                    var live = new CheckBox { Text = "Anzeigen", AutoSize = true };
                    live.CheckedChanged += (_, _) => Change(current => current with { ShowLive = live.Checked });
                    int index = manualTable.RowCount++;
                    manualTable.RowStyles.Add(new(SizeType.AutoSize));
                    Control[] cells = [name, MixerCell(a, ab), MixerCell(v, vb), InlineButtons(bb), display, live];
                    for (int i = 0; i < cells.Length; i++) manualTable.Controls.Add(cells[i], i, index);
                    row = new(name, a, v, ab, vb, bb, display, live);
                    manualRows.Add(id, row);
                }
                var value = session.Control(pager.Id);
                row.Name.Text = pager.Name;
                row.Atem.Checked = value.ManualAtem;
                row.Vmix.Checked = value.ManualVmix;
                void ShowStates(Button[] buttons, bool enabled, int state)
                {
                    foreach (var button in buttons)
                    {
                        button.Enabled = enabled;
                        button.BackColor = enabled && button.Text == StatesText(state) ? Color.LightSteelBlue : SystemColors.Control;
                    }
                }
                ShowStates(row.AtemStates, value.ManualAtem, value.Atem);
                ShowStates(row.VmixStates, value.ManualVmix, value.Vmix);
                bool presentation = pager.Firmware?.Supports("presentation") == true;
                ShowStates(row.BeamerStates, presentation, value.Beamer ? 3 : 1);
                row.Display.Enabled = row.Live.Enabled = presentation;
                row.Display.SelectedIndex = value.TextMode;
                row.Live.Checked = value.ShowLive;
            }
            var settings = statuses.Select(pager => session.Control(pager.Id)).ToArray();
            allManual.CheckState = settings.Length > 0 && settings.All(value => value.ManualAtem && value.ManualVmix)
                ? CheckState.Checked : settings.Any(value => value.ManualAtem || value.ManualVmix)
                    ? CheckState.Indeterminate : CheckState.Unchecked;
            allManual.Text = allManual.CheckState == CheckState.Indeterminate
                ? "ATEM / vMix für alle manuell · einzelne Ausnahmen aktiv" : "ATEM / vMix für alle manuell";
        }
        finally { refreshingManual = false; }
    }

    private static string StatesText(int state) => state switch { 3 => "LIVE", 2 => "PREVIEW", _ => "SAFE" };
}
