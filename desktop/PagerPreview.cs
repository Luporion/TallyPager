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
        bool canPresent = report.Firmware?.Supports("presentation") == true;
        bool revised = report.Firmware?.Supports(FirmwareInfo.TallyLabels) == true;
        bool showOwn = !revised || display.ShowOwnTally;
        bool beamer = report.Online && canPresent && display.Beamer;
        if (beamer && !revised)
        {
            e.Graphics.FillRectangle(Brushes.Black, 143, 18, 97, 12);
            e.Graphics.DrawString("LIVE auf BEAMER", small, Brushes.White, 147, 19);
        }
        using var centered = new StringFormat { Alignment = StringAlignment.Center };
        if (beamer && revised)
        {
            e.Graphics.FillRectangle(Brushes.Yellow, 132, 18, 105, 39);
            e.Graphics.DrawString("LIVE", large, Brushes.Black, new RectangleF(132, 19, 105, 16), centered);
            e.Graphics.DrawString("auf BEAMER", small, Brushes.Black, new RectangleF(132, 34, 105, 10), centered);
            e.Graphics.FillRectangle(Brushes.Black, 172, 43, 20, 9);
            e.Graphics.FillEllipse(Brushes.Yellow, 185, 45, 4, 4);
            e.Graphics.DrawLine(Pens.Black, 174, 52, 174, 54);
            e.Graphics.DrawLine(Pens.Black, 190, 52, 190, 54);
            e.Graphics.DrawLine(Pens.Black, 194, 45, 199, 43);
            e.Graphics.DrawLine(Pens.Black, 194, 49, 199, 51);
        }
        if (report.Online && display.Test)
        {
            if (beamer && revised)
            {
                e.Graphics.FillRectangle(Brushes.Black, 6, 21, 120, 22);
                e.Graphics.DrawString("Simulationssignal", small, Brushes.White, 12, 23);
                e.Graphics.DrawString("vom Rechner", small, Brushes.White, 12, 33);
            }
            else
            {
                e.Graphics.FillRectangle(Brushes.Black, 6, 30, 228, 12);
                e.Graphics.DrawString("Simulationssignal vom Rechner", small, Brushes.White, 12, 31);
            }
        }
        string message = report.Online ? display.Chat : "Core offline";
        int y = beamer && revised ? 58 : report.Online && (display.Test || beamer) ? 46 : 28;
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
            int end = revised ? 88 : 80;
            int bigLines = revised ? (end - y) / 17 : y == 28 ? 3 : 2;
            bool compact = message.Length > bigLines * 17;
            int columns = compact ? 36 : 17, lineHeight = compact ? 9 : 17;
            int limit = columns * (compact ? (end - y) / lineHeight : bigLines);
            for (int i = 0; i < Math.Min(message.Length, limit); i += columns)
                e.Graphics.DrawString(message.Substring(i, Math.Min(columns, message.Length - i)),
                    compact ? small : large, Brushes.White, 12, y + i / columns * lineHeight);
        }
        string outerOwn = showOwn && display.OutsideSource != "off" ? $"{display.OutsideSource}: {Tallies[outside]}" : "";
        string otherOutside = report.Online && canPresent && outside != 3 ? display.OutsideLive : "";
        if (revised && otherOutside.Length > 0 && outerOwn.Length * 6 > 100)
            outerOwn = outerOwn.Replace("Tally Arbiter:", "Arbiter:", StringComparison.Ordinal);
        if (showOwn && display.InsideSource != "off")
            e.Graphics.DrawString($"{display.InsideSource}: {Tallies[inside]}", small, Brushes.White, 12, revised ? 102 : 82);
        if (outerOwn.Length > 0)
            e.Graphics.DrawString(outerOwn, small, Brushes.White, revised ? 6 : 12, revised ? 123 : 92);
        e.Graphics.DrawString(clock, small, Brushes.White, 201, 123);
        if (report.Online && canPresent && inside != 3)
            e.Graphics.DrawString(display.InsideLive, small, Brushes.White, new RectangleF(6, revised ? 90 : 104, 228, 10), centered);
        if (otherOutside.Length > 0)
        {
            int left = outerOwn.Length > 0 ? 10 + outerOwn.Length * 6 : 6;
            int width = revised ? 2 * Math.Min(120 - left, clock.Length > 0 ? 77 : 114) : 228;
            if (width > 0)
            {
                int maxChars = width / 6;
                if (otherOutside.Length > maxChars)
                {
                    int separator = otherOutside.IndexOf(": ", StringComparison.Ordinal);
                    if (separator >= 0) otherOutside = otherOutside[(separator + 2)..];
                    if (otherOutside.Length > maxChars)
                        otherOutside = maxChars > 3 ? otherOutside[..(maxChars - 3)] + "..." : "";
                }
                e.Graphics.DrawString(otherOutside, small, Brushes.White,
                    new RectangleF(120 - width / 2f, revised ? 123 : 114, width, 10), centered);
            }
        }
        if (report.Online && (!canPresent || display.TextMode == 0) && display.RequireAck && display.ChatId.Length > 0 && report.CanAck)
            e.Graphics.DrawString(report.AcknowledgedAt is null ? "B: quittieren" : "Quittiert",
                small, Brushes.White, revised ? 149 : 12, revised ? 102 : 125);
        e.Graphics.ResetTransform();
        e.Graphics.DrawString(report.PreviewReceived ? "Anzeige vom Stick bestätigt" : "Vorschau · Anzeige unbestätigt",
            Font, Brushes.DimGray, 0, 135 * scale + 2);
    }
}
