namespace TallyPager;

internal sealed partial class MainWindow
{
    private CompanionApi? companion;

    private void AddCompanion(TableLayoutPanel grid)
    {
        Heading(grid, "Bitfocus Companion und Middle Control");
        var address = new TextBox { Text = CompanionApi.Address, ReadOnly = true };
        var token = new TextBox { ReadOnly = true, UseSystemPasswordChar = true, PlaceholderText = "Beim Start verfügbar" };
        var route = new TextBox { ReadOnly = true };
        var quick = Choices();
        string local = Path.Combine(core.StorageDirectory, "companion-token.dat");
        string autostartPath = Path.Combine(core.StorageDirectory, "companion-autostart.json");
        var autostart = new CheckBox { Text = "Lokale API beim Programmstart einschalten", AutoSize = true };
        ProfileAction(() => autostart.Checked = File.Exists(autostartPath) &&
            System.Text.Json.JsonSerializer.Deserialize<bool>(File.ReadAllText(autostartPath)), integrationStatus);
        autostart.CheckedChanged += (_, _) => ProfileAction(() => ProfileStore.Write(autostartPath, autostart.Checked), integrationStatus);
        var toggle = Action("Lokale API starten");
        async Task ToggleApi()
        {
            toggle.Enabled = false;
            try
            {
                if (companion is not null)
                {
                    var stopped = companion;
                    companion = null;
                    await stopped.CloseAsync();
                    token.Clear();
                    toggle.Text = "Lokale API starten";
                    integrationStatus.Text = "Lokale API gestoppt.";
                }
                else
                {
                    string secret;
                    if (File.Exists(local)) secret = LocalSecret.Open(
                        System.Text.Json.JsonSerializer.Deserialize<string>(File.ReadAllText(local)) ?? "");
                    else
                    {
                        secret = PilotCore.RandomHex(32);
                        ProfileStore.Write(local, LocalSecret.Seal(secret));
                    }
                    var service = new CompanionApi(secret, DispatchCompanion);
                    try { await service.StartAsync(); }
                    catch { service.Dispose(); throw; }
                    if (IsDisposed) { service.Dispose(); return; }
                    companion = service;
                    token.Text = "Bearer " + secret;
                    toggle.Text = "Lokale API stoppen";
                    integrationStatus.Text = "Lokale API erreichbar. In Companion den Header Authorization mit dem kopierten Wert setzen.";
                }
            }
            catch (Exception error) { integrationStatus.Text = "Companion-API: " + error.Message; }
            finally { toggle.Enabled = true; }
        }
        toggle.Click += async (_, _) => await ToggleApi();
        Row(grid, "Adresse", address, Action("Kopieren", (_, _) => Clipboard.SetText(address.Text)));
        Row(grid, "Authorization", token, Action("Kopieren", (_, _) =>
        {
            if (token.Text.Length > 0) Clipboard.SetText(token.Text);
        }));
        Full(grid, Buttons(toggle));
        Full(grid, autostart);
        Shown += async (_, _) => { if (autostart.Checked) await ToggleApi(); };
        Full(grid, integrationStatus);
        Row(grid, "Schnelltext", quick);
        Row(grid, "POST-URL", route, Action("Kopieren", (_, _) =>
        {
            if (route.Text.Length > 0) Clipboard.SetText(route.Text);
        }));
        quick.SelectedIndexChanged += (_, _) => route.Text = quick.SelectedItem is QuickText text
            ? $"{CompanionApi.Address}/api/v1/quick-texts/{text.Id:D}/send" : "";
        tabs.SelectedIndexChanged += (_, _) =>
        {
            if (tabs.SelectedTab == sources)
                FillChoices(quick, profiles.Active.Texts, (quick.SelectedItem as QuickText)?.Id, text => text.Id);
        };
        Full(grid, new Label
        {
            AutoSize = true, MaximumSize = new Size(725, 0),
            Text = "In Generic HTTP einen POST ohne Body für den Schnelltext anlegen. Für Middle Control das Modul Middle Things Middle Control auf Port 11580 einrichten. Beide Aktionen können auf demselben Stream-Deck-Taster liegen."
        });
    }

    private async Task<ApiReply> DispatchCompanion(ApiCommand command, CancellationToken cancel)
    {
        if (IsDisposed || !IsHandleCreated) return ApiReply.Error(503, "Regiefenster nicht verfügbar.");
        try { return await InvokeAsync(() => HandleCompanion(command), cancel); }
        catch (InvalidOperationException) { return ApiReply.Error(503, "Regiefenster nicht verfügbar."); }
    }

    private ApiReply HandleCompanion(ApiCommand command)
    {
        var active = profiles.Active;
        if (command.Action == "status")
        {
            var devices = core.Statuses().ToDictionary(p => p.Id);
            return new(200, new
            {
                profile = new { active.Id, active.Name },
                quickTexts = active.Texts.Select(t => new { t.Id, t.Name, t.RequireAck }).ToArray(),
                groups = active.Groups.Select(g => new { g.Id, g.Name, g.Roles }).ToArray(),
                roles = active.Roles.Select(role =>
                {
                    var bound = devices.GetValueOrDefault(profiles.Bindings.GetValueOrDefault(role.Id) ?? "");
                    return new
                    {
                        role.Id, role.Name, role.MiddleCamera, device = bound?.Name,
                        transport = bound?.Transport,
                        connection = bound?.Connection.ToString() ?? "Unbound", rtt = bound?.Rtt, rssi = bound?.Rssi,
                        battery = bound?.Online == true ? bound.Battery : null,
                        inside = bound?.Online == true ? (int?)bound.View.Inside : null,
                        outside = bound?.Online == true ? (int?)bound.View.Outside : null,
                        insideSource = bound?.View.InsideSource, outsideSource = bound?.View.OutsideSource,
                        chat = bound is null ? null : OperatorSession.Delivery(bound),
                        chatId = bound?.View.ChatId, receivedAt = bound?.ReceivedAt, acknowledgedAt = bound?.AcknowledgedAt
                    };
                }).ToArray()
            });
        }
        if (command.Action == "send")
        {
            if (active.Texts.FirstOrDefault(t => t.Id == command.TextId) is not { } text)
                return ApiReply.Error(404, "Schnelltext im aktiven Profil fehlt.");
            var result = SendQuick(text);
            chatStatus.Text = result.Message;
            return result.Count == 0 ? new(409, new { queued = 0, missingRoles = result.Missing }) :
                new(202, new { queued = result.Count, missingRoles = result.Missing });
        }
        var body = command.Body!;
        if (body.ProfileId != active.Id) return ApiReply.Error(409, "Das Zielprofil ist nicht aktiv.");
        if (body.Roles!.Any(id => !active.Roles.Any(role => role.Id == id)))
            return ApiReply.Error(404, "Eine Kamera-Rolle fehlt.");
        var target = new QuickText(Guid.Empty, "", body.Chat ? "" : null, body.Next ? "" : null,
            body.Roles!, null, false, null);
        var (ids, missing) = profiles.Targets(target, core.Devices.Select(d => d.Id));
        if (ids.Length == 0) return new(409, new { queued = 0, missingRoles = missing });
        session.SetText(ids, target.Chat, target.Next);
        return new(202, new { queued = ids.Length, missingRoles = missing });
    }
}
