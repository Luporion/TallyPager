namespace TallyPager;

internal sealed partial class MainWindow
{
    private readonly ComboBox remoteProfile = Choices(), fleetMode = ModePicker(true);
    private readonly Button remoteSwitch = Action("Ausgewählten Stick umstellen"),
        fleetSwitch = Action("Alle erreichbaren Sticks umstellen");
    private readonly Label remoteTarget = Notice(), remoteResult = Notice();
    private Form? remoteModesDialog;
    private bool changingModes;

    private WindowsBleBridge? windowsBluetooth;
    private Button? windowsBluetoothLink;
    private readonly Label windowsBluetoothStatus = Notice();

    private void OpenRemoteModes()
    {
        if (remoteModesDialog is null)
        {
            remoteModesDialog = new Form
            {
                Text = "Funkmodus wechseln", ClientSize = new Size(680, 460),
                MinimumSize = new Size(550, 340), StartPosition = FormStartPosition.CenterParent,
                ShowInTaskbar = false, MinimizeBox = false, MaximizeBox = false, Font = Font
            };
            AddRemoteModes(remoteModesDialog);
        }
        RefreshRemoteModes();
        remoteModesDialog.ShowDialog(this);
    }

    private void AddRemoteModes(Form dialog)
    {
        var grid = Grid(dialog, "Ausgewählter Stick");
        Full(grid, remoteTarget);
        Row(grid, "Geräteprofil", remoteProfile, remoteSwitch);
        remoteSwitch.Click += async (_, _) =>
        {
            if (SelectedPager() is { } pager && remoteProfile.SelectedItem is DeviceSlot slot)
                await SwitchModes([(pager, slot)]);
        };
        remoteProfile.SelectedIndexChanged += (_, _) => RefreshModeButtons();
        Heading(grid, "Flotte");
        Row(grid, "Zielmodus", fleetMode, fleetSwitch);
        fleetMode.SelectedIndex = 0;
        fleetSwitch.Click += async (_, _) => await SwitchFleet();
        Full(grid, new Label
        {
            AutoSize = true, MaximumSize = new Size(480, 0),
            Text = "Jeder Stick braucht ein eindeutiges Profil für den Zielmodus. Wenn die neue Verbindung ausbleibt, " +
                "kehrt der Stick zum bisherigen Profil zurück."
        });
        remoteTarget.MaximumSize = remoteResult.MaximumSize = new Size(480, 0);
        Full(grid, remoteResult);
        var close = Action("Schließen");
        close.DialogResult = DialogResult.Cancel;
        Full(grid, Buttons(close));
        dialog.CancelButton = close;
    }

    private void RefreshRemoteModes()
    {
        var pager = SelectedPager();
        var slots = pager is { Online: true, Slots: { } available }
            ? available.Where(slot => slot.Number != pager.ActiveSlot).ToArray() : [];
        if (!remoteProfile.Items.Cast<DeviceSlot>().SequenceEqual(slots))
        {
            var previous = remoteProfile.SelectedItem;
            remoteProfile.Items.Clear();
            remoteProfile.Items.AddRange(slots.Cast<object>().ToArray());
            if (previous is not null && remoteProfile.Items.Contains(previous)) remoteProfile.SelectedItem = previous;
            else if (slots.Length > 0) remoteProfile.SelectedIndex = 0;
        }
        remoteTarget.Text = pager switch
        {
            null => "Für den Einzelwechsel zuerst genau einen Stick in der Tabelle auswählen.",
            { Online: false } => $"{pager.Name} · Tally-Verbindung erforderlich.",
            { Firmware: { } fw } when !fw.Supports(FirmwareInfo.RemoteSlots) =>
                $"{pager.Name} · Funkwechsel benötigt die Firmware ab 0.8.0.",
            { ActiveSlot: null } => $"{pager.Name} · Warte auf Geräteprofile …",
            _ => $"{pager.Name} · Profil {pager.ActiveSlot + 1} aktiv · {Transport.Name(pager.Transport)}" +
                (slots.Length == 0 ? " · Weitere Geräteprofile einmalig per USB speichern." : "")
        };
        RefreshModeButtons();
    }

    private void RefreshModeButtons()
    {
        bool ready = !changingModes && !busy;
        remoteSwitch.Enabled = ready && remoteProfile.SelectedItem is DeviceSlot;
        remoteProfile.Enabled = ready && remoteProfile.Items.Count > 0;
        fleetSwitch.Enabled = fleetMode.Enabled = ready;
    }

    private string? CheckTarget(DeviceSlot slot)
    {
        if (slot.Mode == "udp") return null;
        if (slot.Mode == "ble" && windowsBluetooth is { Failure: null }) return null;
        if (activeRadio is not { Failure: null } usb || usb.Mode != slot.Mode)
            return $"Funkbasis für {Transport.Name(slot.Mode)} verbinden.";
        if (IsEspNow(slot.Mode) && (usb.Status.RadioAddress != slot.Gateway || usb.Status.Channel != slot.Channel))
            return $"Das Zielprofil erwartet die Basis {Transport.DisplayAddress(slot.Gateway)} auf Kanal {slot.Channel}.";
        return null;
    }

    private async Task SwitchFleet()
    {
        string mode = SelectedTransport(fleetMode);
        var candidates = new List<(PagerStatus Pager, DeviceSlot Slot)>();
        var skipped = new List<string>();
        foreach (var pager in core.Statuses())
        {
            if (pager.Online && Transport.PagerMode(pager.Transport) == mode) continue;
            var profiles = pager.Slots?.Where(slot => slot.Mode == mode && slot.Number != pager.ActiveSlot).ToArray();
            if (!pager.Online || profiles is not [var profile] || pager.Firmware?.Supports(FirmwareInfo.RemoteSlots) != true)
            {
                skipped.Add(pager.Name + " · " + (!pager.Online ? "getrennt" :
                    profiles is { Length: > 1 } ? "mehrere passende Profile" : "kein passendes Profil"));
                continue;
            }
            candidates.Add((pager, profile));
        }
        await SwitchModes(candidates, skipped);
    }

    private async Task SwitchModes(IReadOnlyList<(PagerStatus Pager, DeviceSlot Slot)> devices, IEnumerable<string>? skipped = null)
    {
        if (changingModes || busy) return;
        var eligible = new List<(PagerStatus Pager, DeviceSlot Slot)>();
        var notes = skipped?.ToList() ?? [];
        foreach (var item in devices)
            if (CheckTarget(item.Slot) is { } error) notes.Add($"{item.Pager.Name} · {error}");
            else eligible.Add(item);
        if (eligible.Count == 0)
        {
            remoteResult.Text = notes.Count > 0 ? string.Join("\n", notes) : "Kein Stick zum Umstellen ausgewählt.";
            return;
        }
        string targets = string.Join("\n", eligible.Select(item => $"{item.Pager.Name} → {item.Slot}"));
        string prompt = "Während des Neustarts setzt die Tally-Anzeige kurz aus. Funkmodus jetzt wechseln?\n\n" + targets;
        if (MessageBox.Show(remoteModesDialog ?? this, prompt, "Funkmodus wechseln", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        changingModes = true;
        RefreshModeButtons();
        remoteResult.Text = "Warte auf die Bestätigung der Sticks …";
        try
        {
            string[] results = await Task.WhenAll(eligible.Select(async item =>
            {
                string result;
                try
                {
                    result = await core.SwitchSlot(item.Pager.Id, item.Slot) switch
                    {
                        "applied" => $"Bestätigt · {item.Slot}",
                        "rollback" => "Zum bisherigen Profil zurückgekehrt",
                        "rejected" => "Wechsel vom Stick abgelehnt",
                        _ => "Keine Bestätigung erhalten · Verbindung und aktives Profil prüfen"
                    };
                }
                catch (InvalidOperationException error) { result = error.Message; }
                core.Diagnostics.Add(item.Pager.Name, "Funkmodus wechseln", result);
                return $"{item.Pager.Name} · {result}";
            }));
            remoteResult.Text = string.Join("\n", results.Concat(notes));
        }
        finally
        {
            changingModes = false;
            RefreshRemoteModes();
        }
    }

    private void AddWindowsBluetoothControls(TableLayoutPanel grid)
    {
        Heading(grid, "Direkt über Windows-Bluetooth");
        windowsBluetoothLink = Action("BLE direkt verbinden", async (_, _) => await ToggleWindowsBluetooth());
        Full(grid, windowsBluetoothLink);
        Full(grid, windowsBluetoothStatus);
        windowsBluetoothStatus.Text = "Nutzt den Bluetooth-LE-Adapter des Basis-Servers. BLE-Sticks werden automatisch gesucht.";
    }

    private async Task ToggleWindowsBluetooth()
    {
        if (busy || changingModes) return;
        if (windowsBluetooth is not null) { DisconnectWindowsBluetooth("Direktes BLE getrennt."); return; }
        if (activeRadio?.Mode == "ble") { windowsBluetoothStatus.Text = "USB-Funkbasis im BLE-Modus zuerst trennen."; return; }
        busy = true;
        try
        {
            windowsBluetooth = await WindowsBleBridge.Start();
            core.AttachBluetooth(windowsBluetooth);
            windowsBluetoothLink!.Text = "Direktes BLE trennen";
            RefreshWindowsBluetooth();
        }
        catch (Exception error) { windowsBluetoothStatus.Text = error.Message; }
        finally { busy = false; }
    }

    private void RefreshWindowsBluetooth()
    {
        if (windowsBluetooth?.Failure is { } error) DisconnectWindowsBluetooth(error);
        if (windowsBluetooth is { } bridge)
        {
            string status = $"Suche aktiv · BLE-Verbindungen {bridge.Connected}" +
                (bridge.PeerProblem is { } problem ? $"\n{problem}" : "");
            if (windowsBluetoothStatus.Text != status) windowsBluetoothStatus.Text = status;
        }
    }

    private void DisconnectWindowsBluetooth(string message)
    {
        core.AttachBluetooth(null);
        windowsBluetooth = null;
        windowsBluetoothLink!.Text = "BLE direkt verbinden";
        windowsBluetoothStatus.Text = message;
    }
}
