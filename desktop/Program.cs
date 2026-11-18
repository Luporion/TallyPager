using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Diagnostics;

namespace TallyPager;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            if (args is ["--wait-for-tallypager", var pidText, var startedText] &&
                int.TryParse(pidText, out var pid) && long.TryParse(startedText, out var ticks))
                WaitForPreviousApp(pid, ticks);
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

    private static void WaitForPreviousApp(int pid, long ticks)
    {
        try
        {
            using var previous = Process.GetProcessById(pid);
            if (previous.StartTime.ToUniversalTime().Ticks == ticks && !previous.WaitForExit(20000))
                throw new TimeoutException("Die bisherige TallyPager-App läuft noch. Bitte schließen und die neue EXE öffnen.");
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }
}

internal sealed partial class MainWindow : Form
{
    private static readonly string[] States = ["Unbekannt", "Inaktiv", "Preview", "Program"];
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
    private readonly TextBox vmixHost = new() { PlaceholderText = "vMix-IP, Hostname oder Web-URL" };
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
    private readonly Button connectAtem = Action("Verbinden"), connectVmix = Action("Verbinden"), connectArbiter = Action("Verbinden");
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage overview = new("Regie");
    private readonly TabPage sources = new("Externe Verbindungen");
    private readonly TabPage simulation = new("Simulation");
    private readonly TabPage setup = new("Stick einrichten/verwalten");
    private readonly Label selectedFirmware = Notice();
    private readonly PagerPreview pagerPreview = new() { Visible = false };
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
        profiles = new(core.StorageDirectory, core.Devices);
        session = new(core, profiles);
        Text = "TallyPager " + PagerUsb.Version;
        Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(970, 740);
        MinimumSize = new Size(710, 620);
        BackColor = Color.FromArgb(241, 245, 249);
        ForeColor = Color.FromArgb(22, 34, 49);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 165));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        header.ColumnStyles.Add(new(SizeType.Percent, 100));
        header.ColumnStyles.Add(new(SizeType.AutoSize));
        header.Controls.Add(new Label
        {
            Text = "TallyPager", Font = new Font("Segoe UI", 19, FontStyle.Bold), AutoSize = true,
            Margin = new Padding(1, 0, 0, 9)
        }, 0, 0);
        header.Controls.Add(Action("Updates", (_, _) => OpenUpdates()), 1, 0);
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(devices, 0, 1);
        selectedFirmware.MaximumSize = new Size(800, 0);
        selectedFirmware.Margin = new Padding(3, 3, 3, 7);
        layout.Controls.Add(selectedFirmware, 0, 2);
        layout.Controls.Add(tabs, 0, 3);
        layout.Controls.Add(NetworkFooter(), 0, 4);
        Controls.Add(layout);

        foreach (string column in new[] { "Stick", "Modus", "Innen", "Außen", "Verbindung", "WLAN", "RTT", "Akku", "Anzeige", "Chat", "Firmware", "Adresse" })
        {
            int index = devices.Columns.Add(column, column);
            devices.Columns[index].SortMode = DataGridViewColumnSortMode.NotSortable;
        }
        devices.Columns[0].FillWeight = 145;
        devices.Columns[4].FillWeight = 115;
        devices.Columns[6].FillWeight = 65;
        devices.Columns[7].FillWeight = 60;
        devices.Columns["Firmware"]!.FillWeight = 120;
        devices.Columns["Adresse"]!.FillWeight = 165;
        devices.Columns["Adresse"]!.MinimumWidth = 122;

        tabs.TabPages.AddRange([overview, sources, simulation, setup]);
        AddOverview();
        AddSources();
        AddSimulation();
        AddSetup();
        AddConnectionTools();
        AddRegieTools();
        LoadSources();
        ConnectProfileSources();
        ScanPorts();
        RefreshNetworkFooter();
        string localFirmware = Path.Combine(AppContext.BaseDirectory, PagerUsb.FirmwareFileName);
        if (File.Exists(localFirmware)) firmware.Text = localFirmware;
        RefreshRows();
        if (rows.Count == 0) tabs.SelectedTab = setup;
        devices.SelectionChanged += (_, _) => SelectionChanged();
        SelectionChanged();
        timer.Tick += (_, _) => UpdateNetwork();
        core.Diagnostics.Add("PC", "Anwendung", $"TallyPager {PagerUsb.Version} gestartet");
        timer.Start();
        FormClosing += (_, e) => { if (busy) e.Cancel = true; };
        FormClosed += (_, _) =>
        {
            timer.Dispose();
            core.Diagnostics.Add("PC", "Anwendung", "TallyPager beendet");
            atem?.Dispose();
            vmix?.Dispose();
            arbiter?.Dispose();
            companion?.Dispose();
        };
    }

    private void AddOverview()
    {
        var grid = Grid(overview, "Chat und Next");
        Full(grid, Buttons(Action("Regiecheck", (_, _) => OpenRegieCheck())));
        Full(grid, new Label { AutoSize = true, Text = "Oben einen oder mehrere Sticks auswählen." });
        Row(grid, "Chat", chat, InlineButtons(Action("Leeren", (_, _) => SendText(true, false, clear: true)),
            Action("Senden", (_, _) => SendText(true, false))));
        Row(grid, "Next", next, InlineButtons(Action("Leeren", (_, _) => SendText(false, true, clear: true)),
            Action("Senden", (_, _) => SendText(false, true))));
        Full(grid, Buttons(Action("Chat und Next senden", (_, _) => SendText(true, true)),
            Action("Chat und Next leeren", (_, _) => SendText(true, true, clear: true))));
        Full(grid, chatStatus);
        Full(grid, requireManualAck);
        var showPreview = new CheckBox { Text = "Stick-Vorschau anzeigen", AutoSize = true };
        showPreview.CheckedChanged += (_, _) => pagerPreview.Visible = showPreview.Checked;
        Full(grid, showPreview);
        Full(grid, pagerPreview);
        Row(grid, "Schnelltext", favorite, InlineButtons(Action("Übernehmen", (_, _) => LoadQuick()),
            Action("Direkt senden", (_, _) =>
            {
                if (favorite.SelectedItem is QuickText text) chatStatus.Text = SendQuick(text).Message;
            })));
    }

    private void AddSources()
    {
        var grid = Grid(sources, "Mixer und Zuordnung");
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
        Row(grid, "Innen - Input", insideInput);
        Row(grid, "Außen · Quelle", outsideSource);
        Row(grid, "Außen - Input", outsideInput);
        insideInput.TextUpdate += (_, _) => FilterVmixInputs(insideSource, insideInput);
        outsideInput.TextUpdate += (_, _) => FilterVmixInputs(outsideSource, outsideInput);
        Full(grid, Buttons(Action("Innen und außen tauschen", (_, _) => Swap()),
            Action("Zuordnung speichern", (_, _) => SaveMapping())));
        Full(grid, mappingStatus);
        AddCompanion(grid);
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
        var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.White };
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        setup.Controls.Add(page);
        var header = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(20, 9, 0, 0) };
        var method = Choices();
        method.AccessibleName = "Verbindungsart";
        method.Width = 175;
        method.Items.AddRange(["USB", "WLAN", "Funkmodus wechseln"]);
        header.Controls.Add(new Label { Text = "Verbindungsart", AutoSize = true, Margin = new Padding(0, 7, 12, 0) });
        header.Controls.Add(method);
        page.Controls.Add(header, 0, 0);
        var content = new Panel { Dock = DockStyle.Fill };
        var usb = new Panel { Dock = DockStyle.Fill };
        var wireless = new Panel { Dock = DockStyle.Fill };
        var remote = new Panel { Dock = DockStyle.Fill };
        content.Controls.AddRange([remote, wireless, usb]);
        page.Controls.Add(content, 0, 1);
        method.SelectedIndexChanged += (_, _) =>
        {
            wifiSetupSelected = method.SelectedIndex == 1;
            usb.Visible = method.SelectedIndex == 0;
            wireless.Visible = wifiSetupSelected;
            remote.Visible = method.SelectedIndex == 2;
            RefreshConnectionTools();
        };

        var grid = Grid(usb, "USB-Einrichtung");
        Row(grid, "USB-Anschluss", ports, Action("Ports suchen", (_, _) => ScanPorts()));
        Full(grid, usbPortStatus);
        ports.SelectedIndexChanged += (_, _) => usbFirmware.Text = "";
        Full(grid, Buttons(Action("Firmware lesen", async (_, _) => await InspectUsb(ports, usbFirmware)),
            Action("Stick auslesen", async (_, _) => await InspectDeviceSettings())));
        Full(grid, usbFirmware);
        Row(grid, "Firmwarepaket", firmware, Action("ZIP auswählen", (_, _) => SelectFirmware()));
        Row(grid, "Funkmodus", pagerTransport);
        Row(grid, "Adresse des Basis-Servers", addresses);
        Row(grid, "Name des Sticks", pagerName);
        Row(grid, "WLAN-Name", ssid);
        Row(grid, "WLAN-Passwort", password, Reveal(password));
        Row(grid, "Adresse der Funkbasis", gatewayAddress);
        Row(grid, "ESP-NOW-Kanal", pagerChannel);
        Row(grid, "Geräteprofil am Stick", deviceSlot);
        AddSetupLibrary(grid);
        Full(grid, keepWifi);
        Full(grid, incrementName);
        Full(grid, Buttons(Action("Stick einrichten", async (_, _) => await Provision()),
            Action("Firmware aktualisieren", async (_, _) => await UpdateUsbFirmware())));
        Full(grid, usbStatus);
        usbStatus.Text = "Bei leeren WLAN-Feldern verwendet der Stick die gespeicherte Verbindung.";
        pagerTransport.SelectedIndexChanged += (_, _) => UpdateProvisionFields();
        pagerTransport.SelectedIndex = 0;
        AddWifiSetup(wireless);
        AddRemoteModes(remote);
        method.SelectedIndex = 0;
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
        string mode = SelectedTransport(pagerTransport);
        string host = addresses.Text.Trim();
        string gateway = gatewayAddress.Text.Trim().Replace(":", "").Replace("-", "").ToLowerInvariant();
        int channel = (int)pagerChannel.Value;
        if (ports.SelectedItem is not string port || mode == "udp" && !WifiManagement.ValidHost(host) ||
            IsEspNow(mode) && !Transport.IsMac(gateway))
        {
            usbStatus.Text = "Bitte USB-Port und je nach Modus PC-IPv4 oder die ESP-NOW-Basisadresse eingeben.";
            return;
        }
        if (activeRadio?.Port == port)
        {
            usbStatus.Text = "Dieser USB-Port ist mit der Funkbasis verbunden. Verbindung zuerst im Tab Funkbasis trennen.";
            return;
        }
        string name = pagerName.Text.Trim(), network = mode == "udp" ? ssid.Text : "", secret = mode == "udp" ? password.Text : "";
        if (name.Length == 0 || !ProfileStore.TextOk(name, 48) || !ProfileStore.TextOk(network, 32) ||
            !ProfileStore.TextOk(secret, 64) || (network.Length == 0 && secret.Length != 0))
        {
            usbStatus.Text = "Name und WLAN-Daten prüfen. Für gespeichertes WLAN beide WLAN-Felder leer lassen.";
            return;
        }
        busy = true;
        setup.Controls[0].Enabled = false;
        try
        {
            usbStatus.Text = $"Prüfe {port} …";
            var current = await ReadUsbFirmware(port, usbFirmware);
            if (current.Firmware?.AllowsUsbMode(mode) != true)
            {
                usbStatus.Text = $"Firmware {current.Version} bietet die USB-Einrichtung für {Transport.Name(mode)} nicht an.";
                return;
            }
            int? slot = current.Firmware.Supports("device-slots") ? (int)deviceSlot.Value - 1 : null;
            if (slot is null && deviceSlot.Value != 1)
            {
                usbStatus.Text = "Weitere Geräteprofile sind ab Firmware 0.7.0 verfügbar.";
                return;
            }
            var pager = core.Prepare(current.Id, name);
            core.Save(pager);
            usbStatus.Text = "Schreibe Einstellungen und prüfe den Neustart …";
            var result = await Task.Run(() => PagerUsb.Configure(port, pager, host, network, secret,
                mode, IsEspNow(mode) ? gateway : "", channel, slot));
            if (pager.ConfiguredTransport != result.Mode)
                core.Save(pager with { ConfiguredTransport = result.Mode });
            ShowUsbFirmware(result, usbFirmware);
            if (profiles.RoleFor(pager.Id) is null && !profiles.Unbound(profiles.Active).Any())
                profiles.Bind(pager.Id, new(Guid.NewGuid(), pager.Name, pager.Inside, pager.Outside));
            RefreshProfileChoices();
            if (!keepWifi.Checked) password.Clear();
            if (incrementName.Checked) pagerName.Text = NextDeviceName(name);
            usbStatus.Text = mode != "udp" ? $"Funkmodus {Transport.Name(result.Mode)} am Stick gespeichert." :
                result.Wifi ? $"USB-Einrichtung bestätigt. Stick-IP {result.Ip}." :
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
            if (atem is not null)
            {
                StoreSources(profiles.Active.Sources with { AtemEnabled = false });
                atem.Dispose();
                atem = null;
            }
            else if (IPAddress.TryParse(atemHost.Text, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
            {
                StoreSources(profiles.Active.Sources with { Atem = ip.ToString(), AtemEnabled = true });
                atem = new AtemSource(ip.ToString());
            }
            else { atemStatus.Text = "Bitte eine gültige ATEM-IPv4-Adresse eingeben."; return; }
            button.Text = atem is null ? "Verbinden" : "Trennen";
            atemStatus.Text = atem?.State.Status ?? MixerState.Off.Status;
        }
        catch (Exception error) { atemStatus.Text = error.Message; }
    }

    private void ToggleVmix(Button button)
    {
        if (vmix is not null)
        {
            if (!ProfileAction(() => StoreSources(profiles.Active.Sources with { VmixEnabled = false }), vmixStatus)) return;
            vmix.Dispose();
            vmix = null;
        }
        else if (VmixSource.ValidAddress(vmixHost.Text.Trim()))
        {
            if (!ProfileAction(() => StoreSources(profiles.Active.Sources with { Vmix = vmixHost.Text.Trim(), VmixEnabled = true }), vmixStatus)) return;
            vmix = new VmixSource(vmixHost.Text.Trim());
        }
        else { vmixStatus.Text = "Bitte die vMix-IP, den Rechnernamen oder die Web-URL eingeben."; return; }
        button.Text = vmix is null ? "Verbinden" : "Trennen";
        vmixStatus.Text = vmix?.State.Status ?? MixerState.Off.Status;
    }

    private void ToggleArbiter(Button button)
    {
        if (arbiter is not null)
        {
            if (!ProfileAction(() => StoreSources(profiles.Active.Sources with { ArbiterEnabled = false }), arbiterStatus)) return;
            arbiter.Dispose();
            arbiter = null;
        }
        else if (TallyArbiterSource.Endpoint(arbiterHost.Text) is { } endpoint)
        {
            if (!ProfileAction(() => StoreSources(profiles.Active.Sources with { Arbiter = arbiterHost.Text.Trim(), ArbiterEnabled = true }), arbiterStatus)) return;
            arbiter = new TallyArbiterSource(endpoint);
        }
        else { arbiterStatus.Text = "Bitte Host, Host:Port oder eine HTTP-/HTTPS-Adresse eingeben."; return; }
        button.Text = arbiter is null ? "Verbinden" : "Trennen";
        arbiterStatus.Text = arbiter?.State.Status ?? ArbiterState.Off.Status;
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
            var role = profiles.RoleFor(id);
            ShowMapping(insideSource, insideInput, role?.Inside ?? OperatorSession.DefaultInside);
            ShowMapping(outsideSource, outsideInput, role?.Outside ?? OperatorSession.DefaultOutside);
            var message = session.Text(id);
            chat.Text = message.Chat;
            next.Text = message.Next;
            mappingStatus.Text = role is null ? $"{pager.Name} · keine Kamera-Rolle gebunden." : $"{pager.Name} · {role.Name}";
            if (role is not null) roleChoice.SelectedItem = role;
        }
        else
        {
            chat.Clear();
            next.Clear();
            mappingStatus.Text = "Für die Zuordnung genau einen Stick auswählen.";
        }
        ShowSelectedFirmware();
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

    private void FilterVmixInputs(ComboBox source, ComboBox input)
    {
        if (source.SelectedIndex != (int)MixerKind.Vmix) return;
        string text = input.Text;
        int caret = input.SelectionStart;
        var matches = lastVmix.Inputs.Where(item => item.ToString().Contains(text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        input.BeginUpdate();
        input.Items.Clear();
        input.Items.AddRange(matches);
        input.Text = text;
        input.EndUpdate();
        input.DroppedDown = matches.Length > 0;
        input.SelectionStart = caret;
        input.SelectionLength = 0;
    }

    private Assignment? AssignmentFor(ComboBox source, ComboBox input)
    {
        var mixer = (MixerKind)Math.Max(0, source.SelectedIndex);
        if (mixer == MixerKind.Off) return new(mixer, 0);
        if (input.SelectedItem is MixerInput item) return new(mixer, item.Number, item.Key);
        if (input.Tag is ValueTuple<Assignment, string> retained && retained.Item1.Mixer == mixer &&
            input.Text == retained.Item2) return retained.Item1;
        if (mixer is MixerKind.Arbiter or MixerKind.ArbiterAtem or MixerKind.ArbiterVmix) return null;
        if (mixer == MixerKind.Vmix && !int.TryParse(input.Text, out _))
        {
            var named = lastVmix.Inputs.Where(value => value.Name.Equals(input.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            return named is [var match] ? new(mixer, match.Number, match.Key) : null;
        }
        if (!int.TryParse(input.Text, out int number) ||
            (mixer == MixerKind.Vmix ? number is < 1 or > 1000 : number is < 0 or > 65535)) return null;
        var listed = (mixer == MixerKind.Vmix ? lastVmix.Inputs : input.Items.OfType<MixerInput>())
            .FirstOrDefault(value => value.Number == number);
        return new(mixer, number, listed?.Key);
    }

    private void Swap()
    {
        if (mappingId is not { } id) return;
        var pager = core.Devices.First(device => device.Id == id);
        var role = profiles.RoleFor(id);
        var inside = AssignmentFor(insideSource, insideInput) ?? role?.Inside ?? OperatorSession.DefaultInside;
        var outside = AssignmentFor(outsideSource, outsideInput) ?? role?.Outside ?? OperatorSession.DefaultOutside;
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
        ProfileAction(() =>
        {
            var role = profiles.RoleFor(pager.Id) ?? new CameraRole(Guid.NewGuid(), pager.Name, null, null);
            profiles.Bind(pager.Id, role with { Inside = inside, Outside = outside });
            RefreshProfileChoices();
            mappingStatus.Text = $"Zuordnung für {role.Name} gespeichert.";
        }, mappingStatus);
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Enter && tabs.SelectedTab == overview && (chat.Focused || next.Focused))
        {
            SendText(chat.Focused, next.Focused);
            return true;
        }
        return base.ProcessCmdKey(ref message, keyData);
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
        session.SetText(ids, message, upcoming, requireManualAck.Checked);
        if (clear && updateChat) chat.Clear();
        if (clear && updateNext) next.Clear();
        string fields = updateChat && updateNext ? "Chat und Next" : updateChat ? "Chat" : "Next";
        chatStatus.Text = $"{fields} für {ids.Length} Stick(s) {(clear ? "geleert" : "in die Warteschlange gestellt")}.";
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
            if (lastTick != 0 && now - lastTick >= 350)
                core.Diagnostics.Add("PC", "Sendezyklus", $"Pause {now - lastTick} ms");
            lastTick = now;
            session.Update(a, v, now, ta);
            core.Tick();
            if (now - lastUi < 250) return;
            lastUi = now;
            if (now - lastPortScan >= 1000) ScanPorts();
            if (now - lastAdapterScan >= 5000) RefreshNetworkFooter();
            UpdateMixerUi(a, v, ta);
            RefreshRows();
            RefreshDiagnostics(v);
            RefreshConnectionTools();
        }
        catch (SocketException) { chatStatus.Text = "Netzwerkfehler. Bitte LAN und UDP-Port 46224 prüfen."; }
    }

    private void UpdateMixerUi(MixerState a, MixerState v, ArbiterState ta)
    {
        if (ReferenceEquals(a, lastAtem) && ReferenceEquals(v, lastVmix) && ReferenceEquals(ta, lastArbiter)) return;
        LogMixer("ATEM", atemStatus, lastAtem.Status, a.Status);
        LogMixer("vMix", vmixStatus, lastVmix.Status, v.Status);
        LogMixer("Tally Arbiter", arbiterStatus, lastArbiter.Status, ta.Status);
        bool listChanged = !SameInputs(a.Inputs, lastAtem.Inputs) || !SameInputs(v.Inputs, lastVmix.Inputs) ||
            !SameInputs(ta.Devices, lastArbiter.Devices) || !SameInputs(ta.Atem, lastArbiter.Atem) ||
            !SameInputs(ta.Vmix, lastArbiter.Vmix);
        lastAtem = a;
        lastVmix = v;
        lastArbiter = ta;
        if (listChanged && mappingId is { } id)
        {
            var role = profiles.RoleFor(id);
            if (!insideInput.Focused && !insideInput.DroppedDown)
                ShowMapping(insideSource, insideInput, AssignmentFor(insideSource, insideInput) ??
                    role?.Inside ?? OperatorSession.DefaultInside);
            if (!outsideInput.Focused && !outsideInput.DroppedDown)
                ShowMapping(outsideSource, outsideInput, AssignmentFor(outsideSource, outsideInput) ??
                    role?.Outside ?? OperatorSession.DefaultOutside);
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
            if (deviceConnections.GetValueOrDefault(status.Id) != status.Connection)
            {
                core.Diagnostics.Add(status.Name, "Stick-Verbindung", $"{connection} · {Transport.Name(status.Transport)}, letzte Telemetrie vor {status.TelemetryAgeMs?.ToString() ?? "—"} ms, " +
                    $"RTT {status.Rtt?.ToString() ?? "—"} ms, RSSI {status.Rssi?.ToString() ?? "—"} dBm");
                deviceConnections[status.Id] = status.Connection;
            }
            string[] values = [status.Name, (session.IsTesting(status.Id) ? "TEST" : "") +
                (session.IsTesting(status.Id) ? " · " : "") + Transport.Name(status.Transport),
                LayerStatus(status.Online, status.View.Inside, status.View.InsideSource),
                LayerStatus(status.Online, status.View.Outside, status.View.OutsideSource),
                connection, status.Rssi is int rssi ? $"{WifiBars(rssi)}/4 · {rssi} dBm" : "—",
                status.Rtt is int rtt ? $"{rtt} ms" : "—", status.Online && status.Battery is int battery ? $"{battery} %" : "—",
                status.PreviewReceived ? "Bestätigt" : "—", OperatorSession.Delivery(status),
                status.Firmware?.ShortVersion ?? "—", status.Address ?? "—"];
            for (int i = 0; i < values.Length; i++)
                if (!Equals(row.Cells[i].Value, values[i])) row.Cells[i].Value = values[i];
            row.Cells[4].Style.ForeColor = status.Connection switch
            {
                Connection.Connected => Color.DarkGreen, Connection.Delayed => Color.SaddleBrown, _ => Color.DimGray
            };
            row.Cells["Firmware"].ToolTipText = status.Firmware?.Description ?? "Firmware noch nicht gemeldet.";
            row.Cells["Adresse"].Style.ForeColor = status.LastKnownAddress ? Color.DimGray : devices.ForeColor;
            row.Cells["Adresse"].ToolTipText = status.LastKnownAddress ? "Zuletzt gemeldete Adresse" :
                status.Transport == "udp" ? "IPv4-Adresse des Sticks" : "Funkadresse des Sticks";
        }
        ShowSelectedFirmware();
    }

    private void ShowSelectedFirmware()
    {
        var status = SelectedIds() is [var id] ? core.Statuses().FirstOrDefault(pager => pager.Id == id) : null;
        pagerPreview.Update(status);
        string text = status is null ? "" : status.Firmware is { } firmware
            ? (status.Online || status.Settings is not null ? "" : "Zuletzt erkannt · ") + firmware.Description
            : "Firmware noch nicht gemeldet. Die genaue Version lässt sich per USB ablesen.";
        if (selectedFirmware.Text != text) selectedFirmware.Text = text;
        selectedFirmware.Visible = text.Length != 0;
    }

    private static string LayerStatus(bool online, int state, string source) =>
        source == "off" ? "Aus" : online ? States[state] : "—";

    private static int WifiBars(int rssi) => rssi switch { >= -55 => 4, >= -67 => 3, >= -75 => 2, >= -82 => 1, _ => 0 };

    private void ScanPorts()
    {
        lastPortScan = Environment.TickCount64;
        var found = SerialPort.GetPortNames().Order().ToArray();
        if (knownPorts.SequenceEqual(found)) return;
        var removed = knownPorts.Except(found).ToArray();
        var added = found.Except(knownPorts).ToArray();
        usbPortStatus.Text = string.Join(" · ", added.Select(port => $"{port} verbunden")
            .Concat(removed.Select(port => $"{port} getrennt")));
        knownPorts = found;
        foreach (var picker in new[] { ports, basePort })
        {
            string? previous = picker.SelectedItem as string;
            picker.Items.Clear();
            picker.Items.AddRange(found);
            if (previous is not null && picker.Items.Contains(previous)) picker.SelectedItem = previous;
            else if (picker.Items.Count == 1) picker.SelectedIndex = 0;
            else if (added is [var onlyNew]) picker.SelectedItem = onlyNew;
        }
    }

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

    private static TableLayoutPanel Grid(Control page, string title)
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
        Heading(grid, title);
        return grid;
    }

    private static void Heading(TableLayoutPanel grid, string title) =>
        Full(grid, new Label { Text = title, AutoSize = true, Font = new Font("Segoe UI", 14, FontStyle.Bold) });

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
