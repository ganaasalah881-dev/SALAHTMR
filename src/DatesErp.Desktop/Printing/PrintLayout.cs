using System.Globalization;

namespace DatesErp.Desktop.Printing;

// Pure layout contract. Linked into the cross-platform test project: no WPF or DB calls.
public sealed class PrintSection
{
    public string Title { get; set; } = "";
    public string[] Columns { get; set; } = Array.Empty<string>();
    public List<string[]> Rows { get; set; } = new();
    public double[] Weights { get; set; }
    public string Style { get; set; } = "body";
}
public sealed class PrintSpec
{
    public string Company { get; set; } = "";
    public string CompanyNameEn { get; set; } = "";
    public string CompanyDepartmentEn { get; set; } = "";
    public string CompanyAddress { get; set; } = "";
    public string CompanyPhone { get; set; } = "";
    public string CompanyTaxNumber { get; set; } = "";
    public byte[] Logo { get; set; }
    public string Title { get; set; } = "";
    public string Number { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime CapturedAt { get; set; } = DateTime.Now;
    public bool Landscape { get; set; }
    public List<PrintSection> Sections { get; set; } = new();
}
public sealed record PrintSlice(string Section, int RowIndex, string[] Cells, double[] Widths,
    double Y, int FirstLine, int LineCount, double FontSize, string Style)
{
    public double LayoutLineHeight { get; init; } = PrintLayout.LineHeight;
    public double CellPadding { get; init; } = PrintLayout.Padding;
    public double Height => LineCount * LayoutLineHeight + CellPadding * 2;
}
public sealed class PrintLayoutPage { public List<PrintSlice> Slices { get; } = new(); }
public sealed record PrintPlacement(double X, double Y, double Scale);
public static class PrintLayout
{
    public const double A4Width = 210 * 96 / 25.4, A4Height = 297 * 96 / 25.4;
    public const double Margin = 32, LineHeight = 20, Padding = 6, FontSize = 13.5;
    public static string Format(object value) => value switch
    {
        null => "—", DateTime dt => dt.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
        double d => d.ToString("N2", CultureInfo.InvariantCulture),
        decimal d => d.ToString("N2", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "—"
    };
    public static double[] ColumnWidths(double available, int count, double[] weights = null)
    {
        if (!double.IsFinite(available) || available <= 0 || count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (weights != null && (weights.Length != count || weights.Any(w => !double.IsFinite(w) || w <= 0)))
            throw new ArgumentException("أوزان أعمدة الطباعة غير صالحة.");
        var w = weights ?? Enumerable.Repeat(1d, count).ToArray();
        return w.Select(x => available * x / w.Sum()).ToArray();
    }
    public static PrintPlacement Fit(double sourceW, double sourceH, double x, double y, double width, double height)
    {
        if (new[] { sourceW, sourceH, width, height }.Any(v => !double.IsFinite(v) || v <= 0)
            || !double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0)
            throw new ArgumentException("مساحة الطباعة غير صالحة.");
        double scale = Math.Min(width / sourceW, height / sourceH);
        return new(x + (width - sourceW * scale) / 2, y + (height - sourceH * scale) / 2, scale);
    }
    // Dimensions are configurable for the modern compact model. Defaults preserve the previous template exactly.
    public static List<PrintLayoutPage> Paginate(PrintSpec spec, double headerBottom,
        Func<string, double, double, bool, int> lineCounter,
        double margin = Margin, double lineHeight = LineHeight, double padding = Padding,
        double fontSize = FontSize, double bottomMargin = 48)
    {
        if (spec == null) throw new ArgumentNullException(nameof(spec));
        if (lineCounter == null) throw new ArgumentNullException(nameof(lineCounter));
        if (new[] { margin, lineHeight, padding, fontSize, bottomMargin, headerBottom }.Any(x => !double.IsFinite(x) || x < 0)
            || lineHeight <= 0 || fontSize <= 0)
            throw new ArgumentException("أبعاد تخطيط الطباعة غير صالحة.");
        double pageW = spec.Landscape ? A4Height : A4Width;
        double pageH = spec.Landscape ? A4Width : A4Height;
        double bottom = pageH - bottomMargin, contentW = pageW - 2 * margin;
        if (contentW <= 0) throw new ArgumentException("هوامش الطباعة أكبر من عرض الورقة.");
        if (headerBottom < margin || headerBottom > bottom - 180) throw new ArgumentException("ترويسة المستند طويلة جدًا؛ اختصر عنوان المستند أو انقل التفاصيل إلى بياناته.");
        var pages = new List<PrintLayoutPage>();
        PrintLayoutPage page = null; double y = headerBottom;
        void NewPage() { page = new(); pages.Add(page); y = headerBottom; if (pages.Count > 1000) throw new InvalidOperationException("المستند تجاوز 1000 صفحة؛ قسّم فترة التقرير."); }
        PrintSlice Slice(string section, int rowIndex, string[] cells, double[] widths, double sliceY, int firstLine, int count, string style)
            => new(section, rowIndex, cells, widths, sliceY, firstLine, count, fontSize, style)
            { LayoutLineHeight = lineHeight, CellPadding = padding };
        NewPage();
        foreach (var section in spec.Sections)
        {
            int cols = section.Columns.Length > 0 ? section.Columns.Length : Math.Max(1, section.Rows.FirstOrDefault()?.Length ?? 1);
            var widths = ColumnWidths(contentW, cols, section.Weights);
            if (widths.Any(w => w < 24)) throw new ArgumentException("أعمدة كثيرة أو ضيقة جدًا؛ استخدم نموذجًا أفقيًا أو قسّم الجدول.");
            int Lines(string[] cells, double[] ws, bool bold, string style = "body")
            {
                int lineCount = cells.Select((v, i) =>
                {
                    double cellWidth = style == "kpi"
                        ? ws[i] + ws[i % 2 == 0 ? i + 1 : i - 1] - padding * 2
                        : ws[i] - padding * 2;
                    int measured = lineCounter(v ?? "", cellWidth, fontSize, bold);
                    return measured > 1 ? measured : 1;
                }).DefaultIfEmpty(1).Max();
                return lineCount > 1 ? lineCount : 1;
            }
            var heading = new List<PrintSlice>();
            if (!string.IsNullOrWhiteSpace(section.Title))
                heading.Add(Slice(section.Title, -2, new[] { section.Title }, new[] { contentW }, 0, 0, Lines(new[] { section.Title }, new[] { contentW }, true), "title"));
            if (section.Columns.Length > 0)
                heading.Add(Slice(section.Title, -1, section.Columns.ToArray(), widths, 0, 0, Lines(section.Columns, widths, true), "header"));
            double hh = heading.Sum(h => h.Height);
            if (hh + lineHeight + 2 * padding > bottom - headerBottom) throw new ArgumentException("عناوين الجدول أطول من مساحة الصفحة.");
            void Headers()
            {
                foreach (var h in heading) { page.Slices.Add(h with { Y = y }); y += h.Height; }
            }
            var firstRow = section.Rows.FirstOrDefault();
            if (firstRow != null && firstRow.Length > cols) throw new ArgumentException("عدد خلايا الصف يتجاوز أعمدة النموذج.");
            var initialCells = Enumerable.Range(0, cols).Select(i => firstRow != null && i < firstRow.Length ? firstRow[i] ?? "" : "").ToArray();
            var firstHeight = Lines(initialCells, widths, section.Style == "total" || section.Style == "signature", section.Style) * lineHeight + 2 * padding;
            if (section.Style == "kpi") firstHeight = Math.Max(firstHeight, 3 * lineHeight + 2 * padding);
            double reserve = Math.Min(firstHeight, bottom - headerBottom - hh);
            if (y + hh + reserve > bottom && page.Slices.Count > 0) NewPage();
            Headers();
            if (section.Rows.Count == 0)
            {
                var empty = Slice(section.Title, 0, new[] { "لا توجد بنود" }, new[] { contentW }, y, 0, 1, section.Style);
                page.Slices.Add(empty); y += empty.Height + 10; continue;
            }
            var rows = section.Rows;
            for (int ri = 0; ri < rows.Count; ri++)
            {
                if (rows[ri].Length > cols) throw new ArgumentException("عدد خلايا الصف يتجاوز أعمدة النموذج.");
                var cells = Enumerable.Range(0, cols).Select(c => c < rows[ri].Length ? rows[ri][c] ?? "" : "").ToArray();
                int lines = Lines(cells, widths, section.Style == "total" || section.Style == "signature", section.Style);
                if (section.Style == "kpi") lines = Math.Max(lines, 3);
                double fullHeight = lines * lineHeight + 2 * padding;
                if (y + fullHeight > bottom && fullHeight <= bottom - headerBottom - hh && y > headerBottom + hh)
                { NewPage(); Headers(); }
                int first = 0;
                while (first < lines)
                {
                    int fit = (int)Math.Floor((bottom - y - 2 * padding) / lineHeight);
                    if (fit < 1) { NewPage(); Headers(); continue; }
                    int take = Math.Min(fit, lines - first);
                    var slice = Slice(section.Title, ri, cells, widths, y, first, take, section.Style);
                    page.Slices.Add(slice); y += slice.Height; first += take;
                    if (first < lines) { NewPage(); Headers(); }
                }
            }
            y += 10;
        }
        return pages;
    }
}
