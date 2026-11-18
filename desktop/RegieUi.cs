using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace TallyPager;

internal sealed partial class MainWindow
{
    private readonly ProfileStore profiles;
    private readonly ComboBox profileChoice = Choices(), roleChoice = Choices(), groupChoice = Choices(),
        textChoice = Choices(), favorite = Choices();
    private readonly Label profileStatus = Notice(), profileSummary = Notice(), integrationStatus = Notice(),
        vmixMapping = Notice();
    private readonly CheckBox requireManualAck = new() { Text = "Chat-Quittierung anfordern", AutoSize = true };
    private readonly TextBox diagnosticsView = new() { Multiline = true, ReadOnly = true, Height = 220, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox receiptsView = new() { Multiline = true, ReadOnly = true, Height = 130, ScrollBars = ScrollBars.Vertical };
    private readonly TabPage diagnosticsTab = new("Diagnose");
    private readonly Label diagnosticStorage = Notice();
    private long renderedDiagnosticRevision = -1;
    private readonly Dictionary<string, Connection> deviceConnections = [];
    private long lastTick;

    private void AddRegieTools()
    {
        var page = new TabPage("Regieprofile");
        tabs.TabPages.Add(page);
        var grid = Grid(page, "Regieprofile");
        Row(grid, "Profil", profileChoice, Action("Aktivieren", (_, _) => ActivateProfile()));
        Full(grid, profileSummary);
        profileChoice.SelectedIndexChanged += (_, _) => DescribeProfile();
        Full(grid, Buttons(Action("Aktives Profil kopieren", (_, _) => NameProfile(true)),
            Action("Aktives Profil umbenennen", (_, _) => NameProfile(false)),
            Action("Quellenadressen speichern", (_, _) => SaveSourceFields())));
        Row(grid, "Kamera-Rolle", roleChoice, InlineButtons(Action("Neu", (_, _) => EditRole(null)),
            Action("Bearbeiten", (_, _) => EditRole(roleChoice.SelectedItem as CameraRole))));
        Full(grid, Buttons(Action("Ausgewählten Stick an Rolle binden", (_, _) => BindRole())));
        Row(grid, "Zielgruppe", groupChoice, InlineButtons(Action("Neu", (_, _) => EditGroup(null)),
            Action("Bearbeiten", (_, _) => { if (groupChoice.SelectedItem is TargetGroup group) EditGroup(group); })));
        Row(grid, "Schnelltext", textChoice, InlineButtons(Action("Neu", (_, _) => EditQuick(null)),
            Action("Bearbeiten", (_, _) => { if (textChoice.SelectedItem is QuickText text) EditQuick(text); }),
            Action("Löschen", (_, _) => DeleteQuick())));
        Full(grid, Buttons(Action("Profil exportieren", (_, _) => ExportProfiles(true)),
            Action("Sicherung exportieren", (_, _) => ExportProfiles(false)),
            Action("Importieren", (_, _) => ImportProfiles())));
        Full(grid, profileStatus);
        tabs.TabPages.Add(diagnosticsTab);
        var diagnostic = Grid(diagnosticsTab, "Verbindungen und Quittierungen");
        Full(diagnostic, diagnosticStorage);
        Full(diagnostic, vmixMapping);
        Full(diagnostic, Buttons(Action("Netzwerkdiagnose exportieren", (_, _) => ExportDiagnostics())));
        Full(diagnostic, new Label { Text = "Netzwerkereignisse", AutoSize = true });
        Full(diagnostic, diagnosticsView);
        Full(diagnostic, new Label { Text = "Abgeschlossene Chat-Anweisungen", AutoSize = true });
        Full(diagnostic, receiptsView);
        RefreshProfileChoices();
    }

    private void RefreshProfileChoices()
    {
        var active = profiles.Active;
        FillChoices(profileChoice, profiles.Backup.Profiles, active.Id, p => p.Id);
        FillChoices(roleChoice, active.Roles, (roleChoice.SelectedItem as CameraRole)?.Id, r => r.Id);
        FillChoices(groupChoice, active.Groups, (groupChoice.SelectedItem as TargetGroup)?.Id, g => g.Id);
        FillChoices(textChoice, active.Texts, (textChoice.SelectedItem as QuickText)?.Id, t => t.Id);
        FillChoices(favorite, active.Texts, (favorite.SelectedItem as QuickText)?.Id, t => t.Id);
        DescribeProfile();
    }

    private static void FillChoices<T>(ComboBox box, T[] values, Guid? selected, Func<T, Guid> id) where T : class
    {
        box.Items.Clear();
        box.Items.AddRange(values);
        if (values.Length != 0) box.SelectedItem = values.FirstOrDefault(value => id(value) == selected) ?? values[0];
    }

    private void DescribeProfile()
    {
        if (profileChoice.SelectedItem is not RegieProfile profile) return;
        string missing = string.Join(", ", profile.Roles.Where(r => !profiles.Bindings.TryGetValue(r.Id, out string? id) ||
            !core.Devices.Any(d => d.Id == id)).Select(r => r.Name));
        string changes = profile.Id == profiles.Active.Id ? "Aktives Profil" :
            $"Wechsel von {profiles.Active.Name} · laufende Simulationen und Nachrichten enden.";
        static string Value(string value, bool enabled) => value.Length == 0 ? "—" : enabled ? value : value + " (aus)";
        var source = profile.Sources;
        profileSummary.Text = $"{changes}\nATEM {Value(source.Atem, source.AtemEnabled)} · vMix {Value(source.Vmix, source.VmixEnabled)} · " +
            $"Tally Arbiter {Value(source.Arbiter, source.ArbiterEnabled)}\n{profile.Roles.Length} Rollen, {profile.Groups.Length} Gruppen, " +
            $"{profile.Texts.Length} Schnelltexte. Rollen ohne Stick: {(missing.Length == 0 ? "keine" : missing)}";
    }

    private static bool ProfileAction(Action action, Label status)
    {
        try { action(); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            status.Text = error.Message;
            return false;
        }
    }

    private void NameProfile(bool copy)
    {
        string activeName = profiles.Active.Name;
        var field = new TextBox { MaxLength = 80, Text = copy && activeName.Length <= 72 ? activeName + " (Kopie)" : activeName };
        if (!Edit(copy ? "Profil kopieren" : "Profil umbenennen", ("Name", field))) return;
        ProfileAction(() =>
        {
            if (copy) profiles.Duplicate(field.Text.Trim());
            else profiles.Update(profiles.Active with { Name = field.Text.Trim() });
            RefreshProfileChoices();
            if (copy) profileChoice.SelectedIndex = profileChoice.Items.Count - 1;
            profileStatus.Text = copy ? "Kopie erstellt. Mit Aktivieren übernehmen." : "Name gespeichert.";
        }, profileStatus);
    }

    private void ActivateProfile()
    {
        if (profileChoice.SelectedItem is not RegieProfile profile || profile.Id == profiles.Active.Id) return;
        ProfileAction(() =>
        {
            profiles.Activate(profile.Id);
            ResetProfileRuntime();
            profileStatus.Text = $"Profil {profile.Name} aktiviert.";
        }, profileStatus);
    }

    private void ResetProfileRuntime()
    {
        atem?.Dispose();
        vmix?.Dispose();
        arbiter?.Dispose();
        atem = null;
        vmix = null;
        arbiter = null;
        connectAtem.Text = connectVmix.Text = connectArbiter.Text = "Verbinden";
        session.Reset();
        session.Update(MixerState.Off, MixerState.Off, Environment.TickCount64, ArbiterState.Off);
        LoadSources();
        RefreshProfileChoices();
        SelectionChanged();
        requireManualAck.Checked = false;
        simStatus.Text = chatStatus.Text = "";
        ConnectProfileSources();
    }

    private void ConnectProfileSources()
    {
        var source = profiles.Active.Sources;
        if (source.AtemEnabled && atemHost.Text.Length != 0) ToggleAtem(connectAtem);
        if (source.VmixEnabled && vmixHost.Text.Length != 0) ToggleVmix(connectVmix);
        if (source.ArbiterEnabled && arbiterHost.Text.Length != 0) ToggleArbiter(connectArbiter);
    }

    private void LoadSources()
    {
        var address = profiles.Active.Sources;
        atemHost.Text = address.Atem;
        vmixHost.Text = address.Vmix;
        arbiterHost.Text = address.Arbiter;
    }

    private void StoreSources(SourceAddresses addresses)
    {
        if (addresses == profiles.Active.Sources) return;
        if (addresses.Atem.Length > 0 && (!IPAddress.TryParse(addresses.Atem, out var ip) ||
                ip.AddressFamily != AddressFamily.InterNetwork) ||
            addresses.Vmix.Length > 0 && !VmixSource.ValidAddress(addresses.Vmix) ||
            addresses.Arbiter.Length > 0 && TallyArbiterSource.Endpoint(addresses.Arbiter) is null)
            throw new InvalidDataException("ATEM-IPv4, vMix-Adresse oder Tally-Arbiter-Adresse prüfen.");
        profiles.Update(profiles.Active with { Sources = addresses });
        if (profileChoice.Items.Count != 0) RefreshProfileChoices();
    }

    private void SaveSourceFields() => ProfileAction(() =>
    {
        StoreSources(profiles.Active.Sources with
            { Atem = atemHost.Text.Trim(), Vmix = vmixHost.Text.Trim(), Arbiter = arbiterHost.Text.Trim() });
        profileStatus.Text = "Quellenadressen gespeichert.";
    }, profileStatus);

    private void EditRole(CameraRole? role)
    {
        var name = new TextBox { Text = role?.Name ?? "", MaxLength = 80 };
        var number = new NumericUpDown { Minimum = 0, Maximum = 99, Value = role?.MiddleCamera ?? 0 };
        if (!Edit("Kamera-Rolle", ("Name", name), ("Middle Camera ID (0 = aus)", number))) return;
        ProfileAction(() =>
        {
            role = new(role?.Id ?? Guid.NewGuid(), name.Text.Trim(), role?.Inside, role?.Outside,
                number.Value == 0 ? null : (int)number.Value);
            var active = profiles.Active;
            profiles.Update(active with { Roles = [.. active.Roles.Where(r => r.Id != role.Id), role] });
            RefreshProfileChoices();
            roleChoice.SelectedItem = role;
            profileStatus.Text = "Kamera-Rolle gespeichert.";
        }, profileStatus);
    }

    private void BindRole()
    {
        if (SelectedIds() is not [var id] || roleChoice.SelectedItem is not CameraRole role)
        {
            profileStatus.Text = "Oben einen Stick und darunter eine Kamera-Rolle auswählen.";
            return;
        }
        if (profiles.Bindings.TryGetValue(role.Id, out var prior) && prior != id &&
            MessageBox.Show(this, $"Den bisherigen Stick für {role.Name} ersetzen?", "Kamera-Rolle",
                MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        ProfileAction(() =>
        {
            profiles.Bind(id, role);
            RefreshProfileChoices();
            SelectionChanged();
            profileStatus.Text = $"Stick an {role.Name} gebunden.";
        }, profileStatus);
    }

    private CheckedListBox RoleList(Guid[] selected)
    {
        var list = new CheckedListBox { CheckOnClick = true, Height = 110 };
        foreach (var role in profiles.Active.Roles) list.Items.Add(role, selected.Contains(role.Id));
        return list;
    }

    private static Guid[] RoleIds(CheckedListBox roles) => roles.CheckedItems.OfType<CameraRole>().Select(r => r.Id).ToArray();

    private void EditGroup(TargetGroup? group)
    {
        var name = new TextBox { Text = group?.Name ?? "", MaxLength = 80 };
        var roles = RoleList(group?.Roles ?? []);
        if (!Edit("Zielgruppe", ("Name", name), ("Kamera-Rollen", roles))) return;
        ProfileAction(() =>
        {
            var entry = new TargetGroup(group?.Id ?? Guid.NewGuid(), name.Text.Trim(), RoleIds(roles));
            var active = profiles.Active;
            profiles.Update(active with { Groups = [.. active.Groups.Where(g => g.Id != entry.Id), entry] });
            RefreshProfileChoices();
            groupChoice.SelectedItem = entry;
            profileStatus.Text = "Zielgruppe gespeichert.";
        }, profileStatus);
    }

    private void EditQuick(QuickText? quick)
    {
        var name = new TextBox { Text = quick?.Name ?? "", MaxLength = 80 };
        var chatField = new TextBox { Text = quick?.Chat ?? "", MaxLength = 68 };
        var nextField = new TextBox { Text = quick?.Next ?? "", MaxLength = 38 };
        var updateChat = new CheckBox { Text = "Chat ändern", Checked = quick is null || quick.Chat is not null };
        var updateNext = new CheckBox { Text = "Next ändern", Checked = quick?.Next is not null };
        var ack = new CheckBox { Text = "Quittierung anfordern", Checked = quick?.RequireAck == true };
        var ttl = new NumericUpDown { Minimum = 0, Maximum = 3600, Value = quick?.TtlSeconds ?? 0 };
        var group = Choices();
        group.Items.Add("Einzelne Rollen");
        group.Items.AddRange(profiles.Active.Groups);
        group.SelectedItem = (object?)profiles.Active.Groups.FirstOrDefault(g => g.Id == quick?.Group) ?? group.Items[0];
        var roles = RoleList(quick?.Roles ?? []);
        void EnableFields()
        {
            chatField.Enabled = updateChat.Checked;
            nextField.Enabled = updateNext.Checked;
            ack.Enabled = updateChat.Checked;
            roles.Enabled = group.SelectedItem is not TargetGroup;
        }
        updateChat.CheckedChanged += (_, _) => EnableFields();
        updateNext.CheckedChanged += (_, _) => EnableFields();
        group.SelectedIndexChanged += (_, _) => EnableFields();
        EnableFields();
        if (!Edit("Schnelltext", ("Name", name), ("Chat", updateChat), ("Chat-Text", chatField),
                ("Next", updateNext), ("Next-Text", nextField), ("Quittierung", ack), ("Chat-Frist in Sekunden", ttl),
                ("Zielgruppe", group), ("Einzelne Rollen", roles))) return;
        ProfileAction(() =>
        {
            var target = group.SelectedItem as TargetGroup;
            var entry = new QuickText(quick?.Id ?? Guid.NewGuid(), name.Text.Trim(), updateChat.Checked ? chatField.Text.Trim() : null,
                updateNext.Checked ? nextField.Text.Trim() : null, target is null ? RoleIds(roles) : [],
                target?.Id, updateChat.Checked && ack.Checked, ttl.Value == 0 ? null : (int)ttl.Value);
            var active = profiles.Active;
            profiles.Update(active with { Texts = [.. active.Texts.Where(t => t.Id != entry.Id), entry] });
            RefreshProfileChoices();
            textChoice.SelectedItem = favorite.SelectedItem = entry;
            profileStatus.Text = "Schnelltext gespeichert. Frist 0 verwendet für Quittierungen 120 Sekunden.";
        }, profileStatus);
    }

    private void DeleteQuick()
    {
        if (textChoice.SelectedItem is not QuickText quick ||
            MessageBox.Show(this, $"Schnelltext {quick.Name} löschen?", "Schnelltext", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        ProfileAction(() =>
        {
            profiles.Update(profiles.Active with { Texts = profiles.Active.Texts.Where(t => t.Id != quick.Id).ToArray() });
            RefreshProfileChoices();
            profileStatus.Text = "Schnelltext gelöscht.";
        }, profileStatus);
    }

    private void LoadQuick()
    {
        if (favorite.SelectedItem is not QuickText text) return;
        var (ids, missing) = profiles.Targets(text, core.Devices.Select(d => d.Id));
        devices.ClearSelection();
        foreach (string id in ids) if (rows.TryGetValue(id, out var row)) row.Selected = true;
        if (text.Chat is not null) chat.Text = text.Chat;
        if (text.Next is not null) next.Text = text.Next;
        requireManualAck.Checked = text.RequireAck;
        chatStatus.Text = $"Vorlage übernommen. {ids.Length} Stick(s) ausgewählt." + MissingRoles(missing);
    }

    private static string MissingRoles(string[] missing) => missing.Length == 0 ? "" : " Rollen ohne Stick: " + string.Join(", ", missing) + ".";

    private (int Count, string[] Missing, string Message) SendQuick(QuickText text)
    {
        var (ids, missing) = profiles.Targets(text, core.Devices.Select(d => d.Id));
        session.SetText(ids, text.Chat, text.Next, text.RequireAck, text.TtlSeconds);
        return (ids.Length, missing, $"{text.Name} · {ids.Length} Stick(s) in der Warteschlange." + MissingRoles(missing));
    }

    private bool Edit(string title, params (string Label, Control Control)[] fields)
    {
        using var form = new Form
        {
            Text = title, Font = Font, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false,
            MaximizeBox = false, ClientSize = new Size(570, Math.Min(570, 75 + fields.Length * 38 +
                fields.Count(f => f.Control is CheckedListBox) * 75))
        };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12), ColumnCount = 3 };
        grid.ColumnStyles.Add(new(SizeType.Absolute, 195));
        grid.ColumnStyles.Add(new(SizeType.Percent, 100));
        form.Controls.Add(grid);
        foreach (var (label, control) in fields) Row(grid, label, control);
        var save = Action("Speichern");
        save.DialogResult = DialogResult.OK;
        var cancel = Action("Abbrechen");
        cancel.DialogResult = DialogResult.Cancel;
        Full(grid, Buttons(save, cancel));
        form.AcceptButton = save;
        form.CancelButton = cancel;
        var result = form.ShowDialog(this);
        foreach (var (_, control) in fields) grid.Controls.Remove(control);
        return result == DialogResult.OK;
    }

    private void ExportProfiles(bool onlyActive)
    {
        using var dialog = new SaveFileDialog { Filter = "TallyPager JSON (*.json)|*.json", FileName = onlyActive ? "TallyPager-Profil.json" : "TallyPager-Sicherung.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ProfileAction(() =>
        {
            profiles.Export(dialog.FileName, onlyActive);
            profileStatus.Text = "Export gespeichert.";
        }, profileStatus);
    }

    private void ImportProfiles()
    {
        using var dialog = new OpenFileDialog { Filter = "TallyPager JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ProfileAction(() =>
        {
            var incoming = ProfileStore.PreviewImport(dialog.FileName);
            using var preview = new Form { Text = "Sicherung prüfen", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(660, 330), Font = Font };
            var description = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
            description.Text = string.Join("\r\n\r\n", incoming.Profiles.Select(p => $"{p.Name} · {p.Roles.Length} Rollen · {p.Texts.Length} Schnelltexte\r\n" +
                $"ATEM {p.Sources.Atem} · vMix {p.Sources.Vmix} · Tally Arbiter {p.Sources.Arbiter}\r\n" +
                "Rollen ohne bekannte Bindung: " + string.Join(", ", p.Roles.Where(r => !profiles.Bindings.TryGetValue(r.Id, out var bound) ||
                    !core.Devices.Any(d => d.Id == bound)).Select(r => r.Name))));
            var copy = Action("Als Kopie importieren");
            var replace = Action("Alles ersetzen und vorher sichern", (_, _) =>
            {
                if (MessageBox.Show(preview, "Alle Profile aus der Sicherung übernehmen?", "Sicherung", MessageBoxButtons.YesNo) == DialogResult.Yes)
                    preview.DialogResult = DialogResult.Yes;
            });
            var cancel = Action("Abbrechen");
            cancel.DialogResult = DialogResult.Cancel;
            copy.DialogResult = DialogResult.OK;
            var actions = Buttons(copy, replace, cancel);
            actions.Dock = DockStyle.Bottom;
            preview.Controls.Add(description);
            preview.Controls.Add(actions);
            preview.CancelButton = cancel;
            var result = preview.ShowDialog(this);
            if (result is not (DialogResult.OK or DialogResult.Yes)) return;
            profiles.Import(incoming, result == DialogResult.Yes);
            if (result == DialogResult.Yes) ResetProfileRuntime();
            else RefreshProfileChoices();
            profileStatus.Text = result == DialogResult.Yes ? "Wiederhergestellt. Vorheriger Stand wurde lokal gesichert." :
                "Kopien importiert. Kamera-Rollen nach der Aktivierung neu binden.";
        }, profileStatus);
    }

    private void LogMixer(string source, Label label, string prior, string current)
    {
        if (prior == current) return;
        label.Text = current;
        core.Diagnostics.Add(source, "Quellenstatus", current);
    }

    private void RefreshDiagnostics(MixerState v)
    {
        var missing = profiles.Active.Roles.SelectMany(role => new[] { role.Inside ?? OperatorSession.DefaultInside,
            role.Outside ?? OperatorSession.DefaultOutside }.Where(assignment => assignment.Mixer == MixerKind.Vmix)
            .Where(assignment => OperatorSession.FindInput(assignment, MixerState.Off, v, null) is null)
            .Select(assignment => $"{role.Name} · Eingang {assignment.Input}")).Distinct().ToArray();
        string mapping = v.Status.StartsWith("Verbunden", StringComparison.Ordinal) && missing.Length > 0
            ? "vMix erreichbar. Gespeicherte Eingänge fehlen: " + string.Join(", ", missing) : "";
        if (vmixMapping.Text != mapping)
        {
            vmixMapping.Text = mapping;
            if (mapping.Length > 0) core.Diagnostics.Add("vMix", "Zuordnung fehlt", mapping);
        }
        if (tabs.SelectedTab != diagnosticsTab) return;
        string storage = core.Diagnostics.StorageIssue ?? $"Die letzten {DiagnosticLog.Capacity:N0} Netzwerkereignisse bleiben lokal gespeichert.";
        if (storage != diagnosticStorage.Text) diagnosticStorage.Text = storage;
        long revision = core.Diagnostics.Revision;
        if (renderedDiagnosticRevision != revision)
        {
            diagnosticsView.Text = string.Join("\r\n", core.Diagnostics.Entries.Reverse().Select(entry =>
                $"{entry.Time.ToLocalTime():dd.MM.yyyy HH:mm:ss} · {entry.Source} · {entry.Event} · {entry.Detail}"));
            renderedDiagnosticRevision = revision;
        }
        string history = string.Join("\r\n", session.History.Reverse().Select(entry =>
            $"{entry.Time.ToLocalTime():HH:mm:ss} · {core.Devices.FirstOrDefault(p => p.Id == entry.Device)?.Name ?? "Stick"} · {entry.Result}"));
        if (receiptsView.Text != history) receiptsView.Text = history;
    }

    private void ExportDiagnostics()
    {
        using var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "TallyPager-Netzwerkdiagnose.json" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            ProfileAction(() => ProfileStore.Write(dialog.FileName, core.Diagnostics.Entries), vmixMapping);
    }
}
