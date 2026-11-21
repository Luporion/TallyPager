namespace TallyPager;

internal sealed partial class MainWindow
{
    private async Task ChooseAndFlash(ComboBox portPicker, Label installedLabel, Label feedback, FirmwareTarget? preferred = null)
    {
        if (busy || changingModes) return;
        if (portPicker.SelectedItem is not string port) { feedback.Text = "USB-Port auswählen."; return; }
        if (activeRadio?.Port == port) { feedback.Text = "Vor dem Flashen die USB-Funkbasis trennen."; return; }
        busy = true;
        portPicker.Enabled = false;
        try
        {
            feedback.Text = $"Prüfe das Gerät an {port} …";
            UsbStatus? installed = null;
            try { installed = await ReadUsbFirmware(port, installedLabel); }
            catch (TimeoutException) { }
            var installedTarget = FirmwareCatalog.InstalledTarget(installed);
            var hardware = installedTarget is { } known
                ? new UsbHardware(known, installed!.BoardName)
                : await PagerUsb.IdentifyHardware(port);
            if (hardware.Target is not { } device)
            {
                feedback.Text = hardware.Description;
                return;
            }

            string cache = Path.Combine(core.StorageDirectory, "Firmware");
            feedback.Text = "";
            var choice = PickFirmware(port, hardware.Description, device, installed, preferred, cache);
            if (choice is null) return;
            string package;
            if (choice.LocalPath is { } local) package = local;
            else
            {
                feedback.Text = $"Firmware {choice.Version} wird von GitHub geladen …";
                using var http = GitHubUpdates.OpenClient();
                package = await new GitHubUpdates(http).DownloadFirmware(choice, cache, CancellationToken.None);
            }
            feedback.Text = $"Firmware {choice.Version} und USB-Gerät werden geprüft …";
            var progress = new Progress<string>(value => feedback.Text = value);
            await Task.Run(() => PagerUsb.Flash(port, package, progress, choice.Target, choice.Version));
            feedback.Text = $"Prüfe Firmware {choice.Version} nach dem Neustart …";
            var status = await Task.Run(() => PagerUsb.ReadStatus(port, TimeSpan.FromSeconds(8)));
            ShowUsbFirmware(status, installedLabel);
            if (installedLabel != baseFirmware && Equals(basePort.SelectedItem, port)) ShowUsbFirmware(status, baseFirmware);
            if (status.Version != choice.Version.ToString() || FirmwareCatalog.InstalledTarget(status) != choice.Target)
                throw new InvalidOperationException("Nach dem Neustart wurde eine andere Version oder Firmware-Art gemeldet.");
            feedback.Text = choice.Target switch
            {
                FirmwareTarget.Esp8266 => $"Firmware {status.Version} installiert. Funkmodus und Kanal im Funkbasis-Tab wählen.",
                FirmwareTarget.Hotspot when status.RadioReady => $"Firmware {status.Version} installiert. Im Funkbasis-Tab „Funkbasis verbinden“ drücken.",
                FirmwareTarget.Hotspot => $"Firmware {status.Version} installiert. Der Hotspot meldet einen Startfehler.",
                _ => $"Firmware {status.Version} installiert. Mit „Stick einrichten“ können die Geräteprofile bearbeitet werden."
            };
        }
        catch (Exception error) { feedback.Text = error.Message; }
        finally { portPicker.Enabled = true; busy = false; }
    }

    private FirmwareChoice? PickFirmware(string port, string description, FirmwareTarget device, UsbStatus? installed,
        FirmwareTarget? preferred, string cache)
    {
        using var dialog = new Form
        {
            Text = "Firmware auswählen", ClientSize = new Size(640, 320), MinimumSize = new Size(550, 300),
            StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false, MinimizeBox = false, MaximizeBox = false, Font = Font
        };
        var grid = Grid(dialog, $"{port} · {description}");
        if (installed is not null) SetupHint(grid, $"Installiert: Version {installed.Version}");
        var kinds = Choices();
        var versions = Choices();
        var status = Notice();
        status.MaximumSize = new Size(570, 0);
        var custom = Action("Eigenes ZIP auswählen …");
        var flash = Action("Firmware flashen");
        var close = Action("Abbrechen");
        close.DialogResult = DialogResult.Cancel;
        dialog.CancelButton = close;
        Row(grid, "Firmware-Art", kinds);
        Row(grid, "Version", versions);
        Full(grid, status);
        Full(grid, Buttons(custom, flash, close));

        var targets = FirmwareCatalog.CompatibleTargets(device);
        var options = new List<FirmwareChoice>();
        var installedVersion = installed?.Firmware?.VersionNumber;
        FirmwareChoice? selected = null;
        bool loading = true;
        string catalogStatus = "Lokale Pakete und GitHub-Releases werden gesucht …";
        using var stop = new CancellationTokenSource();
        string app = AppContext.BaseDirectory;
        string releaseRoot = UpdateRoot(app, Version.Parse(PagerUsb.Version));

        FirmwareTarget Target() => targets[Math.Max(0, kinds.SelectedIndex)];

        void RefreshStatus()
        {
            var choice = versions.SelectedItem as FirmwareChoice;
            flash.Enabled = !loading && choice is not null;
            status.Text = catalogStatus;
            if (choice is not null && choice.Version < installedVersion)
                status.Text += "\nDowngrade. Prüfe anschließend die Einstellungen und benötigten Funktionen des Geräts.";
            else if (!loading && choice is null)
                status.Text += "\nFür diese Firmware-Art ist kein Paket verfügbar.";
        }

        void FillVersions()
        {
            versions.Items.Clear();
            versions.Items.AddRange(FirmwareCatalog.ForTarget(Target(), options));
            if (versions.Items.Count > 0) versions.SelectedIndex = 0;
            RefreshStatus();
        }

        kinds.Items.AddRange(targets.Select(FirmwareCatalog.Name).ToArray());
        var initial = preferred ?? FirmwareCatalog.InstalledTarget(installed) ?? targets[0];
        kinds.SelectedIndex = Math.Max(0, Array.IndexOf(targets, initial));
        kinds.SelectedIndexChanged += (_, _) => FillVersions();
        versions.SelectedIndexChanged += (_, _) => RefreshStatus();
        RefreshStatus();
        versions.Enabled = custom.Enabled = false;

        dialog.Shown += async (_, _) =>
        {
            using var http = GitHubUpdates.OpenClient();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(7));
            var online = new GitHubUpdates(http).ListFirmware(deadline.Token);
            var local = await Task.Run(() => FirmwareCatalog.FindLocal(app, cache, releaseRoot, Path.Combine(core.StorageDirectory, "Releases")));
            if (dialog.Visible)
            {
                options.AddRange(local);
                FillVersions();
            }
            try
            {
                var remote = await online;
                if (!dialog.Visible) return;
                options.AddRange(remote);
                catalogStatus = "Die neueste Version der gewählten Firmware-Art ist vorausgewählt.";
            }
            catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or
                OperationCanceledException or System.Text.Json.JsonException)
            {
                if (!dialog.Visible) return;
                catalogStatus = "GitHub ist gerade nicht verfügbar. Lokale Pakete können verwendet werden.";
            }
            loading = false;
            versions.Enabled = custom.Enabled = true;
            FillVersions();
        };
        custom.Click += (_, _) =>
        {
            using var file = new OpenFileDialog { Title = "TallyPager-Firmwarepaket", Filter = "Firmwarepaket (*.zip)|*.zip", CheckFileExists = true };
            if (file.ShowDialog(dialog) != DialogResult.OK) return;
            try
            {
                var imported = FirmwareCatalog.FromFile(file.FileName, verify: true);
                int index = Array.IndexOf(targets, imported.Target);
                if (index < 0) throw new InvalidDataException("Dieses Firmwarepaket gehört zu einem anderen Gerät.");
                options.Insert(0, imported);
                kinds.SelectedIndex = index;
                FillVersions();
                versions.SelectedItem = imported;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                MessageBox.Show(dialog, error.Message, "Firmwarepaket prüfen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        flash.Click += (_, _) =>
        {
            if (versions.SelectedItem is not FirmwareChoice choice) return;
            if (choice.Version < installedVersion && MessageBox.Show(dialog,
                $"Version {installed!.Version} durch die ältere Version {choice.Version} auf {port} ersetzen?",
                "Firmware-Downgrade", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            selected = choice;
            dialog.DialogResult = DialogResult.OK;
        };
        var result = dialog.ShowDialog(this);
        stop.Cancel();
        return result == DialogResult.OK ? selected : null;
    }
}
