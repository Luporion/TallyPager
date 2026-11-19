using System.Drawing.Drawing2D;

namespace TallyPager;

internal sealed class PagerPreview : Control
{
    private static readonly Color[] Colors = [Color.FromArgb(66, 65, 66), Color.RoyalBlue, Color.DarkGreen, Color.Firebrick];
    private static readonly string[] Tallies = ["?", "SAFE", "PREVIEW", "LIVE"];
    private PagerStatus? report;

    public PagerPreview()
    {
        DoubleBuffered = true;
        Height = 155;
        Dock = DockStyle.Fill;
        AccessibleName = "Stick-Vorschau aus dem Core-Zustand";
    }

    public void Update(PagerStatus? current)
    {
        if (report?.Id == current?.Id && report?.View == current?.View && report?.Connection == current?.Connection &&
            report?.PreviewReceived == current?.PreviewReceived && report?.Name == current?.Name) return;
        report = current;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (report is null)
        {
            e.Graphics.DrawString("Einen Stick für die Vorschau auswählen.", Font, Brushes.DimGray, 0, 0);
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
        string link = report.Connection switch { Connection.Connected => "Verbunden", Connection.Delayed => "Delayed", _ => "Getrennt" };
        e.Graphics.FillRectangle(Brushes.Black, 126, 0, 114, 18);
        e.Graphics.DrawString(link, small, Brushes.White, 148, 5);
        if (display.InsideSource != "off") e.Graphics.DrawString($"{display.InsideSource}: {Tallies[inside]}", small, Brushes.White, 12, 82);
        if (display.OutsideSource != "off") e.Graphics.DrawString($"{display.OutsideSource}: {Tallies[outside]}", small, Brushes.White, 12, 92);
        if (report.Online && display.Beamer)
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
            e.Graphics.DrawString(display.InsideLive, small, Brushes.White, new RectangleF(6, 104, 228, 10), centered);
            e.Graphics.DrawString(display.OutsideLive, small, Brushes.White, new RectangleF(6, 114, 228, 10), centered);
        }
        string message = report.Online ? display.Chat : "Core offline";
        int y = report.Online && (display.Test || display.Beamer) ? 46 : 28;
        if (report.Online && display.TextMode != 0)
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
        e.Graphics.DrawString(report.PreviewReceived ? "Core-Anzeige vom Stick bestätigt" : "Core-Vorschau · Anzeige unbestätigt",
            Font, Brushes.DimGray, 0, 135 * scale + 2);
    }
}
