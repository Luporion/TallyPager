using System.Diagnostics;
using System.Globalization;

namespace TallyPager;

internal sealed partial class MainWindow
{
    private void OpenUpdates()
    {
        if (busy) return;
        var current = Version.Parse(PagerUsb.Version);
        using var dialog = new Form
        {
            Text = "TallyPager-Updates", Font = Font, StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(710, 220), MinimumSize = new Size(610, 255), MinimizeBox = false, MaximizeBox = false
        };
        var page = Grid(dialog, $"TallyPager {current}");
        var status = Notice();
        var location = new TextBox { ReadOnly = true };
        var check = Action("Nach Updates suchen");
        var load = Action("Programm und Firmware laden");
        var start = Action("Neue Version starten");
        var folder = Action("Downloadordner öffnen");
        var close = Action("Schließen");
        Full(page, status);
        Row(page, "Downloadordner", location, folder);
        Full(page, Buttons(check, load, start, close));
        using var http = GitHubUpdates.OpenClient();
        var updater = new GitHubUpdates(http);
        GitHubRelease? release = null;
        InstalledUpdate? installed = null;
        CancellationTokenSource? active = null;
        bool wantsRestart = false;
        bool wantsClose = false;

        void UpdateButtons()
        {
            bool idle = active is null;
            check.Enabled = idle;
            load.Enabled = idle && release is not null && GitHubUpdates.ReleaseVersion(release) >= current;
            start.Enabled = idle && installed is not null && release is not null && GitHubUpdates.ReleaseVersion(release) > current;
            folder.Enabled = installed is not null;
            close.Text = idle ? "Schließen" : "Abbrechen";
        }

        async Task Run(Func<CancellationToken, Task> work)
        {
            if (active is not null) return;
            using var operation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            active = operation;
            UpdateButtons();
            try { await work(operation.Token); }
            catch (OperationCanceledException)
            {
                status.Text = wantsClose ? "Abgebrochen." : "Die Update-Anfrage wurde abgebrochen oder hat zu lange gedauert.";
            }
            catch (Exception error) { status.Text = "Update fehlgeschlagen · " + error.Message; }
            finally
            {
                active = null;
                UpdateButtons();
                if (wantsClose) dialog.Close();
            }
        }

        Task Check() => Run(async cancel =>
        {
            status.Text = "Suche auf GitHub nach einem stabilen Release …";
            release = null;
            installed = null;
            location.Clear();
            release = await updater.Check(cancel);
            var version = GitHubUpdates.ReleaseVersion(release);
            status.Text = version > current ? $"Version {version} ist auf GitHub verfügbar." :
                version == current ? $"Version {current} ist aktuell. Die Release-Dateien können erneut geladen werden." :
                $"Auf GitHub ist {version} veröffentlicht. Deine App {current} ist neuer.";
        });

        check.Click += async (_, _) => await Check();
        dialog.Shown += async (_, _) => await Check();
        load.Click += async (_, _) =>
        {
            if (release is not { } found) return;
            await Run(async cancel =>
            {
                var progress = new Progress<string>(value => { if (active is not null) status.Text = value; });
                string root = UpdateRoot(AppContext.BaseDirectory, current);
                try
                {
                    installed = await Task.Run(() => updater.Download(found, root, progress, cancel), cancel);
                }
                catch (UnauthorizedAccessException)
                {
                    root = Path.Combine(core.StorageDirectory, "Releases");
                    installed = await Task.Run(() => updater.Download(found, root, progress, cancel), cancel);
                }
                location.Text = Path.GetDirectoryName(installed.Executable);
                if (GitHubUpdates.ReleaseVersion(found) == current)
                {
                    firmware.Text = installed.Firmware;
                    status.Text = "Programm und Firmware geprüft. Das ZIP ist unter Stick einrichten/verwalten ausgewählt.";
                }
                else status.Text = "Beide Dateien sind geprüft. Die neue Version kann jetzt gestartet werden.";
            });
        };
        folder.Click += (_, _) =>
        {
            if (installed is null) return;
            try { Process.Start(new ProcessStartInfo(Path.GetDirectoryName(installed.Executable)!) { UseShellExecute = true }); }
            catch (Exception error) { status.Text = error.Message; }
        };
        start.Click += async (_, _) =>
        {
            if (installed is not { } nextRelease) return;
            await Run(async cancel =>
            {
                if (!await Task.Run(() => GitHubUpdates.HashMatches(nextRelease.Executable, nextRelease.ExeHash), cancel))
                    throw new InvalidDataException("Die heruntergeladene EXE wurde verändert. Bitte erneut laden.");
                cancel.ThrowIfCancellationRequested();
                wantsRestart = wantsClose = true;
            });
        };
        close.Click += (_, _) => dialog.Close();
        dialog.FormClosing += (_, e) =>
        {
            if (active is null) return;
            e.Cancel = true;
            wantsClose = true;
            active.Cancel();
        };
        UpdateButtons();
        dialog.ShowDialog(this);
        if (wantsRestart && installed is { } next && !busy)
        {
            try
            {
                using var running = Process.GetCurrentProcess();
                var launch = new ProcessStartInfo(next.Executable)
                    { WorkingDirectory = Path.GetDirectoryName(next.Executable)!, UseShellExecute = false };
                launch.ArgumentList.Add("--wait-for-tallypager");
                launch.ArgumentList.Add(running.Id.ToString(CultureInfo.InvariantCulture));
                launch.ArgumentList.Add(running.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
                using var started = Process.Start(launch) ?? throw new InvalidOperationException("Die neue App konnte nicht gestartet werden.");
                Close();
            }
            catch (Exception error) { MessageBox.Show(this, error.Message, "TallyPager-Update"); }
        }
    }

    internal static string UpdateRoot(string applicationDirectory, Version current)
    {
        var directory = new DirectoryInfo(applicationDirectory);
        return directory.Name == current.ToString() && directory.Parent is not null
            ? directory.Parent.FullName : Path.Combine(directory.FullName, "TallyPager-Releases");
    }
}
