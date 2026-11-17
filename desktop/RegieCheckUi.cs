namespace TallyPager;

internal sealed partial class MainWindow
{
    private void OpenRegieCheck()
    {
        string? selected = SelectedIds() is [var id] ? id : null;
        using var dialog = new Form
        {
            Text = $"Regiecheck · {profiles.Active.Name}", Font = Font, StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(850, 420), MinimumSize = new Size(690, 300), MinimizeBox = false,
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        var summary = Notice();
        var target = Notice();
        var list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, ShowItemToolTips = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable, AccessibleName = "Ergebnisse des Regiechecks",
        };
        list.Columns.Add("Status", 92);
        list.Columns.Add("Prüfung", 190);
        list.Columns.Add("Ergebnis", 510);
        list.Resize += (_, _) => list.Columns[2].Width = Math.Max(150, list.ClientSize.Width - 306);

        var test = Action("Tally-Test (6 s)");
        var refresh = Action("Erneut prüfen");
        var close = Action("Schließen", (_, _) => dialog.Close());
        close.DialogResult = DialogResult.Cancel;
        dialog.CancelButton = close;
        layout.Controls.Add(summary, 0, 0);
        layout.Controls.Add(list, 0, 1);
        layout.Controls.Add(target, 0, 2);
        layout.Controls.Add(Buttons(refresh, test, close), 0, 3);
        dialog.Controls.Add(layout);

        void UpdateCheck()
        {
            var pagers = core.Statuses();
            var checks = RegieCheck.Run(profiles.Active, profiles.Bindings, pagers, atem?.State ?? MixerState.Off,
                vmix?.State ?? MixerState.Off, arbiter?.State ?? ArbiterState.Off,
                activeRadio is { Failure: null } bridge ? bridge.Mode : null);
            int problems = checks.Count(row => row.Result == CheckResult.Problem);
            int warnings = checks.Count(row => row.Result == CheckResult.Warning);
            summary.Text = problems == 0 && warnings == 0 ? "Alle Prüfungen bestanden." :
                $"{problems} Probleme · {warnings} Hinweise";
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var item in checks.OrderByDescending(row => row.Result))
            {
                string label = item.Result switch { CheckResult.Problem => "Problem", CheckResult.Warning => "Hinweis", _ => "Bereit" };
                list.Items.Add(new ListViewItem([label, item.Target, item.Detail]) { ToolTipText = item.Detail });
            }
            list.EndUpdate();
            var pager = pagers.FirstOrDefault(p => p.Id == selected);
            test.Enabled = pager is { Online: true } && !session.IsTesting(pager.Id);
            target.Text = pager is null ? "Für ein Prüfsignal vor dem Regiecheck genau einen Stick auswählen." :
                !pager.Online ? $"{pager.Name} ist getrennt." :
                session.IsTesting(pager.Id) ? $"{pager.Name} zeigt bereits ein Prüfsignal." : $"Prüfsignal für {pager.Name}";
        }

        refresh.Click += (_, _) => UpdateCheck();
        test.Click += (_, _) =>
        {
            var pager = core.Statuses().FirstOrDefault(p => p.Id == selected);
            if (pager is not { Online: true } || !session.StartPagerCheck(pager.Id, Environment.TickCount64))
            {
                UpdateCheck();
                return;
            }
            string message = $"{pager.Name} zeigt sechs Sekunden TEST. Anschließend erscheinen die aktuellen Mixerzustände.";
            chatStatus.Text = message;
            target.Text = message;
            test.Enabled = false;
            core.Diagnostics.Add(pager.Name, "Tally-Test", "Sechs Sekunden Prüfsignal gestartet");
        };

        UpdateCheck();
        dialog.ShowDialog(this);
    }
}
