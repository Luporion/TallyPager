using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TallyPager;

internal sealed partial class MainWindow
{
    private readonly ToolStripStatusLabel adapterSummary = new()
    {
        Spring = true, TextAlign = ContentAlignment.MiddleRight, IsLink = true,
        ToolTipText = "Alle Adapter und IP-Adressen anzeigen"
    };
    private long lastAdapterScan;
    private string adapterDetails = "";

    private StatusStrip NetworkFooter()
    {
        var footer = new StatusStrip
        {
            Dock = DockStyle.Fill, SizingGrip = false, ShowItemToolTips = true, BackColor = BackColor
        };
        footer.Items.Add(adapterSummary);
        footer.SizeChanged += (_, _) => adapterSummary.Text = CompactAdapters(footer.ClientSize.Width - 20);
        adapterSummary.Click += (_, _) => ShowAdapters();
        return footer;
    }

    private string[] adapterLines = [];

    private void ShowAdapters()
    {
        using var dialog = new Form
        {
            Text = "Netzwerkadapter des Basis-Servers", ClientSize = new Size(540, 420),
            StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, ShowInTaskbar = false
        };
        var list = new TextBox
        {
            Multiline = true, Text = adapterDetails.Replace("\n", Environment.NewLine),
            ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill
        };
        var close = Action("Schließen");
        close.DialogResult = DialogResult.OK;
        close.Dock = DockStyle.Bottom;
        dialog.Controls.Add(list);
        dialog.Controls.Add(close);
        dialog.CancelButton = close;
        dialog.ShowDialog(this);
    }

    private string CompactAdapters(int width)
    {
        const string prefix = "Basis-Server · ";
        if (adapterLines.Length == 0) return "Basis-Server · Keine aktive IPv4-Adresse";
        int count = 0;
        string text = prefix + adapterLines[0];
        while (count < adapterLines.Length)
        {
            string candidate = prefix + string.Join("  |  ", adapterLines.Take(count + 1));
            int omitted = adapterLines.Length - count - 1;
            if (omitted > 0) candidate += $"  +{omitted}";
            if (TextRenderer.MeasureText(candidate, Font).Width > width) break;
            text = candidate;
            count++;
        }
        return count == 0 ? "Basis-Server · Netzwerkadapter anzeigen" : text;
    }

    private void RefreshNetworkFooter()
    {
        lastAdapterScan = Environment.TickCount64;
        try
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(adapter => (adapter.Name, adapter.OperationalStatus,
                    Addresses: adapter.GetIPProperties().UnicastAddresses.Select(item => item.Address)
                        .Where(ip => !IPAddress.IsLoopback(ip)).ToArray()))
                .OrderByDescending(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                .ThenBy(adapter => adapter.Name).ToArray();
            var active = adapters.Where(adapter => adapter.OperationalStatus == OperationalStatus.Up);
            adapterDetails = adapters.Length == 0 ? "Windows meldet keine Netzwerkadapter." : string.Join("\n\n",
                adapters.Select(adapter => $"{adapter.Name} · {(adapter.OperationalStatus == OperationalStatus.Up ? "Aktiv" : "Inaktiv")}\n" +
                    (adapter.Addresses.Length == 0 ? "Keine IP-Adresse" : string.Join("\n", adapter.Addresses.AsEnumerable()))));
            adapterLines = active.Select(adapter => (adapter.Name, IPv4: adapter.Addresses
                    .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork).Select(ip => ip.ToString()).ToArray()))
                .Where(adapter => adapter.IPv4.Length > 0)
                .OrderBy(adapter => adapter.IPv4.All(ip => ip.StartsWith("169.254.", StringComparison.Ordinal)))
                .Select(adapter => $"{adapter.Name} {string.Join(", ", adapter.IPv4)}").ToArray();
            adapterSummary.Text = CompactAdapters(ClientSize.Width - 50);

            string[] found = active.SelectMany(adapter => adapter.Addresses)
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                .Select(ip => ip.ToString()).Distinct()
                .OrderByDescending(ip => ip.StartsWith("192.168.", StringComparison.Ordinal)).ToArray();
            RefreshMumbleServerAddresses(found);
            if (addresses.Items.Cast<string>().SequenceEqual(found)) return;
            string current = addresses.Text;
            addresses.BeginUpdate();
            addresses.Items.Clear();
            addresses.Items.AddRange(found);
            addresses.Text = current.Length != 0 ? current : found.FirstOrDefault() ?? "";
            addresses.EndUpdate();
        }
        catch (NetworkInformationException)
        {
            adapterLines = [];
            adapterDetails = "Windows konnte die Netzwerkadapter gerade nicht auslesen.";
            adapterSummary.Text = "Netzwerkadapter · Angaben derzeit nicht verfügbar";
        }
    }
}
