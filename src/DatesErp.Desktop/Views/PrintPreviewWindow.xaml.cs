using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using DatesErp.Desktop.Printing;
using DatesErp.Desktop.Services;

namespace DatesErp.Desktop.Views;

/// <summary>Single preview window; operational documents can switch between the previous and modern print templates.</summary>
public partial class PrintPreviewWindow : Window
{
    private sealed record TemplateOption(PrintTemplate Template, string Title);
    private FixedDocument _doc;
    private PhaseDocModel _model;

    /// <summary>Compatibility route for reports and specialized documents that already provide a fixed document.</summary>
    public PrintPreviewWindow(FixedDocument doc, string title)
    {
        ArgumentNullException.ThrowIfNull(doc);
        InitializeComponent();
        PreviewTitle.Text = title;
        PrintVersionText.Text = $"نظام التصنيع — الإصدار {BuildInfo.Stamp}";
        Title = "معاينة قبل الطباعة — " + title;
        TemplateChoicePanel.Visibility = Visibility.Collapsed;
        SetDocument(doc);
        AttachZoomSync();
        Loaded += (_, _) => Fit_Click(this, new RoutedEventArgs());
    }

    /// <summary>Operational document route; template choice is explicit and both templates consume the same saved-data snapshot.</summary>
    public PrintPreviewWindow(PhaseDocModel model, string title)
    {
        ArgumentNullException.ThrowIfNull(model);
        InitializeComponent();
        PreviewTitle.Text = title;
        PrintVersionText.Text = $"نظام التصنيع — الإصدار {BuildInfo.Stamp}";
        Title = "معاينة قبل الطباعة — " + title;
        TemplateChoicePanel.Visibility = Visibility.Visible;
        TemplatePicker.ItemsSource = Enum.GetValues<PrintTemplate>()
            .Select(template => new TemplateOption(template, PrintTemplateNames.Display(template))).ToList();
        TemplatePicker.DisplayMemberPath = nameof(TemplateOption.Title);
        _model = model;
        // Match the approved attached forms immediately (green framed, A4, ruled tables).
        // The modern layout remains available as an intentional alternative, never as the default.
        TemplatePicker.SelectedItem = TemplatePicker.Items.Cast<TemplateOption>()
            .First(option => option.Template == PrintTemplate.Previous);
        AttachZoomSync();
        Loaded += (_, _) => { if (_doc != null) Fit_Click(this, new RoutedEventArgs()); };
    }

    private void AttachZoomSync()
    {
        var zoomProperty = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(DocumentViewer.ZoomProperty, typeof(DocumentViewer));
        EventHandler syncZoom = (_, _) =>
        {
            double value = Math.Clamp(Viewer.Zoom, 40, 300);
            if (Math.Abs(ZoomSlider.Value - value) > 0.001) ZoomSlider.Value = value;
            if (Math.Abs(Viewer.Zoom - value) > 0.001) Viewer.Zoom = value;
            ZoomLabel.Text = $"{value:0}%";
        };
        zoomProperty.AddValueChanged(Viewer, syncZoom);
        Closed += (_, _) => zoomProperty.RemoveValueChanged(Viewer, syncZoom);
    }

    private void TemplatePicker_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_model == null || TemplatePicker.SelectedItem is not TemplateOption option) return;
        try
        {
            SetDocument(PhasePrint.Build(_model, option.Template));
            if (IsLoaded) Fit_Click(this, new RoutedEventArgs());
        }
        catch (Exception ex)
        {
            SetDocument(null);
            MessageBox.Show(this, "تعذّر إنشاء المعاينة بالنموذج المحدد.\n" + ex.Message,
                "معاينة الطباعة", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SetDocument(FixedDocument doc)
    {
        _doc = doc;
        Viewer.Document = doc;
        bool available = doc != null && doc.Pages.Count > 0;
        PreviewPlaceholder.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        PrintBtn.IsEnabled = PdfBtn.IsEnabled = available;
        PageCountText.Text = available ? doc.Pages.Count.ToString() : "—";
    }

    private void ViewerPrint_Executed(object sender, ExecutedRoutedEventArgs e) { e.Handled = true; Print_Click(sender, e); }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        try
        {
            bool landscape = _doc.DocumentPaginator.PageSize.Width > _doc.DocumentPaginator.PageSize.Height;
            var dialog = new PrintDialog
            {
                UserPageRangeEnabled = true,
                MinPage = 1,
                MaxPage = (uint)_doc.Pages.Count,
                PrintTicket = new PrintTicket
                {
                    PageMediaSize = new PageMediaSize(PageMediaSizeName.ISOA4),
                    PageOrientation = landscape ? PageOrientation.Landscape : PageOrientation.Portrait
                }
            };
            if (dialog.ShowDialog() != true) return;
            // Respect user-selected paper/orientation AFTER the dialog; do not silently overwrite it.
            var capabilities = dialog.PrintQueue.GetPrintCapabilities(dialog.PrintTicket);
            var area = capabilities.PageImageableArea;
            double x = area?.OriginWidth ?? 0, y = area?.OriginHeight ?? 0;
            double width = area?.ExtentWidth ?? dialog.PrintableAreaWidth, height = area?.ExtentHeight ?? dialog.PrintableAreaHeight;
            var source = _doc.DocumentPaginator;
            var fit = PrintLayout.Fit(source.PageSize.Width, source.PageSize.Height, x, y, width, height);
            int first = dialog.PageRangeSelection == PageRangeSelection.UserPages ? Math.Clamp(dialog.PageRange.PageFrom, 1, _doc.Pages.Count) : 1;
            int last = dialog.PageRangeSelection == PageRangeSelection.UserPages ? Math.Clamp(dialog.PageRange.PageTo, first, _doc.Pages.Count) : _doc.Pages.Count;
            var size = new Size(Math.Max(x + width, capabilities.OrientedPageMediaWidth ?? 0), Math.Max(y + height, capabilities.OrientedPageMediaHeight ?? 0));
            dialog.PrintDocument(new PrinterPaginator(source, fit, size, first - 1, last - first + 1), PreviewTitle.Text);
        }
        catch (Exception ex) { MessageBox.Show(this, "تعذرت الطباعة؛ لم تتغير المعاينة.\n" + ex.Message, "الطباعة", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private sealed class PrinterPaginator(DocumentPaginator source, PrintPlacement fit, Size size, int first, int count) : DocumentPaginator
    {
        public override bool IsPageCountValid => true;
        public override int PageCount => count;
        public override Size PageSize { get => size; set { } }
        public override IDocumentPaginatorSource Source => source.Source;
        public override DocumentPage GetPage(int number)
        {
            if (number < 0 || number >= count) return DocumentPage.Missing;
            var page = source.GetPage(number + first);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(size));
                dc.DrawRectangle(new VisualBrush(page.Visual) { Stretch = Stretch.Fill }, null,
                    new Rect(fit.X, fit.Y, page.Size.Width * fit.Scale, page.Size.Height * fit.Scale));
            }
            return new DocumentPage(visual, size, new Rect(size), new Rect(size));
        }
    }

    private void Pdf_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "ملف PDF|*.pdf", FileName = MakeSafe(PreviewTitle.Text) + ".pdf" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            PrintRenderer.ExportPdf(_doc, dialog.FileName, PreviewTitle.Text);
            AppContainer.Get<DialogService>().Toast("تم حفظ النسخة المطابقة للمعاينة:\n" + dialog.FileName);
        }
        catch (Exception ex) { MessageBox.Show(this, "تعذر حفظ PDF:\n" + ex.Message, "PDF", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { Mouse.OverrideCursor = null; }
    }

    private void ApplyZoom(double value)
    {
        if (Viewer == null || _doc == null) return;
        Viewer.Zoom = value;
        if (ZoomLabel != null) ZoomLabel.Text = $"{value:0}%";
    }
    private void Zoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyZoom(e.NewValue);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomSlider.Value = Math.Min(300, ZoomSlider.Value + 20);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomSlider.Value = Math.Max(40, ZoomSlider.Value - 20);
    private void Reset_Click(object sender, RoutedEventArgs e) => ZoomSlider.Value = 100;
    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        Viewer.FitToWidth();
        ZoomSlider.Value = Math.Clamp(Viewer.Zoom, 40, 300);
    }
    private void Previous_Click(object sender, RoutedEventArgs e) { if (_doc != null) Viewer.PreviousPage(); }
    private void Next_Click(object sender, RoutedEventArgs e) { if (_doc != null) Viewer.NextPage(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private static string MakeSafe(string value)
    {
        var safe = string.Concat((value ?? "").Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(safe) ? "document" : safe;
    }
}
