using System.IO.Ports;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace TallyPager;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            using var core = new PilotCore();
            using var window = new MainWindow(core);
            Application.Run(window);
        }
        catch (Exception error)
        {
            MessageBox.Show("TallyPager konnte nicht gestartet werden.\n\n" + error.Message,
                "TallyPager", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal sealed class MainWindow : Form
{
    private static readonly string[] States = ["Unbekannt", "Inaktiv", "Preview", "Program"];
    private static readonly string ArbiterAddressFile = Path.Combine(PilotCore.DataDirectory, "arbiter-address.txt");
    private readonly PilotCore core;
    private readonly OperatorSession session;
    private readonly DataGridView devices = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
        RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = Color.White, MultiSelect = true, BorderStyle = BorderStyle.FixedSingle,
        AccessibleName = "TallyPager-Geräte",
    };
    private readonly Dictionary<string, DataGridViewRow> rows = [];
    private readonly ComboBox ports = Choices();
    private readonly ComboBox addresses = Choices(editable: true);
    private readonly TextBox pagerName = new() { Text = "Mobile Kamera", MaxLength = 32 };
    private readonly TextBox ssid = new() { MaxLength = 32, PlaceholderText = "Gespeichertes WLAN verwenden" };
    private readonly TextBox password = new() { UseSystemPasswordChar = true, MaxLength = 64 };
    private readonly TextBox firmware = new() { ReadOnly = true };
    private readonly TextBox chat = new() { MaxLength = 68 };
    private readonly TextBox next = new() { MaxLength = 38 };
    private readonly TextBox atemHost = new() { PlaceholderText = "ATEM-IP-Adresse" };
    private readonly TextBox vmixHost = new() { PlaceholderText = "vMix-IP oder Hostname" };
    private readonly TextBox arbiterHost = new() { PlaceholderText = "Host oder Host:4455" };
    private readonly ComboBox insideSource = Choices();
    private readonly ComboBox outsideSource = Choices();
    private readonly ComboBox insideInput = Choices(editable: true);
    private readonly ComboBox outsideInput = Choices(editable: true);
    private readonly ComboBox scenario = Choices();
    private readonly ComboBox simulatedAtem = Choices();
    private readonly ComboBox simulatedVmix = Choices();
    private readonly NumericUpDown interval = new() { Minimum = 0.5m, Maximum = 60, Value = 3, Increment = 0.5m, DecimalPlaces = 1 };
    private readonly Label atemStatus = Notice(), vmixStatus = Notice(), arbiterStatus = Notice(), mappingStatus = Notice();
    private readonly Label usbStatus = Notice(), chatStatus = Notice(), simStatus = Notice();
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage overview = new("Regie");
    private readonly TabPage sources = new("Quellen und Zuordnung");
    private readonly TabPage simulation = new("Simulation");
    private readonly TabPage setup = new("Stick einrichten");
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 25 };
    private AtemSource? atem;
    private VmixSource? vmix;
    private TallyArbiterSource? arbiter;
    private MixerState lastAtem = MixerState.Off, lastVmix = MixerState.Off;
    private ArbiterState lastArbiter = ArbiterState.Off;
    private string? mappingId;
    private long lastUi;
    private bool busy;

    public MainWindow(PilotCore core)
    {
        this.core = core;
        session = new(core);
        Text = "TallyPager " + PagerUsb.Version;
        Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(970, 740);
        MinimumSize = new Size(710, 620);
        BackColor = Color.FromArgb(241, 245, 249);
        ForeColor = Color.FromArgb(22, 34, 49);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 165));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label
        {
            Text = "TallyPager", Font = new Font("Segoe UI", 19, FontStyle.Bold), AutoSize = true,
            Margin = new Padding(1, 0, 0, 9)
        }, 0, 0);
        layout.Controls.Add(devices, 0, 1);
        layout.Controls.Add(tabs, 0, 2);
        Controls.Add(layout);

        foreach (string column in new[] { "Stick", "Modus", "Innen", "Außen", "Verbindung", "WLAN", "RTT", "Akku", "Anzeige" })
        {
            int index = devices.Columns.Add(column, column);
            devices.Columns[index].SortMode = DataGridViewColumnSortMode.NotSortable;
        }
        devices.Columns[0].FillWeight = 145;
        devices.Columns[4].FillWeight = 115;
        devices.Columns[6].FillWeight = 65;
        devices.Columns[7].FillWeight = 60;

        tabs.TabPages.AddRange([overview, sources, simulation, setup]);
        AddOverview();
        AddSources();
        AddSimulation();
        AddSetup();
        LoadArbiterAddress();
        ScanPorts();
        foreach (string address in FindAddresses()) addresses.Items.Add(address);
        if (addresses.Items.Count > 0) addresses.SelectedIndex = 0;
        string localFirmware = Path.Combine(AppContext.BaseDirectory, PagerUsb.FirmwareFileName);
        if (File.Exists(localFirmware)) firmware.Text = localFirmware;
        RefreshRows();
        if (rows.Count == 0) tabs.SelectedTab = setup;
        devices.SelectionChanged += (_, _) => SelectionChanged();
        SelectionChanged();
        timer.Tick += (_, _) => UpdateNetwork();
        timer.Start();
        FormClosing += (_, e) => { if (busy) e.Cancel = true; };
        FormClosed += (_, _) =>
        {
            timer.Dispose();
            atem?.Dispose();
            vmix?.Dispose();
            arbiter?.Dispose();
        };
    }

    private void AddOverview()
    {
        var grid = Grid(overview, "Chat und Next");
        Full(grid, new Label { AutoSize = true, Text = "Oben einen oder mehrere Sticks auswählen." });
        Row(grid, "Chat", chat, InlineButtons(Action("Leeren", (_, _) => SendText(true, false, clear: true)),
            Action("Senden", (_, _) => SendText(true, false))));
        Row(grid, "Next", next, InlineButtons(Action("Leeren", (_, _) => SendText(false, true, clear: true)),
            Action("Senden", (_, _) => SendText(false, true))));
        chat.KeyDown += (_, e) => SendOnEnter(e, chatField: true);
        next.KeyDown += (_, e) => SendOnEnter(e, chatField: false);
        Full(grid, Buttons(Action("Chat und Next senden", (_, _) => SendText(true, true)),
            Action("Chat und Next leeren", (_, _) => SendText(true, true, clear: true))));
        Full(grid, chatStatus);
    }

    private void AddSources()
    {
        var grid = Grid(sources, "Quellen verbinden");
        var connectAtem = Action("Verbinden");
        var connectVmix = Action("Verbinden");
        var connectArbiter = Action("Verbinden");
        connectAtem.Click += (_, _) => ToggleAtem(connectAtem);
        connectVmix.Click += (_, _) => ToggleVmix(connectVmix);
        connectArbiter.Click += (_, _) => ToggleArbiter(connectArbiter);
        Row(grid, "ATEM", atemHost, connectAtem);
        atemStatus.Text = MixerState.Off.Status;
        Full(grid, atemStatus);
        Row(grid, "vMix", vmixHost, connectVmix);
        vmixStatus.Text = MixerState.Off.Status;
        Full(grid, vmixStatus);
        Row(grid, "Tally Arbiter", arbiterHost, connectArbiter);
        arbiterStatus.Text = ArbiterState.Off.Status;
        Full(grid, arbiterStatus);
        foreach (var box in new[] { insideSource, outsideSource })
            box.Items.AddRange(["Aus · ausblenden", "ATEM", "vMix", "Tally Arbiter · Gerät",
                "Tally Arbiter · ATEM", "Tally Arbiter · vMix"]);
        insideSource.SelectionChangeCommitted += (_, _) => ChangeMixer(insideSource, insideInput);
        outsideSource.SelectionChangeCommitted += (_, _) => ChangeMixer(outsideSource, outsideInput);
        Row(grid, "Innen · Quelle", insideSource);
        Row(grid, "Innen · Eingang", insideInput);
        Row(grid, "Außen · Quelle", outsideSource);
        Row(grid, "Außen · Eingang", outsideInput);
        Full(grid, Buttons(Action("Innen und außen tauschen", (_, _) => Swap()),
            Action("Zuordnung speichern", (_, _) => SaveMapping())));
        Full(grid, mappingStatus);
    }

    private void AddSimulation()
    {
        var grid = Grid(simulation, "Simulation für ausgewählte Sticks");
        scenario.Items.AddRange(["Manuelle Zustände", "vMix · Program ↔ Inaktiv", "vMix · Program ↔ Quellausfall",
            "ATEM und vMix · Program/Preview im Wechsel", "Stick-Verbindung unterbrechen", "ATEM · Preview → Program → Inaktiv"]);
        scenario.SelectedIndex = 0;
        foreach (var box in new[] { simulatedAtem, simulatedVmix })
        {
            box.Items.AddRange(States);
            box.SelectedIndex = 1;
        }
        Row(grid, "Szenario", scenario);
        Row(grid, "Intervall in Sekunden", interval);
        Row(grid, "ATEM-Zustand", simulatedAtem);
        Row(grid, "vMix-Zustand", simulatedVmix);
        Full(grid, Buttons(Action("Starten", (_, _) => Simulate()),
            Action("Auswahl stoppen", (_, _) => StopSimulation(false)),
            Action("Alle stoppen", (_, _) => StopSimulation(true))));
        Full(grid, simStatus);
    }

    private void AddSetup()
    {
        var grid = Grid(setup, "M5StickC Plus2 einrichten");
        Row(grid, "USB-Anschluss", ports, Action("Ports suchen", (_, _) => ScanPorts()));
        Row(grid, "Firmwarepaket", firmware, Action("ZIP auswählen", (_, _) => SelectFirmware()));
        Row(grid, "Adresse dieses PCs", addresses);
        Row(grid, "Name des Sticks", pagerName);
        Row(grid, "WLAN-Name", ssid);
        Row(grid, "WLAN-Passwort", password);
        Full(grid, Action("Stick einrichten", async (_, _) => await Provision()));
        Full(grid, usbStatus);
        usbStatus.Text = "Bei leeren WLAN-Feldern verwendet der Stick die gespeicherte Verbindung.";
    }

    private void SelectFirmware()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "TallyPager-Firmwarepaket auswählen", Filter = "Firmwarepaket (*.zip)|*.zip",
            InitialDirectory = AppContext.BaseDirectory, CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) firmware.Text = dialog.FileName;
    }

    private async Task Provision()
    {
        if (busy) return;
        if (ports.SelectedItem is not string port || !IPAddress.TryParse(addresses.Text, out var host) ||
            host.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(host) || host.Equals(IPAddress.Any))
        {
            usbStatus.Text = "Bitte einen COM-Port und die IPv4-Adresse des PCs im Kameranetz wählen.";
            return;
        }
        string name = pagerName.Text.Trim(), network = ssid.Text, secret = password.Text, package = firmware.Text;
        if (name.Length == 0 || Encoding.UTF8.GetByteCount(name) > 48 || Encoding.UTF8.GetByteCount(network) > 32 ||
            Encoding.UTF8.GetByteCount(secret) > 64 || (network.Length == 0 && secret.Length != 0))
        {
            usbStatus.Text = "Name und WLAN-Daten prüfen. Für gespeichertes WLAN beide WLAN-Felder leer lassen.";
            return;
        }
        busy = true;
        setup.Controls[0].Enabled = false;
        try
        {
            usbStatus.Text = $"Prüfe {port} …";
            UsbStatus? current = null;
            try { current = await Task.Run(() => PagerUsb.ReadStatus(port, TimeSpan.FromSeconds(2))); }
            catch (TimeoutException) { }
            if (current?.Version != PagerUsb.Version)
            {
                if (!File.Exists(package))
                {
                    usbStatus.Text = "Bitte das separate Firmwarepaket " + PagerUsb.FirmwareFileName + " auswählen.";
                    return;
                }
                var answer = MessageBox.Show(this, $"TallyPager {PagerUsb.Version} auf den M5StickC Plus2 an {port} schreiben?",
                    "Firmware aktualisieren", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return;
                var progress = new Progress<string>(value => usbStatus.Text = value);
                await Task.Run(() => PagerUsb.Flash(port, package, progress));
                current = await Task.Run(() => PagerUsb.ReadStatus(port, TimeSpan.FromSeconds(8)));
                if (current.Version != PagerUsb.Version) throw new InvalidOperationException("Firmware nach Neustart nicht bestätigt.");
            }
            var pager = core.Prepare(current.Id, name);
            core.Save(pager);
            usbStatus.Text = "Schreibe Einstellungen und prüfe den Neustart …";
            var result = await Task.Run(() => PagerUsb.Configure(port, pager, host.ToString(), network, secret));
            password.Clear();
            usbStatus.Text = result.Wifi ? $"USB-Einrichtung bestätigt. Stick-IP: {result.Ip}." :
                "USB-Einrichtung bestätigt. Der Stick versucht, sich mit dem WLAN zu verbinden.";
            RefreshRows();
            devices.ClearSelection();
            rows[pager.Id].Selected = true;
        }
        catch (Exception error) { usbStatus.Text = error.Message; }
        finally
        {
            setup.Controls[0].Enabled = true;
            busy = false;
        }
    }

    private void ToggleAtem(Button button)
    {
        try
        {
            if (atem is not null) { atem.Dispose(); atem = null; }
            else if (IPAddress.TryParse(atemHost.Text, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                atem = new AtemSource(ip.ToString());
            else { atemStatus.Text = "Bitte eine gültige ATEM-IPv4-Adresse eingeben."; return; }
            button.Text = atem is null ? "Verbinden" : "Trennen";
            atemStatus.Text = atem?.State.Status ?? MixerState.Off.Status;
        }
        catch (Exception error) { atemStatus.Text = error.Message; }
    }

    private void ToggleVmix(Button button)
    {
        if (vmix is not null) { vmix.Dispose(); vmix = null; }
        else if (Uri.CheckHostName(vmixHost.Text.Trim()) != UriHostNameType.Unknown)
            vmix = new VmixSource(vmixHost.Text.Trim());
        else { vmixStatus.Text = "Bitte die vMix-IP oder den Rechnernamen eingeben."; return; }
        button.Text = vmix is null ? "Verbinden" : "Trennen";
        vmixStatus.Text = vmix?.State.Status ?? MixerState.Off.Status;
    }

    private void ToggleArbiter(Button button)
    {
        if (arbiter is not null) { arbiter.Dispose(); arbiter = null; }
        else if (TallyArbiterSource.Endpoint(arbiterHost.Text) is { } endpoint)
        {
            arbiter = new TallyArbiterSource(endpoint);
            try
            {
                Directory.CreateDirectory(PilotCore.DataDirectory);
                File.WriteAllText(ArbiterAddressFile, arbiterHost.Text.Trim());
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        else { arbiterStatus.Text = "Bitte Host, Host:Port oder eine HTTP-/HTTPS-Adresse eingeben."; return; }
        button.Text = arbiter is null ? "Verbinden" : "Trennen";
        arbiterStatus.Text = arbiter?.State.Status ?? ArbiterState.Off.Status;
    }

    private void LoadArbiterAddress()
    {
        try
        {
            if (File.Exists(ArbiterAddressFile)) arbiterHost.Text = File.ReadAllText(ArbiterAddressFile);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private string[] SelectedIds() => devices.SelectedRows.Cast<DataGridViewRow>()
        .Select(row => row.Tag).OfType<string>().ToArray();

    private void SelectionChanged()
    {
        string[] selected = SelectedIds();
        mappingId = selected.Length == 1 ? selected[0] : null;
        if (mappingId is { } id)
        {
            var pager = core.Devices.First(device => device.Id == id);
            ShowMapping(insideSource, insideInput, pager.Inside ?? OperatorSession.DefaultInside);
            ShowMapping(outsideSource, outsideInput, pager.Outside ?? OperatorSession.DefaultOutside);
            var message = session.Text(id);
            chat.Text = message.Chat;
            next.Text = message.Next;
            mappingStatus.Text = pager.Name;
        }
        else
        {
            chat.Clear();
            next.Clear();
            mappingStatus.Text = "Für die Zuordnung genau einen Stick auswählen.";
        }
    }

    private void ShowMapping(ComboBox source, ComboBox input, Assignment mapping)
    {
        source.SelectedIndex = (int)mapping.Mixer;
        input.Tag = null;
        UpdateInputs(source, input);
        var entry = input.Items.OfType<MixerInput>().FirstOrDefault(value =>
            mapping.Key is null ? value.Number == mapping.Input :
                string.Equals(value.Key, mapping.Key, StringComparison.OrdinalIgnoreCase));
        if (entry is not null) input.SelectedItem = entry;
        else
        {
            input.SelectedIndex = -1;
            string value = mapping.Mixer == MixerKind.Off ? "" : mapping.Key is null ?
                mapping.Input.ToString() : $"Gespeicherter Eingang {mapping.Input}";
            input.Text = value;
            if (mapping.Key is not null) input.Tag = (Mapping: mapping, Text: value);
        }
    }

    private void ChangeMixer(ComboBox source, ComboBox input)
    {
        input.Tag = null;
        input.SelectedIndex = -1;
        input.Text = "";
        UpdateInputs(source, input);
    }

    private void UpdateInputs(ComboBox source, ComboBox input)
    {
        var previous = input.SelectedItem as MixerInput;
        string oldText = input.Text;
        input.Items.Clear();
        input.Enabled = source.SelectedIndex > 0;
        var entries = (MixerKind)source.SelectedIndex switch
        {
            MixerKind.Atem => lastAtem.Inputs,
            MixerKind.Vmix => lastVmix.Inputs,
            var mixer => lastArbiter.For(mixer)
        };
        input.Items.AddRange(entries);
        var match = entries.FirstOrDefault(value => previous is not null &&
            (previous.Key is null ? value.Number == previous.Number : value.Key == previous.Key));
        if (match is not null) input.SelectedItem = match;
        else input.Text = oldText;
    }

    private static Assignment? AssignmentFor(ComboBox source, ComboBox input)
    {
        var mixer = (MixerKind)Math.Max(0, source.SelectedIndex);
        if (mixer == MixerKind.Off) return new(mixer, 0);
        if (input.SelectedItem is MixerInput item) return new(mixer, item.Number, item.Key);
        if (input.Tag is ValueTuple<Assignment, string> retained && retained.Item1.Mixer == mixer &&
            input.Text == retained.Item2) return retained.Item1;
        if (mixer is MixerKind.Arbiter or MixerKind.ArbiterAtem or MixerKind.ArbiterVmix) return null;
        if (!int.TryParse(input.Text, out int number) ||
            (mixer == MixerKind.Vmix ? number is < 1 or > 1000 : number is < 0 or > 65535)) return null;
        var listed = input.Items.OfType<MixerInput>().FirstOrDefault(value => value.Number == number);
        return new(mixer, number, listed?.Key);
    }

    private void Swap()
    {
        if (mappingId is not { } id) return;
        var pager = core.Devices.First(device => device.Id == id);
        var inside = AssignmentFor(insideSource, insideInput) ?? pager.Inside ?? OperatorSession.DefaultInside;
        var outside = AssignmentFor(outsideSource, outsideInput) ?? pager.Outside ?? OperatorSession.DefaultOutside;
        ShowMapping(insideSource, insideInput, outside);
        ShowMapping(outsideSource, outsideInput, inside);
        SaveAssignment(pager, outside, inside);
    }

    private void SaveMapping()
    {
        if (mappingId is not { } id) { mappingStatus.Text = "Bitte genau einen Stick auswählen."; return; }
        var pager = core.Devices.First(device => device.Id == id);
        var inside = AssignmentFor(insideSource, insideInput);
        var outside = AssignmentFor(outsideSource, outsideInput);
        if (inside is null || outside is null)
        {
            mappingStatus.Text = "Bitte einen Eingang auswählen oder seine Nummer eingeben.";
            return;
        }
        SaveAssignment(pager, inside, outside);
    }

    private void SaveAssignment(Pager pager, Assignment inside, Assignment outside)
    {
        try
        {
            core.Save(pager with { Inside = inside, Outside = outside });
            mappingStatus.Text = $"Zuordnung für {pager.Name} gespeichert.";
        }
        catch (IOException error) { mappingStatus.Text = error.Message; }
    }

    private void SendOnEnter(KeyEventArgs e, bool chatField)
    {
        if (e.KeyCode != Keys.Enter || e.Modifiers != Keys.None) return;
        e.SuppressKeyPress = true;
        SendText(chatField, !chatField);
    }

    private void SendText(bool updateChat, bool updateNext, bool clear = false)
    {
        var ids = SelectedIds();
        if (ids.Length == 0) { chatStatus.Text = "Bitte mindestens einen Stick auswählen."; return; }
        string? message = updateChat ? clear ? "" : chat.Text.Trim() : null;
        string? upcoming = updateNext ? clear ? "" : next.Text.Trim() : null;
        static bool Invalid(string? value, int limit) =>
            value is not null && (Encoding.UTF8.GetByteCount(value) > limit || value.Any(char.IsControl));
        if (Invalid(message, 160) || Invalid(upcoming, 80))
        {
            chatStatus.Text = "Bitte Chat oder Next kürzen und Steuerzeichen entfernen.";
            return;
        }
        session.SetText(ids, message, upcoming);
        if (clear && updateChat) chat.Clear();
        if (clear && updateNext) next.Clear();
        string fields = updateChat && updateNext ? "Chat und Next" : updateChat ? "Chat" : "Next";
        chatStatus.Text = $"{fields} für {ids.Length} Stick(s) {(clear ? "geleert" : "gesendet")}.";
    }

    private void Simulate()
    {
        var ids = SelectedIds();
        if (ids.Length == 0) { simStatus.Text = "Bitte mindestens einen Stick auswählen."; return; }
        session.Start(ids, new Simulation((Scenario)scenario.SelectedIndex, (int)(interval.Value * 1000),
            simulatedAtem.SelectedIndex, simulatedVmix.SelectedIndex, Environment.TickCount64));
        simStatus.Text = $"Simulation läuft für {ids.Length} Stick(s).";
    }

    private void StopSimulation(bool all)
    {
        session.Stop(all ? core.Devices.Select(pager => pager.Id) : SelectedIds());
        simStatus.Text = "Simulation gestoppt. Aktuelle Mixerzustände werden wieder angezeigt.";
    }

    private void UpdateNetwork()
    {
        try
        {
            var a = atem?.State ?? MixerState.Off;
            var v = vmix?.State ?? MixerState.Off;
            var ta = arbiter?.State ?? ArbiterState.Off;
            long now = Environment.TickCount64;
            session.Update(a, v, now, ta);
            core.Tick();
            if (now - lastUi < 250) return;
            lastUi = now;
            UpdateMixerUi(a, v, ta);
            RefreshRows();
        }
        catch (SocketException) { chatStatus.Text = "Netzwerkfehler. Bitte LAN und UDP-Port 46224 prüfen."; }
    }

    private void UpdateMixerUi(MixerState a, MixerState v, ArbiterState ta)
    {
        if (ReferenceEquals(a, lastAtem) && ReferenceEquals(v, lastVmix) && ReferenceEquals(ta, lastArbiter)) return;
        if (!ReferenceEquals(a, lastAtem)) atemStatus.Text = a.Status;
        if (!ReferenceEquals(v, lastVmix)) vmixStatus.Text = v.Status;
        if (!ReferenceEquals(ta, lastArbiter)) arbiterStatus.Text = ta.Status;
        bool listChanged = !SameInputs(a.Inputs, lastAtem.Inputs) || !SameInputs(v.Inputs, lastVmix.Inputs) ||
            !SameInputs(ta.Devices, lastArbiter.Devices) || !SameInputs(ta.Atem, lastArbiter.Atem) ||
            !SameInputs(ta.Vmix, lastArbiter.Vmix);
        lastAtem = a;
        lastVmix = v;
        lastArbiter = ta;
        if (listChanged && mappingId is { } id)
        {
            var pager = core.Devices.First(device => device.Id == id);
            ShowMapping(insideSource, insideInput, AssignmentFor(insideSource, insideInput) ??
                pager.Inside ?? OperatorSession.DefaultInside);
            ShowMapping(outsideSource, outsideInput, AssignmentFor(outsideSource, outsideInput) ??
                pager.Outside ?? OperatorSession.DefaultOutside);
        }
    }

    private static bool SameInputs(MixerInput[] first, MixerInput[] second) =>
        first.Select(input => (input.Number, input.Name, input.Key))
            .SequenceEqual(second.Select(input => (input.Number, input.Name, input.Key)));

    private void RefreshRows()
    {
        foreach (var status in core.Statuses())
        {
            if (!rows.TryGetValue(status.Id, out var row))
            {
                row = devices.Rows[devices.Rows.Add()];
                row.Tag = status.Id;
                rows.Add(status.Id, row);
            }
            string connection = status.Connection switch
            {
                Connection.Connected => "Verbunden", Connection.Delayed => "Delayed", _ => "Getrennt"
            };
            string[] values = [status.Name, session.IsTesting(status.Id) ? "Simulation" : "Mixer",
                LayerStatus(status.Online, status.View.Inside, status.View.InsideSource),
                LayerStatus(status.Online, status.View.Outside, status.View.OutsideSource),
                connection, status.Rssi is int rssi ? $"{WifiBars(rssi)}/4 · {rssi} dBm" : "—",
                status.Rtt is int rtt ? $"{rtt} ms" : "—", status.Online && status.Battery is int battery ? $"{battery} %" : "—",
                status.PreviewReceived ? "Bestätigt" : "—"];
            for (int i = 0; i < values.Length; i++)
                if (!Equals(row.Cells[i].Value, values[i])) row.Cells[i].Value = values[i];
            row.Cells[4].Style.ForeColor = status.Connection switch
            {
                Connection.Connected => Color.DarkGreen, Connection.Delayed => Color.SaddleBrown, _ => Color.DimGray
            };
        }
    }

    private static string LayerStatus(bool online, int state, string source) =>
        source == "off" ? "Aus" : online ? States[state] : "—";

    private static int WifiBars(int rssi) => rssi switch { >= -55 => 4, >= -67 => 3, >= -75 => 2, >= -82 => 1, _ => 0 };

    private void ScanPorts()
    {
        string? previous = ports.SelectedItem as string;
        ports.Items.Clear();
        ports.Items.AddRange(SerialPort.GetPortNames().Order().ToArray());
        if (previous is not null && ports.Items.Contains(previous)) ports.SelectedItem = previous;
        else if (ports.Items.Count == 1) ports.SelectedIndex = 0;
    }

    private static IEnumerable<string> FindAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
        .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
        .Select(item => item.Address)
        .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
        .Select(ip => ip.ToString()).Distinct()
        .OrderByDescending(ip => ip.StartsWith("192.168.", StringComparison.Ordinal));

    private static ComboBox Choices(bool editable = false) => new()
        { DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList };

    private static Label Notice() => new() { AutoSize = true, MaximumSize = new Size(800, 0) };

    private static Button Action(string name, EventHandler? click = null)
    {
        var button = new Button { Text = name, AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
        if (click is not null) button.Click += click;
        return button;
    }

    private static FlowLayoutPanel Buttons(params Button[] buttons)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        panel.Controls.AddRange(buttons);
        return panel;
    }

    private static FlowLayoutPanel InlineButtons(params Button[] buttons)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        panel.Controls.AddRange(buttons);
        return panel;
    }

    private static TableLayoutPanel Grid(TabPage page, string title)
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(20, 12, 20, 12), ColumnCount = 3,
            AutoScroll = true, BackColor = Color.White
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 158));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        page.Controls.Add(grid);
        Full(grid, new Label { Text = title, AutoSize = true, Font = new Font("Segoe UI", 14, FontStyle.Bold) });
        return grid;
    }

    private static void Full(TableLayoutPanel grid, Control control)
    {
        int row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Margin = new Padding(0, 5, 0, 5);
        grid.Controls.Add(control, 0, row);
        grid.SetColumnSpan(control, 3);
    }

    private static void Row(TableLayoutPanel grid, string name, Control control, Control? action = null)
    {
        int row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        control.AccessibleName = name;
        control.Margin = new Padding(0, 4, 8, 4);
        control.Dock = DockStyle.Fill;
        grid.Controls.Add(control, 1, row);
        if (action is not null) grid.Controls.Add(action, 2, row);
    }
}
