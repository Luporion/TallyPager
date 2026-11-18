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

    private void AddSetupLibrary(TableLayoutPanel grid)
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
        Row(grid, "Gespeichertes WLAN", networks, InlineButtons(
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
        Row(grid, "Einrichtungsvorlage", templates, InlineButtons(
            Action("Laden", (_, _) => ProfileAction(() =>
            {
                if (templates.SelectedItem is not string selected) return;
                var library = LoadSetupLibrary();
                var options = library.Templates[selected];
                int index = Array.IndexOf(new[] { "udp", "espnow", "espnow-lr", "ble" }, options.Mode);
                if (index < 0 || options.Channel is < 1 or > 13 || options.Slot is < 1 or > 4)
                    throw new InvalidDataException("Die Einrichtungsvorlage enthält ungültige Funkdaten.");
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
                if (!Edit("Einrichtungsvorlage speichern", ("Name", name)) || string.IsNullOrWhiteSpace(name.Text)) return;
                ProfileAction(() =>
                {
                    var library = LoadSetupLibrary();
                    library.Templates[name.Text.Trim()] = new(SelectedTransport(pagerTransport), addresses.Text.Trim(), ssid.Text,
                        pagerName.Text, gatewayAddress.Text, (int)pagerChannel.Value, (int)deviceSlot.Value);
                    ProfileStore.Write(SetupLibraryPath, library);
                    Refresh();
                    templates.SelectedItem = name.Text.Trim();
                    usbStatus.Text = "Einrichtungsvorlage gespeichert.";
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
        try
        {
            var data = await Task.Run(() => PagerUsb.ReadSettings(port));
            using var dialog = new Form { Text = "Stick-Einstellungen", StartPosition = FormStartPosition.CenterParent,
                Font = Font, ClientSize = new Size(640, 345), MinimizeBox = false, MaximizeBox = false };
            var grid = Grid(dialog, data.Name);
            Full(grid, new Label { Text = $"{Transport.Name(data.Mode)} · Geräteprofil {data.Slot + 1}", AutoSize = true });
            Row(grid, "Adresse des Core-PCs", new TextBox { Text = data.Host, ReadOnly = true });
            Row(grid, "WLAN-Name", new TextBox { Text = data.Ssid, ReadOnly = true });
            var secret = new TextBox { Text = data.Password, ReadOnly = true, UseSystemPasswordChar = true };
            Row(grid, "WLAN-Passwort", secret, Reveal(secret));
            Full(grid, Action("Schließen", (_, _) => dialog.Close()));
            dialog.ShowDialog(this);
            secret.Clear();
        }
        catch (Exception error) { usbStatus.Text = error.Message; }
        finally { busy = false; }
    }
}
