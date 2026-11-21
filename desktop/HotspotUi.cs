namespace TallyPager;

internal sealed partial class MainWindow
{
    private readonly TextBox hotspotName = new() { ReadOnly = true };
    private readonly TextBox hotspotPassword = new() { ReadOnly = true, UseSystemPasswordChar = true };
    private readonly ComboBox hotspotChannel = Choices();
    private readonly Label hotspotStatus = Notice();
    private UsbStatus? hotspotInfo;
    private Button? applyHotspot, changeHotspotChannel;

    private void AddHotspotSetup(TableLayoutPanel parent)
    {
        var grid = SetupSection(parent, "WLAN-Brücke");
        SetupHint(grid, "Kamera-Pager verwenden WLAN + UDP und den Basis-Server 192.168.4.1. Bis zu vier WLAN-Geräte können sich anmelden.");
        Full(grid, Buttons(Action("M5Stick-Brücken-Firmware flashen", async (_, _) => await FlashHotspot())));
        Row(grid, "WLAN-Name", hotspotName);
        Row(grid, "WLAN-Passwort", hotspotPassword, Reveal(hotspotPassword));
        hotspotChannel.Items.AddRange([1, 6, 11]);
        hotspotChannel.SelectedItem = 6;
        changeHotspotChannel = Action("Kanal speichern", async (_, _) => await SetHotspotChannel());
        Row(grid, "WLAN-Kanal", hotspotChannel, changeHotspotChannel);
        applyHotspot = Action("In Stick-Einrichtung übernehmen", (_, _) => UseHotspot());
        Full(grid, Buttons(applyHotspot));
        Full(grid, hotspotStatus);
        ShowHotspot(null);
    }

    private void ShowHotspot(UsbStatus? status)
    {
        hotspotInfo = Transport.Serves(status?.Mode, Transport.Hotspot) ? status : null;
        hotspotName.Text = hotspotInfo?.HotspotSsid ?? "";
        hotspotPassword.Text = hotspotInfo?.HotspotPassword ?? "";
        hotspotChannel.SelectedItem = hotspotInfo?.Channel ?? 6;
        hotspotChannel.Enabled = hotspotInfo is not null;
        if (changeHotspotChannel is not null) changeHotspotChannel.Enabled = hotspotInfo is not null;
        if (applyHotspot is not null)
            applyHotspot.Enabled = hotspotInfo is { RadioReady: true, Ip: Transport.HotspotIp, HotspotSsid.Length: > 0, HotspotPassword.Length: 16 };
        hotspotStatus.Text = hotspotInfo switch
        {
            null => "Zum Anzeigen der WLAN-Daten die Firmware lesen oder die Brücke per USB verbinden.",
            { RadioReady: false } => "Der Hotspot konnte nicht starten.",
            _ => $"Basis-Server {hotspotInfo.Ip} · Kanal {hotspotInfo.Channel}"
        };
    }

    private void UseHotspot()
    {
        if (busy || hotspotInfo is not { RadioReady: true, Ip: Transport.HotspotIp } status) return;
        pagerTransport.SelectedIndex = 0;
        addresses.Text = status.Ip;
        ssid.Text = status.HotspotSsid;
        password.Text = status.HotspotPassword;
        setupMethod.SelectedIndex = 0;
        tabs.SelectedTab = setup;
        usbStatus.Text = "Hotspot-Daten übernommen. USB-Port des Pagers wählen und „Stick einrichten“ drücken.";
    }

    private async Task FlashHotspot()
    {
        if (busy || changingModes) return;
        if (basePort.SelectedItem is not string port || activeRadio?.Port == port)
        {
            hotspotStatus.Text = "Einen freien USB-Port für den Brücken-Stick auswählen.";
            return;
        }
        string package = Path.Combine(AppContext.BaseDirectory, PagerUsb.HotspotFirmwareFileName);
        if (!File.Exists(package))
        {
            using var picker = new OpenFileDialog { Title = "WLAN-Brücken-Firmware auswählen", Filter = "Firmwarepaket (*.zip)|*.zip" };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            package = picker.FileName;
        }
        if (MessageBox.Show(this, $"WLAN-Brücken-Firmware auf den M5StickC Plus2 an {port} schreiben?",
            "WLAN-Brücke flashen", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        busy = true;
        basePort.Enabled = false;
        try
        {
            hotspotStatus.Text = "M5StickC Plus2 wird geprüft …";
            await PagerUsb.Flash(port, package, new Progress<string>(value => hotspotStatus.Text = value), FirmwareTarget.Hotspot);
            var status = await Task.Run(() => PagerUsb.ReadStatus(port, TimeSpan.FromSeconds(8)));
            ShowUsbFirmware(status, baseFirmware);
            if (status.Version != PagerUsb.Version || status.Mode != Transport.Hotspot || !RadioBridge.CanConnect(status))
                throw new InvalidOperationException("Der M5Stick hat die betriebsbereite WLAN-Brücke nach dem Neustart nicht bestätigt.");
            hotspotStatus.Text = "Hotspot läuft. Oben „Funkbasis verbinden“ drücken und anschließend die Daten in die Stick-Einrichtung übernehmen.";
        }
        catch (Exception error) { hotspotStatus.Text = error.Message; }
        finally { basePort.Enabled = true; busy = false; }
    }

    private async Task SetHotspotChannel()
    {
        if (busy || changingModes || hotspotInfo is not { } status || basePort.SelectedItem is not string port ||
            hotspotChannel.SelectedItem is not int channel || status.Channel == channel) return;
        bool reconnect = activeRadio?.Port == port;
        if (MessageBox.Show(this, status.Mode == Transport.Hybrid ?
                "Der Kanalwechsel startet die Basis neu. Die ESP-NOW-Pager brauchen danach denselben neuen Kanal." :
                "Beim Kanalwechsel startet der Hotspot neu. Kanal jetzt wechseln?",
            "WLAN-Kanal wechseln", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        busy = true;
        basePort.Enabled = false;
        try
        {
            if (reconnect) DisconnectBase("WLAN-Kanal wird gewechselt …");
            var updated = await Task.Run(() => status.Board == "esp8266" ?
                PagerUsb.ConfigureBase(port, status.Id, status.Mode, channel) : PagerUsb.ConfigureHotspotChannel(port, status.Id, channel));
            ShowUsbFirmware(updated, baseFirmware);
        }
        catch (Exception error) { hotspotStatus.Text = error.Message; }
        finally { basePort.Enabled = true; busy = false; }
        if (reconnect && Equals(basePort.SelectedItem, port)) await ConnectBase();
    }
}
