using System.Text.Json;
using System.Text.RegularExpressions;

namespace TallyPager;

internal sealed record DeviceTemplate(string Mode, string Host, string Ssid, string Name, string Gateway, int Channel, int Slot);
internal sealed record DeviceSetupLibrary(Dictionary<string, string> Wifi, Dictionary<string, DeviceTemplate> Templates);

internal sealed partial class MainWindow
{
    private readonly CheckBox keepWifi = new() { Text = "WLAN-Daten für weitere Sticks behalten", AutoSize = true, Checked = true };
    private readonly CheckBox incrementName = new() { Text = "Stick-Nummer nach erfolgreicher Einrichtung erhöhen", AutoSize = true };
    private readonly NumericUpDown deviceSlot = new() { Minimum = 1, Maximum = 4 };
    private readonly Label usbPortStatus = Notice();
    private string[] knownPorts = [];
    private long lastPortScan;

    private string SetupLibraryPath => Path.Combine(core.StorageDirectory, "device-setup.json");
    private DeviceSetupLibrary LoadSetupLibrary() => File.Exists(SetupLibraryPath)
        ? JsonSerializer.Deserialize<DeviceSetupLibrary>(File.ReadAllText(SetupLibraryPath), ProfileStore.Json) ??
            throw new InvalidDataException("Gespeicherte Geräteeinstellungen sind ungültig.")
        : new(new(StringComparer.Ordinal), new(StringComparer.Ordinal));

    private static FlowLayoutPanel Reveal(TextBox field)
    {
        var reveal = new CheckBox { Text = "Anzeigen", AutoSize = true };
        reveal.CheckedChanged += (_, _) => field.UseSystemPasswordChar = !reveal.Checked;
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        actions.Controls.Add(reveal);
        return actions;
    }

    private TableLayoutPanel SetupSection(TableLayoutPanel parent, string title)
    {
        var group = new GroupBox
        {
            Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top, Padding = new Padding(12, 8, 12, 12),
            Font = new Font(Font, FontStyle.Bold)
        };
        var grid = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top,
            ColumnCount = 3, Padding = new Padding(0, 4, 0, 0), Font = Font
        };
        grid.ColumnStyles.Add(new(SizeType.Absolute, 185));
        grid.ColumnStyles.Add(new(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new(SizeType.AutoSize));
        group.Controls.Add(grid);
        Full(parent, group);
        return grid;
    }

    private static void SetupHint(TableLayoutPanel grid, string text) => Full(grid, new Label
    {
        Text = text, AutoSize = true, MaximumSize = new Size(720, 0), Dock = DockStyle.Top,
        ForeColor = Color.FromArgb(70, 83, 98)
    });

    private void ShowProfileHelp()
    {
        MessageBox.Show(this,
            "Vorlagen werden auf diesem PC gespeichert. Sie füllen das Einrichtungsformular aus, damit du mehrere Sticks " +
            "mit ähnlichen Einstellungen vorbereiten kannst. Mit „Stick einrichten“ überträgst du die Felder auf das Gerät.\n\n" +
            "Geräteprofile liegen auf dem Stick. Die Speicherplätze 1 bis 4 enthalten jeweils einen kompletten Satz " +
            "aus Funkmodus, WLAN und Serveradresse. „Stick einrichten“ speichert in den gewählten Platz und aktiviert ihn. " +
            "Später wechselst du am Stick oder über „Funkmodus wechseln …“ zwischen den vorbereiteten Profilen.\n\n" +
            "Regieprofile liegen auf dem PC. Sie speichern den Aufbau für eine Produktion, etwa Mixerquellen, " +
            "Kamerarollen, Tally-Zuordnungen und Schnelltexte. Du verwaltest sie im Tab „Regieprofile“.",
            "Vorlagen und Profile", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void AddSetupLibrary(TableLayoutPanel templatesGrid, TableLayoutPanel wirelessGrid)
    {
        var networks = Choices();
        var templates = Choices();
        void Refresh()
        {
            var library = LoadSetupLibrary();
            networks.Items.Clear();
            networks.Items.AddRange(library.Wifi.Keys.Order().ToArray());
            templates.Items.Clear();
            templates.Items.AddRange(library.Templates.Keys.Order().ToArray());
        }
        void ReadSavedNetwork(DeviceSetupLibrary library, string network)
        {
            try
            {
                string secret = library.Wifi.TryGetValue(network, out var stored) ? LocalSecret.Open(stored) : "";
                ssid.Text = network;
                password.Text = secret;
            }
            catch (Exception error) when (error is System.Security.Cryptography.CryptographicException or FormatException)
            {
                throw new InvalidDataException("Dieses Windows-Konto konnte die gespeicherten WLAN-Daten nicht lesen.", error);
            }
        }
        Row(wirelessGrid, "Gespeichertes WLAN", networks, InlineButtons(
            Action("Laden", (_, _) => ProfileAction(() =>
            {
                if (networks.SelectedItem is string network) ReadSavedNetwork(LoadSetupLibrary(), network);
            }, usbStatus)),
            Action("Aktuelles merken", (_, _) => ProfileAction(() =>
            {
                if (ssid.Text.Length == 0 || !ProfileStore.TextOk(ssid.Text, 32) || !ProfileStore.TextOk(password.Text, 64))
                    throw new InvalidDataException("Zum Speichern zuerst WLAN-Name und Passwort prüfen.");
                var library = LoadSetupLibrary();
                library.Wifi[ssid.Text] = LocalSecret.Seal(password.Text);
                ProfileStore.Write(SetupLibraryPath, library);
                Refresh();
                networks.SelectedItem = ssid.Text;
                usbStatus.Text = "WLAN im Windows-Benutzerkonto gespeichert.";
            }, usbStatus))));
        Row(templatesGrid, "Vorlage", templates, InlineButtons(
            Action("Vorlage laden", (_, _) => ProfileAction(() =>
            {
                if (templates.SelectedItem is not string selected) return;
                var library = LoadSetupLibrary();
                var options = library.Templates[selected];
                int index = Array.IndexOf(new[] { "udp", "espnow", "espnow-lr", "ble" }, options.Mode);
                if (index < 0 || options.Channel is < 1 or > 13 || options.Slot is < 1 or > 4)
                    throw new InvalidDataException("Die Vorlage enthält ungültige Funkdaten.");
                pagerTransport.SelectedIndex = index;
                addresses.Text = options.Host;
                ReadSavedNetwork(library, options.Ssid);
                pagerName.Text = options.Name;
                gatewayAddress.Text = options.Gateway;
                pagerChannel.Value = options.Channel;
                deviceSlot.Value = options.Slot;
                usbStatus.Text = $"Vorlage {selected} geladen.";
            }, usbStatus)),
            Action("Als Vorlage speichern", (_, _) =>
            {
                using var name = new TextBox { MaxLength = 48 };
                if (!Edit("Vorlage speichern", ("Name", name)) || string.IsNullOrWhiteSpace(name.Text)) return;
                ProfileAction(() =>
                {
                    var library = LoadSetupLibrary();
                    library.Templates[name.Text.Trim()] = new(SelectedTransport(pagerTransport), addresses.Text.Trim(), ssid.Text,
                        pagerName.Text, gatewayAddress.Text, (int)pagerChannel.Value, (int)deviceSlot.Value);
                    ProfileStore.Write(SetupLibraryPath, library);
                    Refresh();
                    templates.SelectedItem = name.Text.Trim();
                    usbStatus.Text = "Vorlage gespeichert.";
                }, usbStatus);
            })));
        ProfileAction(Refresh, usbStatus);
    }

    internal static string NextDeviceName(string name)
    {
        var suffix = Regex.Match(name, "[0-9]+$");
        string stem = suffix.Success ? name[..suffix.Index] : name + " ";
        string number = "1";
        if (suffix.Success && int.TryParse(suffix.Value, out int index) && index < int.MaxValue)
            number = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(suffix.Length, '0');
        string result = stem + number;
        return result.Length <= 32 && ProfileStore.TextOk(result, 48) ? result : name;
    }

    private async Task InspectDeviceSettings()
    {
        if (busy || ports.SelectedItem is not string port || activeRadio?.Port == port)
        {
            usbStatus.Text = "Zum Auslesen einen freien USB-Port auswählen.";
            return;
        }
        busy = true;
        setup.Controls[0].Enabled = false;
        try
        {
            var data = await Task.Run(() => PagerUsb.ReadSettings(port));
            LoadDeviceSettings(data);
            usbStatus.Text = $"Einstellungen von {data.Name} geladen · Geräteprofil {data.Slot + 1}.";
        }
        catch (Exception error) { usbStatus.Text = error.Message; }
        finally
        {
            busy = false;
            setup.Controls[0].Enabled = true;
        }
    }

    private void LoadDeviceSettings(UsbSettings data)
    {
        var mode = pagerTransport.Items.OfType<ModeChoice>().FirstOrDefault(item => item.Id == data.Mode);
        if (mode is null || data.Channel < pagerChannel.Minimum || data.Channel > pagerChannel.Maximum ||
            data.Slot + 1 < deviceSlot.Minimum || data.Slot + 1 > deviceSlot.Maximum)
            throw new InvalidDataException("Die ausgelesenen Funk- oder Profildaten werden von dieser App nicht unterstützt.");

        // Changing the mode can prefill a connected base. Apply the device's own values afterwards.
        pagerTransport.SelectedItem = mode;
        addresses.Text = data.Host;
        pagerName.Text = data.Name;
        ssid.Text = data.Ssid;
        password.Text = data.Password;
        gatewayAddress.Text = data.Gateway;
        pagerChannel.Value = data.Channel;
        deviceSlot.Value = data.Slot + 1;
    }
}
