namespace TallyPager;

internal sealed partial class MainWindow
{
    private sealed record ModeChoice(string Id)
    {
        public override string ToString() => Transport.Name(Id);
    }

    private readonly ComboBox pagerTransport = ModePicker(true), baseTransport = ModePicker(false), basePort = Choices();
    private readonly TextBox gatewayAddress = new() { MaxLength = 17 };
    private readonly NumericUpDown pagerChannel = ChannelInput(), baseChannel = ChannelInput();
    private readonly TabPage baseTab = new("Einstellungen Funkbasis-Modus");
    private readonly Label baseStatus = Notice(), wifiStatus = Notice(), wifiDetails = Notice();
    private readonly Label usbFirmware = Notice(), baseFirmware = Notice();
    private readonly TextBox wifiName = new() { MaxLength = 48 }, wifiHost = new() { MaxLength = 15 };
    private readonly TextBox wifiNetwork = new() { MaxLength = 32, PlaceholderText = "Leer lassen, um das WLAN zu behalten" };
    private readonly TextBox wifiPassword = new() { MaxLength = 64, UseSystemPasswordChar = true };
    private readonly Button wifiApply = Action("Am Stick speichern");
    private RadioBridge? activeRadio;
    private Button? baseLink;
    private string? wifiDevice;
    private bool wifiEditing;
    private bool wifiSetupSelected;

    private static ComboBox ModePicker(bool includeUdp)
    {
        var box = Choices();
        if (includeUdp) box.Items.Add(new ModeChoice("udp"));
        foreach (string mode in new[] { "espnow", "espnow-lr", "ble" }) box.Items.Add(new ModeChoice(mode));
        return box;
    }
    private static NumericUpDown ChannelInput() => new() { Minimum = 1, Maximum = 13, Value = 6 };
    private static string SelectedTransport(ComboBox box) => (box.SelectedItem as ModeChoice)?.Id ?? "udp";
    private static bool IsEspNow(string mode) => mode is "espnow" or "espnow-lr";

    private void UpdateProvisionFields()
    {
        string mode = SelectedTransport(pagerTransport);
        addresses.Enabled = ssid.Enabled = password.Enabled = mode == "udp";
        gatewayAddress.Enabled = pagerChannel.Enabled = IsEspNow(mode);
        if (IsEspNow(mode) && activeRadio?.Mode == mode)
        {
            gatewayAddress.Text = activeRadio.Status.RadioAddress;
            pagerChannel.Value = activeRadio.Status.Channel;
        }
    }

    private void AddConnectionTools()
    {
        tabs.TabPages.Add(baseTab);
        var setupBase = Grid(baseTab, "USB-Funkbasis");
        Row(setupBase, "USB-Anschluss", basePort, Action("Ports suchen", (_, _) => ScanPorts()));
        basePort.SelectedIndexChanged += (_, _) => baseFirmware.Text = "";
        Full(setupBase, Buttons(Action("Firmware lesen", async (_, _) => await InspectUsb(basePort, baseFirmware))));
        Full(setupBase, baseFirmware);
        Row(setupBase, "Funkmodus", baseTransport);
        Row(setupBase, "ESP-NOW-Kanal", baseChannel);
        baseTransport.SelectedIndexChanged += (_, _) => baseChannel.Enabled = IsEspNow(SelectedTransport(baseTransport));
        baseTransport.SelectedIndex = 0;
        baseLink = Action("Funkbasis verbinden", async (_, _) => await ConnectBase());
        Full(setupBase, Buttons(Action("Stick als Funkbasis einrichten", async (_, _) => await ConfigureBase(false)),
            baseLink, Action("Wieder als Pager starten", async (_, _) => await ConfigureBase(true))));
        Full(setupBase, baseStatus);
        Full(setupBase, new Label
        {
            AutoSize = true, MaximumSize = new Size(700, 0),
            Text = "Die Funkbasis bleibt per USB am Rechner. Verbinde einen weiteren Stick zum Einrichten mit USB und wähle dort denselben Funkmodus. ESP-NOW nutzt auf beiden Sticks denselben Kanal und die Adresse der Basis. BLE sucht die Pager automatisch."
        });

    }

    private void AddWifiSetup(Control panel)
    {
        var remote = Grid(panel, "Einstellungen über WLAN ändern");
        var searchAddress = new TextBox { MaxLength = 15, PlaceholderText = "Leer lassen für automatische Suche" };
        Row(remote, "Stick-IP für Suche", searchAddress, Action("Im WLAN suchen", (_, _) =>
        {
            string ip = searchAddress.Text.Trim();
            if (ip.Length > 0 && !WifiManagement.ValidHost(ip))
            {
                wifiStatus.Text = "Bitte die IPv4-Adresse des Sticks eingeben oder das Feld leer lassen.";
                return;
            }
            try
            {
                int networks = core.DiscoverWifi(ip.Length == 0 ? null : System.Net.IPAddress.Parse(ip));
                wifiStatus.Text = networks == 0 ? "Kein passender Netzwerkadapter gefunden." :
                    "Suche nach bereits gepaarten Sticks gesendet. Oben einen Stick auswählen.";
            }
            catch (System.Net.Sockets.SocketException error) { wifiStatus.Text = error.Message; }
        }));
        Full(remote, wifiDetails);
        Full(remote, Action("Einstellungen laden", (_, _) => LoadWifi()));
        Row(remote, "Name des Sticks", wifiName);
        Row(remote, "PC-Adresse am Stick", wifiHost);
        Row(remote, "Neuer WLAN-Name", wifiNetwork);
        Row(remote, "Neues WLAN-Passwort", wifiPassword);
        wifiApply.Enabled = false;
        wifiApply.Click += async (_, _) => await ApplyWifi();
        Full(remote, wifiApply);
        Full(remote, wifiStatus);
        Full(remote, new Label
        {
            AutoSize = true, MaximumSize = new Size(700, 0),
            Text = "Wähle oben einen bereits gepaarten WLAN-Stick aus. Die WLAN-Suche erreicht ihn auch ohne aktive Tally-Verbindung. Neue Netzwerkdaten werden erst nach Aufbau der Tally-Verbindung gespeichert."
        });
    }

    private void ShowUsbFirmware(UsbStatus status, Label label)
    {
        core.ObserveUsbFirmware(status.Id, status.Firmware);
        label.Text = status.Firmware?.Description ?? "Unbekannte Firmware.";
        if (status.Firmware is { Older: true })
            label.Text += "\nFirmware-Update kann separat gestartet werden.";
    }

    private async Task<UsbStatus> ReadUsbFirmware(string port, Label label)
    {
        if (activeRadio?.Port == port)
        {
            ShowUsbFirmware(activeRadio.Status, label);
            return activeRadio.Status;
        }
        var status = await Task.Run(() => PagerUsb.ReadStatus(port, TimeSpan.FromSeconds(4)));
        ShowUsbFirmware(status, label);
        return status;
    }

    private async Task InspectUsb(ComboBox picker, Label label)
    {
        if (busy) return;
        if (picker.SelectedItem is not string port) { label.Text = "USB-Port auswählen."; return; }
        busy = true;
        try { await ReadUsbFirmware(port, label); }
        catch (Exception error) { label.Text = error.Message; }
        finally { busy = false; }
    }

    private async Task UpdateUsbFirmware()
    {
        if (busy) return;
        if (ports.SelectedItem is not string port) { usbStatus.Text = "USB-Port auswählen."; return; }
        if (activeRadio?.Port == port)
        {
            usbStatus.Text = "Vor dem Update die USB-Funkbasis trennen.";
            return;
        }
        string package = firmware.Text;
        if (!File.Exists(package))
        {
            usbStatus.Text = "Bitte das Firmwarepaket " + PagerUsb.FirmwareFileName + " auswählen.";
            return;
        }
        busy = true;
        try
        {
            UsbStatus? current = null;
            try { current = await ReadUsbFirmware(port, usbFirmware); }
            catch (TimeoutException) { }
            if (current?.Firmware is { Newer: true })
            {
                usbStatus.Text = "Der Stick hat bereits eine neuere Firmware als dieses Paket.";
                return;
            }
            if (MessageBox.Show(this, $"TallyPager {PagerUsb.Version} auf den M5StickC Plus2 an {port} schreiben?",
                "Firmware aktualisieren", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var progress = new Progress<string>(value => usbStatus.Text = value);
            await Task.Run(() => PagerUsb.Flash(port, package, progress));
            var result = await Task.Run(() => PagerUsb.ReadStatus(port, TimeSpan.FromSeconds(8)));
            if (result.Version != PagerUsb.Version)
                throw new InvalidOperationException("Firmwareversion nach Neustart nicht bestätigt.");
            ShowUsbFirmware(result, usbFirmware);
            usbStatus.Text = $"Firmware {result.Version} installiert.";
        }
        catch (Exception error) { usbStatus.Text = error.Message; }
        finally { busy = false; }
    }

    private async Task ConnectBase()
    {
        if (busy) return;
        if (activeRadio is not null) { DisconnectBase("Funkbasis getrennt."); return; }
        if (basePort.SelectedItem is not string port) { baseStatus.Text = "USB-Port der Funkbasis auswählen."; return; }
        busy = true;
        try
        {
            baseStatus.Text = "Funkbasis wird geprüft …";
            activeRadio = await Task.Run(() => RadioBridge.Connect(port));
            core.AttachRadio(activeRadio);
            baseLink!.Text = "Funkbasis trennen";
            var baseInfo = activeRadio.Status;
            ShowUsbFirmware(baseInfo, baseFirmware);
            baseStatus.Text = $"{port} · {Transport.Name(baseInfo.Mode)} · Adresse {baseInfo.RadioAddress}" +
                (IsEspNow(baseInfo.Mode) ? $" · Kanal {baseInfo.Channel}" : "");
            core.Diagnostics.Add("USB-Funkbasis", "Basis verbunden", Transport.Name(baseInfo.Mode));
            UpdateProvisionFields();
        }
        catch (Exception error) { baseStatus.Text = error.Message; }
        finally { busy = false; }
    }

    private void DisconnectBase(string notice)
    {
        if (activeRadio is { } bridge)
            core.Diagnostics.Add("USB-Funkbasis", "Basis getrennt", Transport.Name(bridge.Mode));
        core.AttachRadio(null);
        activeRadio = null;
        baseLink!.Text = "Funkbasis verbinden";
        baseStatus.Text = notice;
    }

    private async Task ConfigureBase(bool restore)
    {
        if (busy) return;
        if (basePort.SelectedItem is not string port) { baseStatus.Text = "USB-Port der Funkbasis auswählen."; return; }
        if (activeRadio?.Port == port) DisconnectBase("Funkbasis getrennt.");
        busy = true;
        try
        {
            string mode = SelectedTransport(baseTransport);
            int channel = (int)baseChannel.Value;
            var current = await ReadUsbFirmware(port, baseFirmware);
            if (restore ? current.Firmware?.Supports(FirmwareInfo.RadioBase) != true : current.Firmware?.AllowsBase(mode) != true)
            {
                baseStatus.Text = $"Firmware {current.Version} bietet diese Rollenänderung nicht an.";
                return;
            }
            var status = await Task.Run(() => restore ? PagerUsb.RestorePager(port, current.Id) :
                PagerUsb.ConfigureBase(port, current.Id, mode, channel));
            ShowUsbFirmware(status, baseFirmware);
            baseStatus.Text = restore ? $"Pager im Modus {Transport.Name(status.Mode)} gestartet." :
                $"Funkbasis bereit · Adresse {status.RadioAddress}. Jetzt Funkbasis verbinden.";
        }
        catch (Exception error) { baseStatus.Text = error.Message; }
        finally { busy = false; }
    }

    private PagerStatus? SelectedWifi()
    {
        return SelectedIds() is [var id] ? core.Statuses().FirstOrDefault(item => item.Id == id) : null;
    }

    private void RefreshConnectionTools()
    {
        if (activeRadio?.Failure is { } failure) DisconnectBase("USB-Funkbasis getrennt · " + failure);
        if (tabs.SelectedTab != setup || !wifiSetupSelected) return;
        var selected = SelectedWifi();
        wifiDetails.Text = selected is { Settings: { } settings }
            ? $"{settings.Name} · Stick-IP {settings.StickIp} · Firmware {settings.Version} · WLAN {(settings.Ssid.Length > 0 ? settings.Ssid : "gespeichert")}"
            : "Einen erreichbaren, gepaarten WLAN-Stick mit Firmware 0.6.0 oder neuer auswählen.";
        wifiApply.Enabled = !wifiEditing && selected is { Settings: not null } && selected.Id == wifiDevice;
    }

    private void LoadWifi()
    {
        if (wifiEditing) return;
        if (SelectedWifi() is not { Settings: { } settings } selected)
        {
            wifiStatus.Text = "Der ausgewählte Stick hat keine WLAN-Einstellungen gemeldet.";
            return;
        }
        wifiDevice = selected.Id;
        wifiName.Text = settings.Name;
        wifiHost.Text = settings.Host;
        wifiNetwork.Clear();
        wifiPassword.Clear();
        wifiStatus.Text = "Einstellungen geladen.";
        RefreshConnectionTools();
    }

    private async Task ApplyWifi()
    {
        if (wifiEditing || SelectedWifi() is not { Settings: not null } selected || selected.Id != wifiDevice) return;
        string host = wifiHost.Text.Trim();
        if (WifiManagement.ValidHost(host)) host = System.Net.IPAddress.Parse(host).ToString();
        var change = new WifiChange(wifiName.Text.Trim(), host, wifiNetwork.Text, wifiPassword.Text);
        if (!WifiManagement.Valid(change))
        {
            wifiStatus.Text = "Name, PC-IPv4 oder WLAN-Daten prüfen.";
            return;
        }
        wifiEditing = true;
        wifiPassword.Clear();
        wifiStatus.Text = "Änderung gesendet. Bei neuen Netzwerkdaten kann die Bestätigung etwas dauern …";
        RefreshConnectionTools();
        try
        {
            var result = await core.ChangeWifi(selected.Id, change);
            if (result.Result == "applied")
            {
                var pager = core.Devices.Single(device => device.Id == selected.Id);
                if (pager.Name != result.Name) core.Save(pager with { Name = result.Name });
                wifiStatus.Text = "Der Stick hat die Einstellungen gespeichert und bestätigt.";
            }
            else wifiStatus.Text = result.Result == "rollback" ?
                "Der Stick ist zu den bisherigen Netzwerkeinstellungen zurückgekehrt." :
                "Der Stick konnte die Einstellungen nicht speichern.";
        }
        catch (Exception error) { wifiStatus.Text = error.Message; }
        finally
        {
            wifiEditing = false;
            wifiDevice = null;
            RefreshConnectionTools();
        }
    }
}
