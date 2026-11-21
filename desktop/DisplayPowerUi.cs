namespace TallyPager;

internal sealed partial class MainWindow
{
    private void OpenDisplayPower()
    {
        if (SelectedPager() is not { } pager) return;
        using var dialog = new Form
        {
            Text = $"Display und Akku · {pager.Name}", ClientSize = new Size(550, 345),
            MinimumSize = new Size(460, 325), StartPosition = FormStartPosition.CenterParent,
            ShowInTaskbar = false, MinimizeBox = false, MaximizeBox = false, Font = Font
        };
        var grid = Grid(dialog, pager.Name);
        var brightness = new NumericUpDown
        {
            Minimum = DisplayPowerSettings.Minimum, Maximum = DisplayPowerSettings.Maximum,
            Value = DisplayPowerSettings.Minimum, Enabled = false
        };
        var dimSafe = new CheckBox { Text = "Bei SAFE nach 30 Sekunden dimmen", AutoSize = true, Enabled = false };
        var dimUnchanged = new CheckBox
        {
            Text = "Nach 2 Minuten ohne Zustandswechsel dimmen", AutoSize = true, Enabled = false
        };
        var status = Notice();
        status.MaximumSize = new Size(395, 0);
        var reload = Action("Vom Stick laden");
        var save = Action("Am Stick speichern");
        save.Enabled = false;
        var close = Action("Schließen");
        close.DialogResult = DialogResult.Cancel;
        dialog.CancelButton = close;

        Row(grid, "Helligkeit (%)", brightness);
        Full(grid, dimSafe);
        Full(grid, dimUnchanged);
        Full(grid, new Label
        {
            AutoSize = true, MaximumSize = new Size(405, 0),
            Text = "Dimmt auf halbe Helligkeit. Bei neuem Tally, Chat, Next oder Beamer-Signal wird das Display wieder heller."
        });
        Full(grid, Buttons(reload, save, close));
        Full(grid, status);

        async Task ReadOrSave(DisplayPowerSettings? settings)
        {
            reload.Enabled = brightness.Enabled = dimSafe.Enabled = dimUnchanged.Enabled = save.Enabled = false;
            status.Text = settings is null ? "Lese Einstellungen vom Stick …" : "Warte auf Bestätigung vom Stick …";
            bool loaded = false;
            try
            {
                var actual = await core.RequestDisplayPower(pager.Id, settings);
                if (dialog.IsDisposed || !dialog.Visible) return;
                brightness.Value = actual.Brightness;
                dimSafe.Checked = actual.DimSafe;
                dimUnchanged.Checked = actual.DimUnchanged;
                status.Text = settings is null ? "Einstellungen vom Stick geladen." : "Der Stick hat die Einstellungen gespeichert.";
                loaded = true;
            }
            catch (Exception error) when (error is InvalidOperationException or TimeoutException)
            {
                if (!dialog.IsDisposed && dialog.Visible) status.Text = error.Message;
            }
            finally
            {
                if (!dialog.IsDisposed && dialog.Visible)
                {
                    reload.Enabled = true;
                    brightness.Enabled = dimSafe.Enabled = dimUnchanged.Enabled = save.Enabled = loaded;
                }
            }
        }

        dialog.Shown += async (_, _) => await ReadOrSave(null);
        reload.Click += async (_, _) => await ReadOrSave(null);
        save.Click += async (_, _) => await ReadOrSave(new((int)brightness.Value, dimSafe.Checked, dimUnchanged.Checked));
        dialog.ShowDialog(this);
    }
}
