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
    private Button? restorePager;
    private string? wifiDevice;
    private bool wifiEditing;
    private bool wifiSetupSelected;
    private long lastBasePreview;

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
        if (IsEspNow(mode) && activeRadio is { } bridge && Transport.Serves(bridge.Mode, mode))
        {
            gatewayAddress.Text = bridge.Status.RadioAddress;
            pagerChannel.Value = bridge.Status.Channel;
        }
    }

    private void AddConnectionTools()
    {
        tabs.TabPages.Add(baseTab);
        var setupBase = Grid(baseTab, "USB-Funkbasis");
        Row(setupBase, "USB-Anschluss", basePort, Action("Ports suchen", (_, _) => ScanPorts()));
        basePort.SelectedIndexChanged += (_, _) =>
        {
            baseFirmware.Text = "";
            ShowBaseModes(null);
            ShowHotspot(null);
        };
        Full(setupBase, Buttons(Action("Firmware lesen", async (_, _) => await InspectUsb(basePort, baseFirmware))));
        Full(setupBase, baseFirmware);
        Row(setupBase, "Funkmodus", baseTransport);
        Row(setupBase, "Funkkanal", baseChannel);
        baseTransport.SelectedIndexChanged += (_, _) =>
            baseChannel.Enabled = IsEspNow(SelectedTransport(baseTransport)) || Transport.Serves(SelectedTransport(baseTransport), Transport.Hotspot);
        baseTransport.SelectedIndex = 0;
        baseLink = Action("Funkbasis verbinden", async (_, _) => await ConnectBase());
        restorePager = Action("Wieder als Pager starten", async (_, _) => await ConfigureBase(true));
        Full(setupBase, Buttons(Action("Funkbasis einrichten", async (_, _) => await ConfigureBase(false)),
            baseLink, restorePager));
        Full(setupBase, baseStatus);
        AddHotspotSetup(setupBase);
        Heading(setupBase, "ESP8266-Funkbasis");
        Full(setupBase, Buttons(Action("ESP8266-Firmware flashen", async (_, _) => await FlashEsp8266())));
        Full(setupBase, new Label
        {
            AutoSize = true, MaximumSize = new Size(700, 0),
            Text = "ESP8266MOD mit 4 MB Flash. Nach dem Flashen stehen oben ESP-NOW, WLAN-Brücke und beide zusammen zur Wahl. " +
                "Mit WLAN-Brücke Kanal 1, 6 oder 11 wählen. Im Kombimodus verwenden beide Funkwege diesen Kanal."
        });
        AddWindowsBluetoothControls(setupBase);
    }

    private async Task FlashEsp8266()
    {
        if (busy || changingModes) return;
        if (basePort.SelectedItem is not string port || activeRadio?.Port == port)
        {
            baseStatus.Text = "Einen freien USB-Port für den ESP8266 auswählen.";
            return;
        }
        string package = Path.Combine(AppContext.BaseDirectory, PagerUsb.Esp8266FirmwareFileName);
        if (!File.Exists(package))
        {
            using var picker = new OpenFileDialog { Title = "ESP8266-Firmwarepaket auswählen", Filter = "Firmwarepaket (*.zip)|*.zip" };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            package = picker.FileName;
        }
        if (MessageBox.Show(this, $"Die Funkbasis-Firmware ersetzt das vorhandene Programm auf dem ESP8266 an {port}. Jetzt flashen?",
                "ESP8266 flashen", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        busy = true;
        try
        {
            int channel = (int)baseChannel.Value;
            baseStatus.Text = "ESP8266 und Flashgröße werden geprüft …";
            await PagerUsb.Flash(port, package, new Progress<string>(value => baseStatus.Text = value), FirmwareTarget.Esp8266);
            var status = await Task.Run(() => PagerUsb.ReadStatus(port, TimeSpan.FromSeconds(8)));
            if (status.Board != "esp8266" || status.Version != PagerUsb.Version || !RadioBridge.CanConnect(status))
                throw new InvalidOperationException("Die ESP8266-Funkbasis hat den Neustart nicht bestätigt.");
            if (status.Mode == "espnow" && status.Channel != channel)
                status = await Task.Run(() => PagerUsb.ConfigureBase(port, status.Id, "espnow", channel));
            ShowUsbFirmware(status, baseFirmware);
            baseStatus.Text = "ESP8266 installiert. Oben Funkmodus und Kanal wählen, einrichten und verbinden.";
        }
        catch (Exception error) { baseStatus.Text = error.Message; }
        finally { busy = false; }
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
        Row(remote, "Adresse des Basis-Servers", wifiHost);
        Row(remote, "Neuer WLAN-Name", wifiNetwork);
        Row(remote, "Neues WLAN-Passwort", wifiPassword, Reveal(wifiPassword));
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
        var info = status.Firmware;
        label.Text = status.BoardName + " · " + (info?.Description ?? "Unbekannte Firmware.");
        if ((status.Board != "m5stick-cplus2" || status.Mode == Transport.Hotspot) && info is { Available.Length: > 0 })
            label.Text = $"{status.BoardName} · Firmware {info.ShortVersion}\nUnterstützt · {string.Join(", ", info.Available)}";
        if (status.Board != "m5stick-cplus2" && info is { Unknown.Length: > 0 })
            label.Text += "\nGemeldete Funktionen · " + string.Join(", ", info.Unknown);
        if (label == baseFirmware)
        {
            ShowBaseModes(status);
            ShowHotspot(status);
            if (status.BaseRole && status.Channel is >= 1 and <= 13) baseChannel.Value = status.Channel;
        }
        if (status.Firmware is { Older: true })
            label.Text += "\nFirmware-Update kann separat gestartet werden.";
    }

    private void ShowBaseModes(UsbStatus? status)
    {
        string previous = SelectedTransport(baseTransport);
        string[] possible = status?.Board == "esp8266" ? ["espnow", Transport.Hotspot, Transport.Hybrid] :
            ["espnow", "espnow-lr", "ble"];
        string[] modes = possible
            .Where(mode => status is null || status.Firmware?.AllowsBase(mode) == true).ToArray();
        baseTransport.Items.Clear();
        baseTransport.Items.AddRange(modes.Select(mode => new ModeChoice(mode)).ToArray());
        baseTransport.Enabled = modes.Length > 0;
        if (modes.Length > 0)
            baseTransport.SelectedIndex = Math.Max(0, Array.IndexOf(modes, status is { BaseRole: true } ? status.Mode : previous));
        if (restorePager is not null)
            restorePager.Enabled = status is null || (status.Board == "m5stick-cplus2" &&
                status.Firmware?.Supports(FirmwareInfo.RadioBase) == true);
    }

    private async Task<UsbStatus> ReadUsbFirmware(string port, Label label)
    {
        if (activeRadio?.Port == port)
        {
            ShowUsbFirmware(activeRadio.Status, label);
            return activeRadio.Status;
        }
        var status = await Task.Run(() => PagerUsb.ReadStatus(port, TimeSpan.FromSeconds(4)));
        if (label != baseFirmware || Equals(basePort.SelectedItem, port)) ShowUsbFirmware(status, label);
        return status;
    }

    private async Task InspectUsb(ComboBox picker, Label label)
    {
        if (busy || changingModes) return;
        if (picker.SelectedItem is not string port) { label.Text = "USB-Port auswählen."; return; }
        busy = true;
        try { await ReadUsbFirmware(port, label); }
        catch (TimeoutException)
        {
            label.Text = "Chip und Flashgröße werden gelesen …";
            try { label.Text = await PagerUsb.IdentifyHardware(port); }
            catch (Exception error) { label.Text = error.Message; }
        }
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
            if (result.Version != PagerUsb.Version || result.Mode == Transport.Hotspot)
                throw new InvalidOperationException("Firmwareversion nach Neustart nicht bestätigt.");
            ShowUsbFirmware(result, usbFirmware);
            usbStatus.Text = $"Firmware {result.Version} installiert.";
        }
        catch (Exception error) { usbStatus.Text = error.Message; }
        finally { busy = false; }
    }

    private async Task ConnectBase()
    {
        if (busy || changingModes) return;
        if (activeRadio is not null) { DisconnectBase("Funkbasis getrennt."); return; }
        if (basePort.SelectedItem is not string port) { baseStatus.Text = "USB-Port der Funkbasis auswählen."; return; }
        busy = true;
        basePort.Enabled = false;
        try
        {
            baseStatus.Text = "Funkbasis wird geprüft …";
            var bridge = await Task.Run(() => RadioBridge.Connect(port));
            if (bridge.Mode == "ble" && windowsBluetooth is not null)
            {
                bridge.Dispose();
                baseStatus.Text = "BLE direkt über Windows zuerst trennen.";
                return;
            }
            activeRadio = bridge;
            core.AttachRadio(activeRadio);
            baseLink!.Text = "Funkbasis trennen";
            var baseInfo = activeRadio.Status;
            ShowUsbFirmware(baseInfo, baseFirmware);
            baseStatus.Text = $"{port} · {Transport.Name(baseInfo.Mode)} verbunden" +
                (Transport.Serves(baseInfo.Mode, Transport.Hotspot) ? $" · {baseInfo.HotspotSsid}" : "") +
                (baseInfo.RadioAddress.Length != 0 ? $" · Funkadresse {Transport.DisplayAddress(baseInfo.RadioAddress)}" : "") +
                (baseInfo.Mode != "ble" ? $" · Kanal {baseInfo.Channel}" : "");
            core.Diagnostics.Add("USB-Funkbasis", "Basis verbunden", Transport.Name(baseInfo.Mode));
            UpdateProvisionFields();
        }
        catch (Exception error) { baseStatus.Text = error.Message; }
        finally { basePort.Enabled = true; busy = false; }
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
        if (busy || changingModes) return;
        if (basePort.SelectedItem is not string port) { baseStatus.Text = "USB-Port der Funkbasis auswählen."; return; }
        if (activeRadio?.Port == port) DisconnectBase("Funkbasis getrennt.");
        busy = true;
        try
        {
            string mode = SelectedTransport(baseTransport);
            int channel = (int)baseChannel.Value;
            var current = await ReadUsbFirmware(port, baseFirmware);
            if (restore ? current.Board != "m5stick-cplus2" || current.Firmware?.Supports(FirmwareInfo.RadioBase) != true :
                current.Firmware?.AllowsBase(mode) != true)
            {
                baseStatus.Text = $"Firmware {current.Version} bietet diese Rollenänderung nicht an.";
                return;
            }
            var status = await Task.Run(() => restore ? PagerUsb.RestorePager(port, current.Id) :
                PagerUsb.ConfigureBase(port, current.Id, mode, channel));
            ShowUsbFirmware(status, baseFirmware);
            baseStatus.Text = restore ? $"Pager im Modus {Transport.Name(status.Mode)} gestartet." :
                $"{Transport.Name(status.Mode)} bereit. Jetzt Funkbasis verbinden.";
        }
        catch (Exception error) { baseStatus.Text = error.Message; }
        finally { busy = false; }
    }

    private PagerStatus? SelectedPager()
    {
        return SelectedIds() is [var id] ? core.Statuses().FirstOrDefault(item => item.Id == id) : null;
    }

    private void RefreshConnectionTools()
    {
        RefreshWindowsBluetooth();
        if (remoteModesDialog is { Visible: true }) RefreshRemoteModes();
        if (activeRadio?.Failure is { } failure) DisconnectBase("USB-Funkbasis getrennt · " + failure);
        if (activeRadio is { } bridge && Environment.TickCount64 - lastBasePreview >= 1000)
        {
            lastBasePreview = Environment.TickCount64;
            bridge.SendBaseView(core.Statuses());
        }
        if (tabs.SelectedTab != setup || !wifiSetupSelected) return;
        var selected = SelectedPager();
        wifiDetails.Text = selected is { Settings: { } settings }
            ? $"{settings.Name} · Stick-IP {settings.StickIp} · Firmware {settings.Version} · WLAN {(settings.Ssid.Length > 0 ? settings.Ssid : "gespeichert")}"
            : "Einen erreichbaren, gepaarten WLAN-Stick mit Firmware 0.6.0 oder neuer auswählen.";
        wifiApply.Enabled = !wifiEditing && selected is { Settings: not null } && selected.Id == wifiDevice;
    }

    private void LoadWifi()
    {
        if (wifiEditing) return;
        if (SelectedPager() is not { Settings: { } settings } selected)
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
        if (wifiEditing || SelectedPager() is not { Settings: not null } selected || selected.Id != wifiDevice) return;
        string host = wifiHost.Text.Trim();
        if (WifiManagement.ValidHost(host)) host = System.Net.IPAddress.Parse(host).ToString();
        var change = new WifiChange(wifiName.Text.Trim(), host, wifiNetwork.Text, wifiPassword.Text);
        if (!WifiManagement.Valid(change))
        {
            wifiStatus.Text = "Namen, Basis-Server-Adresse oder WLAN-Daten prüfen.";
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
