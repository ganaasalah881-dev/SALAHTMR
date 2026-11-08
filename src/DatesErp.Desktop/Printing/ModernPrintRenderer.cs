using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DatesErp.Desktop.Services;

namespace DatesErp.Desktop.Printing;

/// <summary>Compact alternate business template. It consumes the same immutable PrintSpec as the previous template.</summary>
public static class ModernPrintRenderer
{
    private const double Margin = 24;
    private const double LineHeight = 15;
    private const double CellPadding = 4;
    private const double FontSize = 10.5;
    private const double BottomMargin = 42;

    private static readonly Brush Ink = BrushOf("#172B4D");
    private static readonly Brush Muted = BrushOf("#52657A");
    private static readonly Brush Navy = BrushOf("#102A43");
    private static readonly Brush Teal = BrushOf("#0F766E");
    private static readonly Brush Rule = BrushOf("#C8D4DF");
    private static readonly Brush Stripe = BrushOf("#F4F7FA");
    private static readonly Brush Meta = BrushOf("#F1F5F9");
    private static readonly Brush Gold = BrushOf("#D4A72C");
    private static readonly Brush White = Brushes.White;

    private static Brush BrushOf(string value)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(value);
        brush.Freeze();
        return brush;
    }

    public static FixedDocument Build(PrintSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        double width = spec.Landscape ? PrintLayout.A4Height : PrintLayout.A4Width;
        double height = spec.Landscape ? PrintLayout.A4Width : PrintLayout.A4Height;
        double contentWidth = width - Margin * 2;
        double headerY = 18;
        double headerWidth = contentWidth;
        double titleWidth = Math.Max(180, contentWidth - 295);
        var title = PrintRenderer.Text(spec.Title, titleWidth - 18, 15, true, White, TextAlignment.Center, 18);
        double headerHeight = Math.Max(76, title.Height + 43);
        double headerBottom = headerY + headerHeight + 12;
        var company = PrintRenderer.Text(spec.Company, 205, 12, true, White, TextAlignment.Right, 15);
        var companyDetails = string.Join("  ·  ", new[]
        {
            spec.Number,
            spec.CapturedAt.ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("ar-YE")),
            spec.Status
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var details = PrintRenderer.Text(companyDetails, titleWidth - 20, 9.5, false, BrushOf("#D8E5F0"), TextAlignment.Center, 12);
        var companyMetaText = PrintRenderer.Text("نظام التصنيع وإدارة العمليات", 205, 9, false, BrushOf("#D8E5F0"), TextAlignment.Right, 12);
        var pages = PrintLayout.Paginate(spec, headerBottom,
            (text, cellWidth, fontSize, bold) => Math.Max(1, (int)Math.Ceiling(
                PrintRenderer.Text(text, cellWidth, fontSize, bold, Ink, TextAlignment.Right, LineHeight).Height / LineHeight)),
            Margin, LineHeight, CellPadding, FontSize, BottomMargin);

        BitmapImage logo = LoadLogo(spec.Logo);
        var document = new FixedDocument();
        document.DocumentPaginator.PageSize = new Size(width, height);
        for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(White, null, new Rect(0, 0, width, height));
                // Compact identity strip: document type, number, status and company in one bounded band.
                dc.DrawRoundedRectangle(Navy, null, new Rect(Margin, headerY, headerWidth, headerHeight), 8, 8);
                dc.DrawRectangle(Teal, null, new Rect(Margin, headerY + headerHeight - 4, headerWidth, 4));
                double rightX = width - Margin - 217;
                dc.DrawText(company, new Point(rightX, headerY + 13));
                dc.DrawText(companyMetaText, new Point(rightX, headerY + 34));
                double titleX = Margin + 70;
                dc.DrawText(title, new Point(titleX + 9, headerY + 8));
                dc.DrawText(details, new Point(titleX + 10, headerY + title.Height + 13));
                if (logo != null)
                {
                    double scale = Math.Min(50 / logo.Width, 50 / logo.Height);
                    dc.DrawImage(logo, new Rect(Margin + 13, headerY + (headerHeight - logo.Height * scale) / 2 - 2,
                        logo.Width * scale, logo.Height * scale));
                }
                else
                {
                    dc.DrawRoundedRectangle(BrushOf("#1C4666"), new Pen(BrushOf("#6688A3"), 1),
                        new Rect(Margin + 12, headerY + 15, 48, 44), 5, 5);
                    dc.DrawText(PrintRenderer.Text("ERP", 44, 10, true, White, TextAlignment.Center, 12), new Point(Margin + 14, headerY + 29));
                }

                foreach (var slice in pages[pageIndex].Slices)
                    DrawSlice(dc, slice, width, contentWidth);

                double footerY = height - 28;
                dc.DrawLine(new Pen(Rule, 0.8), new Point(Margin, footerY - 5), new Point(width - Margin, footerY - 5));
                dc.DrawText(PrintRenderer.Text($"{spec.Company}  ·  نسخة محفوظة للطباعة  ·  {BuildInfo.Stamp}", contentWidth - 130,
                    8, false, Muted, TextAlignment.Right, 10), new Point(Margin, footerY));
                dc.DrawText(PrintRenderer.Text($"صفحة {pageIndex + 1} من {pages.Count}", 100, 8, true, Muted, TextAlignment.Left, 10),
                    new Point(width - Margin - 100, footerY));
            }
            var page = new FixedPage { Width = width, Height = height, Background = White, FlowDirection = FlowDirection.LeftToRight };
            page.Children.Add(new DrawingPage(visual, width, height));
            page.Measure(new Size(width, height));
            page.Arrange(new Rect(0, 0, width, height));
            page.UpdateLayout();
            var content = new PageContent();
            ((IAddChild)content).AddChild(page);
            document.Pages.Add(content);
        }
        return document;
    }

    private static void DrawSlice(DrawingContext dc, PrintSlice slice, double pageWidth, double contentWidth)
    {
        bool bold = slice.Style is "header" or "title" or "total" or "signature";
        Brush background = slice.Style switch
        {
            "header" => Navy,
            "title" => BrushOf("#E8F0F6"),
            "total" => BrushOf("#FFF6DB"),
            "meta" => Meta,
            "signature" => White,
            "kpi" => White,
            _ => slice.RowIndex % 2 == 0 ? White : Stripe
        };
        if (slice.Style == "title")
        {
            dc.DrawRoundedRectangle(background, null, new Rect(Margin, slice.Y, contentWidth, slice.Height), 4, 4);
            dc.DrawRectangle(Teal, null, new Rect(pageWidth - Margin - 3, slice.Y + 2, 3, Math.Max(1, slice.Height - 4)));
        }
        if (slice.Style == "total")
            dc.DrawLine(new Pen(Gold, 1.2), new Point(Margin, slice.Y), new Point(pageWidth - Margin, slice.Y));
        if (slice.Style == "signature")
            dc.DrawLine(new Pen(Rule, 0.8), new Point(Margin, slice.Y), new Point(pageWidth - Margin, slice.Y));
        if (slice.Style == "meta")
            dc.DrawRoundedRectangle(Meta, new Pen(Rule, 0.7), new Rect(Margin, slice.Y, contentWidth, slice.Height), 4, 4);

        double x = pageWidth - Margin;
        for (int column = 0; column < slice.Cells.Length; column++)
        {
            x -= slice.Widths[column];
            var cell = new Rect(x, slice.Y, slice.Widths[column], slice.Height);
            if (slice.Style is not ("kpi" or "title"))
                dc.DrawRectangle(background, new Pen(Rule, 0.65), cell);
            if (slice.Style == "signature")
            {
                var lines = (slice.Cells[column] ?? "").Split('\n');
                for (int line = 0; line < slice.LineCount; line++)
                {
                    int index = slice.FirstLine + line;
                    if (index >= lines.Length) break;
                    var brush = line == 0 ? Navy : Muted;
                    dc.DrawText(PrintRenderer.Text(lines[index], slice.Widths[column] - 2 * slice.CellPadding,
                        line == 0 ? 9.5 : 8.5, line == 0, brush, TextAlignment.Center, LineHeight),
                        new Point(x + slice.CellPadding, slice.Y + slice.CellPadding + line * slice.LayoutLineHeight));
                }
                continue;
            }

            Brush foreground = slice.Style == "header" ? White
                : slice.Style == "title" ? Navy
                : slice.Style == "total" ? BrushOf("#7A4B00")
                : slice.Style == "meta" && column % 2 == 0 ? Muted : Ink;
            double fontSize = slice.Style == "title" ? 10.5
                : slice.Style == "header" ? 9.5
                : slice.Style == "meta" ? (column % 2 == 0 ? 8.5 : 9.5)
                : slice.Style == "total" ? 9.5 : slice.FontSize;
            bool cellBold = slice.Style == "title" || slice.Style == "header" || slice.Style == "total"
                || slice.Style == "meta" && column % 2 == 1;
            var text = PrintRenderer.Text(slice.Cells[column], slice.Widths[column] - 2 * slice.CellPadding,
                fontSize, cellBold, foreground, slice.Style == "header" ? TextAlignment.Center : TextAlignment.Right, LineHeight);
            dc.PushClip(new RectangleGeometry(new Rect(x + slice.CellPadding, slice.Y + slice.CellPadding,
                Math.Max(1, slice.Widths[column] - 2 * slice.CellPadding), Math.Max(1, slice.LineCount * slice.LayoutLineHeight))));
            double offset = slice.FirstLine * slice.LayoutLineHeight;
            if (column == 0 && slice.Cells.Length > 1 && text.Height <= slice.LayoutLineHeight) offset = 0;
            dc.DrawText(text, new Point(x + slice.CellPadding, slice.Y + slice.CellPadding - offset));
            dc.Pop();
        }
    }

    private static BitmapImage LoadLogo(byte[] bytes)
    {
        if (bytes is not { Length: > 0 }) return null;
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
            return image;
        }
        catch { return null; }
    }

    private sealed class DrawingPage : FrameworkElement
    {
        private readonly DrawingVisual _visual;
        public DrawingPage(DrawingVisual visual, double width, double height)
        {
            _visual = visual; Width = width; Height = height; AddVisualChild(visual);
        }
        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => index == 0 ? _visual : throw new ArgumentOutOfRangeException(nameof(index));
    }
}
