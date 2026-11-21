namespace TallyPager;

internal sealed partial class MainWindow
{
    private readonly TextBox hotspotName = new() { ReadOnly = true };
    private readonly TextBox hotspotPassword = new() { ReadOnly = true, UseSystemPasswordChar = true };
    private readonly ComboBox hotspotChannel = Choices();
    private readonly ComboBox hotspotSecurity = Choices();
    private readonly TextBox newHotspotPassword = new()
    {
        UseSystemPasswordChar = true, MaxLength = 63, PlaceholderText = "8 bis 63 Zeichen (ASCII)"
    };
    private readonly Label hotspotStatus = Notice();
    private readonly ComboBox mumbleServer = Choices();
    private readonly Label mumbleStatus = Notice();
    private UsbStatus? hotspotInfo;
    private Button? applyHotspot, changeHotspotChannel, changeHotspotSecurity;
    private CheckBox? revealHotspotPassword, revealNewHotspotPassword;

    private sealed record SecurityChoice(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    private void AddHotspotSetup(TableLayoutPanel parent)
    {
        var grid = SetupSection(parent, "WLAN-Brücke");
        SetupHint(grid, "Kamera-Pager verwenden WLAN + UDP und den Basis-Server 192.168.4.1. Bis zu vier WLAN-Geräte können sich anmelden.");
        Full(grid, Buttons(Action("M5Stick-Brücken-Firmware auswählen …", async (_, _) =>
            await ChooseAndFlash(basePort, baseFirmware, hotspotStatus, FirmwareTarget.Hotspot))));
        Row(grid, "WLAN-Name", hotspotName);
        revealHotspotPassword = PasswordReveal(hotspotPassword);
        Row(grid, "Aktuelles WLAN-Passwort", hotspotPassword, revealHotspotPassword);
        hotspotSecurity.Items.AddRange([new SecurityChoice("automatic", "WPA2 · automatisches Passwort"),
            new SecurityChoice("custom", "WPA2 · eigenes Passwort"), new SecurityChoice("open", "Offen · ohne Passwort")]);
        hotspotSecurity.SelectedIndexChanged += (_, _) => UpdateHotspotSecurityFields();
        changeHotspotSecurity = Action("WLAN-Schutz speichern", async (_, _) => await SetHotspotSecurity());
        Row(grid, "WLAN-Schutz (ESP82)", hotspotSecurity, changeHotspotSecurity);
        revealNewHotspotPassword = PasswordReveal(newHotspotPassword);
        Row(grid, "Neues WLAN-Passwort", newHotspotPassword, revealNewHotspotPassword);
        hotspotChannel.Items.AddRange([1, 6, 11]);
        hotspotChannel.SelectedItem = 6;
        changeHotspotChannel = Action("Kanal speichern", async (_, _) => await SetHotspotChannel());
        Row(grid, "WLAN-Kanal", hotspotChannel, changeHotspotChannel);
        applyHotspot = Action("In Stick-Einrichtung übernehmen", (_, _) => UseHotspot());
        Full(grid, Buttons(applyHotspot));
        Full(grid, hotspotStatus);
        ShowHotspot(null);

        var voice = SetupSection(parent, "Mumble über WLAN");
        Row(voice, "Mumble-Server am PC", mumbleServer);
        RefreshMumbleServerAddresses([]);
        SetupHint(voice, "Für den Funkmodus „ESP-NOW + WLAN + Mumble“. Handy mit dem WLAN der Funkbasis verbinden und in Mumble 192.168.4.1, Port 64738, eintragen. Der Mumble-Server läuft auf diesem Rechner.");
        Full(voice, mumbleStatus);
        RefreshMumbleStatus();
    }

    private void RefreshMumbleServerAddresses(string[] addresses)
    {
        string[] options = ["127.0.0.1", .. addresses];
        if (mumbleServer.Items.Cast<string>().SequenceEqual(options)) return;
        string selected = mumbleServer.SelectedItem as string ?? options[0];
        mumbleServer.Items.Clear();
        mumbleServer.Items.AddRange(options);
        mumbleServer.SelectedItem = options.Contains(selected) ? selected : options[0];
    }

    private void RefreshMumbleStatus()
    {
        var bridge = activeRadio;
        mumbleServer.Enabled = bridge?.Mode != Transport.Mumble;
        if (bridge?.Mode != Transport.Mumble)
            mumbleStatus.Text = "Bereit zum Einrichten mit ESP8266-Firmware ab 0.15.0.";
        else if (!bridge.Status.MumbleReady)
            mumbleStatus.Text = "Mumble konnte auf der Funkbasis nicht gestartet werden. Tally bleibt aktiv.";
        else
        {
            string clients = $"Mumble aktiv · {bridge.MumbleClients}/2 Geräte verbunden";
            mumbleStatus.Text = bridge.MumbleError is { } error ? clients + "\n" + error : clients;
        }
    }

    private void ShowHotspot(UsbStatus? status)
    {
        hotspotInfo = Transport.Serves(status?.Mode, Transport.Hotspot) ? status : null;
        if (revealHotspotPassword is not null)
        {
            revealHotspotPassword.Checked = false;
            revealHotspotPassword.Enabled = hotspotInfo?.HotspotPassword.Length > 0;
        }
        hotspotName.Text = hotspotInfo?.HotspotSsid ?? "";
        hotspotPassword.Text = hotspotInfo?.HotspotPassword ?? "";
        newHotspotPassword.Clear();
        if (revealNewHotspotPassword is not null) revealNewHotspotPassword.Checked = false;
        hotspotSecurity.SelectedItem = hotspotSecurity.Items.OfType<SecurityChoice>()
            .FirstOrDefault(choice => hotspotInfo?.CanConfigureHotspotSecurity == true && choice.Id == hotspotInfo.HotspotSecurity);
        UpdateHotspotSecurityFields();
        hotspotChannel.SelectedItem = hotspotInfo?.Channel ?? 6;
        hotspotChannel.Enabled = hotspotInfo is not null;
        if (changeHotspotChannel is not null) changeHotspotChannel.Enabled = hotspotInfo is not null;
        if (applyHotspot is not null)
            applyHotspot.Enabled = hotspotInfo?.HasHotspotCredentials == true;
        hotspotStatus.Text = hotspotInfo switch
        {
            null => "Zum Anzeigen der WLAN-Daten die Firmware lesen oder die Brücke per USB verbinden.",
            { RadioReady: false } => "Der Hotspot konnte nicht starten.",
            { Board: "esp8266", CanConfigureHotspotSecurity: false } =>
                $"Basis-Server {hotspotInfo.Ip} · Kanal {hotspotInfo.Channel} · WLAN-Schutz einstellbar ab Firmware 0.14.0.",
            _ => $"Basis-Server {hotspotInfo.Ip} · Kanal {hotspotInfo.Channel}"
        };
    }

    private void UpdateHotspotSecurityFields()
    {
        hotspotSecurity.Enabled = hotspotInfo?.CanConfigureHotspotSecurity == true;
        newHotspotPassword.Enabled = hotspotSecurity.Enabled && hotspotSecurity.SelectedItem is SecurityChoice { Id: "custom" };
        if (!newHotspotPassword.Enabled) newHotspotPassword.Clear();
        if (revealNewHotspotPassword is not null)
        {
            revealNewHotspotPassword.Enabled = newHotspotPassword.Enabled;
            if (!newHotspotPassword.Enabled) revealNewHotspotPassword.Checked = false;
        }
        if (changeHotspotSecurity is not null)
            changeHotspotSecurity.Enabled = hotspotSecurity.Enabled && hotspotSecurity.SelectedItem is SecurityChoice;
    }

    private void UseHotspot()
    {
        if (busy || hotspotInfo is not { HasHotspotCredentials: true } status) return;
        pagerTransport.SelectedIndex = 0;
        addresses.Text = status.Ip;
        ssid.Text = status.HotspotSsid;
        password.Text = status.HotspotPassword;
        setupMethod.SelectedIndex = 0;
        tabs.SelectedTab = setup;
        usbStatus.Text = "Hotspot-Daten übernommen. USB-Port des Pagers wählen und „Stick einrichten“ drücken.";
    }

    private async Task SetHotspotSecurity()
    {
        if (busy || changingModes || hotspotInfo is not { CanConfigureHotspotSecurity: true } status ||
            basePort.SelectedItem is not string port || hotspotSecurity.SelectedItem is not SecurityChoice choice) return;
        string key = newHotspotPassword.Text;
        if (choice.Id == "custom" && !PagerUsb.IsHotspotPassword(key))
        {
            hotspotStatus.Text = "Für WPA2 bitte 8 bis 63 druckbare ASCII-Zeichen eingeben.";
            newHotspotPassword.Focus();
            return;
        }
        if (status.HasHotspotCredentials && choice.Id == status.HotspotSecurity &&
            (choice.Id != "custom" || key == status.HotspotPassword)) return;
        string confirmation = choice.Id == "open"
            ? "Jedes Gerät in Reichweite kann sich mit dem offenen WLAN verbinden. Die Brücke startet neu. WLAN ohne Passwort speichern?"
            : "Die Brücke startet neu. WLAN-Geräte brauchen danach das eingestellte Passwort. WLAN-Schutz jetzt speichern?";
        if (MessageBox.Show(this, confirmation, "WLAN-Schutz ändern", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await UpdateHotspot(port, () => PagerUsb.ConfigureHotspotSecurity(port, status, choice.Id, key),
            "WLAN-Schutz gespeichert.");
    }

    private async Task SetHotspotChannel()
    {
        if (busy || changingModes || hotspotInfo is not { } status || basePort.SelectedItem is not string port ||
            hotspotChannel.SelectedItem is not int channel || status.Channel == channel) return;
        if (MessageBox.Show(this, Transport.IsHybrid(status.Mode) ?
                "Der Kanalwechsel startet die Basis neu. Die ESP-NOW-Pager brauchen danach denselben neuen Kanal." :
                "Beim Kanalwechsel startet der Hotspot neu. Kanal jetzt wechseln?",
            "WLAN-Kanal wechseln", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await UpdateHotspot(port, () => status.Board == "esp8266" ?
            PagerUsb.ConfigureBase(port, status.Id, status.Mode, channel) : PagerUsb.ConfigureHotspotChannel(port, status.Id, channel),
            "WLAN-Kanal gespeichert.");
    }

    private async Task UpdateHotspot(string port, Func<UsbStatus> configure, string success)
    {
        bool reconnect = activeRadio?.Port == port;
        string? failure = null;
        busy = true;
        basePort.Enabled = false;
        try
        {
            if (reconnect) DisconnectBase("WLAN-Brücke wird neu gestartet …");
            hotspotStatus.Text = "WLAN-Brücke wird neu gestartet …";
            var updated = await Task.Run(configure);
            ShowUsbFirmware(updated, baseFirmware);
        }
        catch (Exception error)
        {
            ShowHotspot(null);
            failure = error.Message;
        }
        finally { basePort.Enabled = true; busy = false; }
        if (reconnect && Equals(basePort.SelectedItem, port)) await ConnectBase();
        if (Equals(basePort.SelectedItem, port)) hotspotStatus.Text = (failure ?? success) + " " + hotspotStatus.Text;
    }
}
