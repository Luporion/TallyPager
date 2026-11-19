using System.Drawing.Drawing2D;

namespace TallyPager;

internal sealed class PagerPreview : Control
{
    private static readonly Color[] Colors = [Color.FromArgb(66, 65, 66), Color.RoyalBlue, Color.DarkGreen, Color.Firebrick];
    private static readonly string[] Tallies = ["?", "SAFE", "PREVIEW", "LIVE"];
    private PagerStatus? report;
    private string clock = "";

    public PagerPreview()
    {
        DoubleBuffered = true;
        Height = 155;
        Dock = DockStyle.Fill;
        AccessibleName = "Stick-Vorschau aus dem Core-Zustand";
    }

    public void Update(PagerStatus? current)
    {
        string currentClock = current is { Online: true, Firmware: { } firmware } && firmware.Supports("clock")
            ? DateTime.Now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "";
        if (report?.Id == current?.Id && report?.View == current?.View && report?.Connection == current?.Connection &&
            report?.PreviewReceived == current?.PreviewReceived && report?.Name == current?.Name &&
            report?.Rssi == current?.Rssi && report?.Rtt == current?.Rtt &&
            report?.Firmware == current?.Firmware && report?.Transport == current?.Transport &&
            report?.AcknowledgedAt == current?.AcknowledgedAt && report?.CanAck == current?.CanAck && clock == currentClock) return;
        report = current;
        clock = currentClock;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (report is null)
        {
            e.Graphics.DrawString("Stick-Vorschau", Font, Brushes.Black, 0, 0);
            e.Graphics.DrawString("Einen Stick auswählen.", Font, Brushes.DimGray, 0, 24);
            return;
        }
        var display = report.View;
        float scale = Math.Min(1.5f, Math.Min(Width / 240f, (Height - 20) / 135f));
        if (scale <= 0) return;
        e.Graphics.ScaleTransform(scale, scale);
        e.Graphics.SmoothingMode = SmoothingMode.None;
        using var small = new Font("Consolas", 8, GraphicsUnit.Pixel);
        using var large = new Font("Consolas", 14, GraphicsUnit.Pixel);
        int inside = report.Online ? display.Inside : 0;
        int outside = report.Online ? display.Outside : 0;
        using var border = new SolidBrush(display.OutsideSource == "off" ? Color.Black : Colors[outside]);
        using var center = new SolidBrush(display.InsideSource == "off" ? Color.Black : Colors[inside]);
        e.Graphics.FillRectangle(border, 0, 0, 240, 135);
        e.Graphics.FillRectangle(center, 6, 18, 228, 100);
        string title = report.Online && display.Next.Length > 0 ? "next: " + display.Next : report.Name;
        e.Graphics.DrawString(title[..Math.Min(19, title.Length)], small, Brushes.White, 5, 5);
        string link = report.Connection switch { Connection.Connected => "", Connection.Delayed => "Delayed", _ => "Getrennt" };
        if (report.Online && report.Rtt is int rtt) link = $"{link} {rtt}ms".TrimStart();
        e.Graphics.FillRectangle(Brushes.Black, 126, 0, 114, 18);
        if (report.Transport == "udp")
        {
            int bars = report.Rssi switch { >= -55 => 4, >= -67 => 3, >= -75 => 2, >= -82 => 1, _ => 0 };
            for (int i = 0; i < 4; i++)
                e.Graphics.FillRectangle(i < bars ? Brushes.LimeGreen : Brushes.DimGray, 128 + i * 5, 11 - i * 2, 3, 3 + i * 2);
        }
        e.Graphics.DrawString(link, small, Brushes.White, report.Transport == "udp" ? 148 : 128, 5);
        if (display.InsideSource != "off") e.Graphics.DrawString($"{display.InsideSource}: {Tallies[inside]}", small, Brushes.White, 12, 82);
        if (display.OutsideSource != "off") e.Graphics.DrawString($"{display.OutsideSource}: {Tallies[outside]}", small, Brushes.White, 12, 92);
        bool canPresent = report.Firmware?.Supports("presentation") == true;
        bool beamer = report.Online && canPresent && display.Beamer;
        if (beamer)
        {
            e.Graphics.FillRectangle(Brushes.Black, 143, 18, 97, 12);
            e.Graphics.DrawString("LIVE auf BEAMER", small, Brushes.White, 147, 19);
        }
        if (report.Online && display.Test)
        {
            e.Graphics.FillRectangle(Brushes.Black, 6, 30, 228, 12);
            e.Graphics.DrawString("Simulationssignal vom Rechner", small, Brushes.White, 12, 31);
        }
        using var centered = new StringFormat { Alignment = StringAlignment.Center };
        if (report.Online)
        {
            if (canPresent)
            {
                e.Graphics.DrawString(display.InsideLive, small, Brushes.White, new RectangleF(6, 104, 228, 10), centered);
                e.Graphics.DrawString(display.OutsideLive, small, Brushes.White, new RectangleF(6, 114, 228, 10), centered);
            }
            if ((!canPresent || display.TextMode == 0) && display.RequireAck && display.ChatId.Length > 0 && report.CanAck)
                e.Graphics.DrawString(report.AcknowledgedAt is null ? "B: quittieren" : "Quittiert", small, Brushes.White, 12, 125);
        }
        e.Graphics.DrawString(clock, small, Brushes.White, 201, 122);
        string message = report.Online ? display.Chat : "Core offline";
        int y = report.Online && (display.Test || beamer) ? 46 : 28;
        if (report.Online && canPresent && display.TextMode != 0)
        {
            int tally = display.TextMode == 1 ? (display.OutsideSource != "off" ? outside : inside) :
                display.InsideSource != "off" ? inside : outside;
            using var tallyFont = new Font("Consolas", 27, FontStyle.Bold, GraphicsUnit.Pixel);
            e.Graphics.FillRectangle(Brushes.Black, 8, y, 224, 30);
            e.Graphics.DrawString(Tallies[tally], tallyFont, Brushes.White, new RectangleF(8, y, 224, 32), centered);
        }
        else
        {
            bool compact = message.Length > (y == 28 ? 51 : 34);
            int columns = compact ? 36 : 17, lineHeight = compact ? 9 : 17;
            int limit = compact ? columns * ((80 - y) / lineHeight) : y == 28 ? 51 : 34;
            for (int i = 0; i < Math.Min(message.Length, limit); i += columns)
                e.Graphics.DrawString(message.Substring(i, Math.Min(columns, message.Length - i)),
                    compact ? small : large, Brushes.White, 12, y + i / columns * lineHeight);
        }
        e.Graphics.ResetTransform();
        e.Graphics.DrawString(report.PreviewReceived ? "Anzeige vom Stick bestätigt" : "Vorschau · Anzeige unbestätigt",
            Font, Brushes.DimGray, 0, 135 * scale + 2);
    }
}
