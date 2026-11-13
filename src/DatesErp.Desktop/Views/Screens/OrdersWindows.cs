using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DatesErp.Core.Common;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using DatesErp.Infrastructure.Session;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// §أمر الإنتاج من الخطة — لا إعادة إدخال: العميل والصنف والمنتج والمخطط تُجلب من الخطة،
/// والمستخدم يحدد فقط كمية التنفيذ من المتبقي والتاريخ والوردية والخط.
/// المتبقي = المخطط − أوامر سابقة، والطاقة تُحسب لحظياً، والتوزيع على عدة ورديات بضغطة واحدة.
/// </summary>
internal static class OrderGridColumns
{
    internal static DataGridTextColumn IdentCol(string header, string path, double star, double minWidth)
    {
        var col = new DataGridTextColumn
        {
            Header = header,
            Width = new DataGridLength(star, DataGridLengthUnitType.Star),
            MinWidth = minWidth,
            IsReadOnly = true,
            Binding = new System.Windows.Data.Binding(path)
        };
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        style.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(TextBlock.ToolTipProperty, new System.Windows.Data.Binding(path)));
        col.ElementStyle = style;
        return col;
    }
}


    // §حُذف غلافَا NewOrderWindow وProductionOrderWindow في B40: لم ينشئهما أي كود —
    // OrdersView يستضيف NewOrderPanel وOrderDocumentPanel مباشرة داخل الشاشة.

/// <summary>§B80 — صف بند أمر قابل للتعديل (مسودة): الكراتين تُعدَّل والوزن المكافئ يُحسب في الخدمة.</summary>
public class OrderItemEditRow
{
    public int LineNo { get; set; }
    public int Id { get; set; }
    public string Customer { get; set; }
    public string Shipment { get; set; }
    public string Warehouse { get; set; }
    public string Lot { get; set; }
    public string Raw { get; set; }
    public string Product { get; set; }
    public string Pack { get; set; }
    public double PlannedKg { get; set; }
    public int PlannedCartons { get; set; }
    /// <summary>الكراتين القابلة للتعديل — تبدأ بالمخطط.</summary>
    public int Cartons { get; set; }
    public double ProducedKg { get; set; }
    public string StatusAr { get; set; }
}

public class OrderDocumentPanel : UserControl
{
    private readonly int _orderId;
    private Window _dialogOwner;
    private readonly TextBlock _title = new() { FontSize = 14, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x16, 0x28, 0x3C)) };
    private readonly WrapPanel _cardPanel = new();
    private readonly ProgressBar _progress = new() { Height = 18, Maximum = 100, Foreground = new SolidColorBrush(Color.FromRgb(0x14, 0x53, 0x2D)) };
    private readonly TextBlock _progressText = new() { FontSize = 12, Margin = new Thickness(0, 4, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(0x14, 0x53, 0x2D)), FontWeight = FontWeights.SemiBold };
    private readonly DataGrid _itemsGrid = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, Height = 155, RowHeight = 30, ColumnHeaderHeight = 34 };
    private readonly TextBox _notesBox = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 44, IsReadOnly = true };
    private List<OrderItemEditRow> _itemRows = new();
    private readonly DataGrid _materialsGrid = new() { AutoGenerateColumns = false, IsReadOnly = true, Height = 115, RowHeight = 28, ColumnHeaderHeight = 32 };
    private readonly DataGrid _eventsGrid = new() { AutoGenerateColumns = false, IsReadOnly = true, Height = 155, RowHeight = 28, ColumnHeaderHeight = 32 };
    private readonly StackPanel _actionsPanel = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
    private OrderCardDto _card;
    // §v1.50.34 — الشريط الكلاسيكي برأس الشاشة: حفظ/تعديل/إضافة/بحث/حذف
    private Button _editBtn, _saveBtn, _deleteBtn;
    private bool _editing;
    /// <summary>يفتح أمراً آخر (نتيجة بحث/إصدار) — تُمرَّر من الشاشة المضيفة.</summary>
    public Action<int> Navigator { get; set; }
    /// <summary>تُستدعى بعد إصدار/حذف ليحدّث المضيف قائمته.</summary>
    public Action Changed { get; set; }
    /// <summary>العودة لقائمة أوامر اليوم بعد الحذف.</summary>
    public Action ReturnToList { get; set; }
    private bool IsDraft => _card?.Status == DocStatuses.Draft;

    private static void StyleShapeGrid(DataGrid g)
    {
        g.Background = Brushes.White;
        g.RowBackground = Brushes.White;
        g.AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(0xFB, 0xFA, 0xF5));
        g.GridLinesVisibility = DataGridGridLinesVisibility.All;
        g.HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
        g.VerticalGridLinesBrush = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
        g.BorderBrush = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
        g.BorderThickness = new Thickness(1);
        var hs = new Style(typeof(DataGridColumnHeader));
        hs.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x16, 0x28, 0x3C))));
        hs.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        hs.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        hs.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7, 5, 7, 5)));
        hs.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        hs.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0x2A, 0x44, 0x70))));
        hs.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
        g.ColumnHeaderStyle = hs;
    }

    public OrderDocumentPanel(int orderId)
    {
        _orderId = orderId;
        FlowDirection = FlowDirection.RightToLeft;
        StyleShapeGrid(_itemsGrid);
        StyleShapeGrid(_materialsGrid);
        StyleShapeGrid(_eventsGrid);

        _itemsGrid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new System.Windows.Data.Binding("LineNo"), Width = 45, IsReadOnly = true });
        _itemsGrid.Columns.Add(OrderGridColumns.IdentCol("العميل المالك 👤", "Customer", 1.1, 130));
        _itemsGrid.Columns.Add(OrderGridColumns.IdentCol("الشحنة 🚢", "Shipment", 0.85, 105));
        _itemsGrid.Columns.Add(OrderGridColumns.IdentCol("المخزن 🏬", "Warehouse", 1.0, 115));
        _itemsGrid.Columns.Add(OrderGridColumns.IdentCol("الدفعة 📦", "Lot", 0.9, 110));
        _itemsGrid.Columns.Add(OrderGridColumns.IdentCol("الصنف الخام المستلم 🌴", "Raw", 1.2, 150));
        _itemsGrid.Columns.Add(OrderGridColumns.IdentCol("المنتج النهائي 🏷️", "Product", 1.3, 155));
        _itemsGrid.Columns.Add(OrderGridColumns.IdentCol("العبوة 📦", "Pack", 0.9, 110));
        _itemsGrid.Columns.Add(new DataGridTextColumn { Header = "المخطط (كجم)", Binding = new System.Windows.Data.Binding("PlannedKg"), Width = 90, IsReadOnly = true });
        _itemsGrid.Columns.Add(new DataGridTextColumn { Header = "المخطط (كرتون)", Binding = new System.Windows.Data.Binding("PlannedCartons"), Width = 95, IsReadOnly = true });
        _itemsGrid.Columns.Add(new DataGridTextColumn { Header = "المنتَج (كجم)", Binding = new System.Windows.Data.Binding("ProducedKg"), Width = 90, IsReadOnly = true });
        _itemsGrid.Columns.Add(new DataGridTextColumn { Header = "الحالة", Binding = new System.Windows.Data.Binding("StatusAr"), Width = 80, IsReadOnly = true });

        _materialsGrid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new System.Windows.Data.Binding("LineNo"), Width = 45, IsReadOnly = true });
        _materialsGrid.Columns.Add(new DataGridTextColumn { Header = "الصنف المساعد", Binding = new System.Windows.Data.Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _materialsGrid.Columns.Add(new DataGridTextColumn { Header = "المطلوب", Binding = new System.Windows.Data.Binding("Required"), Width = 90 });
        _materialsGrid.Columns.Add(new DataGridTextColumn { Header = "المصروف", Binding = new System.Windows.Data.Binding("Issued"), Width = 90 });
        _materialsGrid.Columns.Add(new DataGridTextColumn { Header = "المتبقي", Binding = new System.Windows.Data.Binding("Remaining"), Width = 90 });
        _materialsGrid.Columns.Add(new DataGridTextColumn { Header = "المتاح", Binding = new System.Windows.Data.Binding("Available"), Width = 90 });
        _materialsGrid.Columns.Add(new DataGridTextColumn { Header = "حالة المخزون", Binding = new System.Windows.Data.Binding("StockStatus"), Width = 90 });
        _materialsGrid.Columns.Add(new DataGridTextColumn { Header = "الوحدة", Binding = new System.Windows.Data.Binding("Unit"), Width = 80 });
        _materialsGrid.Columns.Add(new DataGridTextColumn { Header = "التفاصيل", Binding = new System.Windows.Data.Binding("Details"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

        _eventsGrid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new System.Windows.Data.Binding("LineNo"), Width = 45, IsReadOnly = true });
        _eventsGrid.Columns.Add(new DataGridTextColumn { Header = "الوقت", Binding = new System.Windows.Data.Binding("Time"), Width = 130 });
        _eventsGrid.Columns.Add(new DataGridTextColumn { Header = "المستخدم", Binding = new System.Windows.Data.Binding("User"), Width = 140 });
        _eventsGrid.Columns.Add(new DataGridTextColumn { Header = "العملية", Binding = new System.Windows.Data.Binding("Action"), Width = 200 });
        _eventsGrid.Columns.Add(new DataGridTextColumn { Header = "التفاصيل", Binding = new System.Windows.Data.Binding("Detail"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

        // §v1.50.34 — الرأس ثابت (لا يختفي بالتمرير): العنوان + الشريط الكلاسيكي + أزرار الحالة،
        // والمحتوى يُمرَّر تحته. زر الحفظ في رأس الشاشة كما طلب المستخدم.
        var head = new StackPanel { Margin = new Thickness(12, 10, 12, 2) };
        head.Children.Add(_title);
        head.Children.Add(BuildDocTools());
        head.Children.Add(_actionsPanel);
        var body = new StackPanel { Margin = new Thickness(12, 6, 12, 12) };
        body.Children.Add(_cardPanel);
        body.Children.Add(_progress);
        body.Children.Add(_progressText);
        body.Children.Add(Section("📦 بنود الأمر — الهوية كاملة: العميل/الشحنة/الدفعة/الصنف المستلم/المنتج النهائي", _itemsGrid));
        body.Children.Add(Section("🧰 المواد المساعدة المحتسبة", _materialsGrid));
        body.Children.Add(Section("📝 ملاحظات الأمر — تُعدَّل بوضع «✏️ تعديل» وتُحفظ بزر «💾 حفظ» من رأس الشاشة. كميات البنود ثابتة من الخطة المعتمدة.", _notesBox));
        body.Children.Add(Section("📋 سجل العمليات — من فعل ماذا ومتى", _eventsGrid));
        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = dock;

        if (orderId > 0)
        {
            Refresh();
            Loaded += (_, _) => Refresh();
        }
    }

    private static TextBlock Lbl(string t) => new() { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0), FontSize = 11.5 };

    private static Border Section(string header, UIElement body)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = header, FontWeight = FontWeights.Bold, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x24, 0x6A)), Margin = new Thickness(0, 8, 0, 4) });
        sp.Children.Add(body);
        return new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xC8, 0xB0)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8), Margin = new Thickness(0, 6, 0, 0), Background = Brushes.White };
    }

    private bool Can(string module, string action)
    {
        try { return AppContainer.Get<SessionContext>().Can(module, action); }
        catch { return false; }
    }

    private void Refresh()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

            _card = svc.GetOrderCard(_orderId);
            if (_card == null)
            {
                _title.Text = "أمر الإنتاج غير موجود.";
                return;
            }

            _title.Text = $"📝 أمر الإنتاج {_card.OrderNumber} — {_card.StatusAr}";
            _cardPanel.Children.Clear();
            AddCard("العميل", _card.CustomerName);
            AddCard("الصنف المستلم", _card.RawName);
            AddCard("المنتج النهائي", _card.ProductName);
            AddCard("الخطة", _card.PlanNumber);
            AddCard("الدفعة", _card.LotCode);
            AddCard("الشحنة", _card.ShipmentNumber);
            AddCard("التاريخ", _card.ProductionDate);
            AddCard("الوردية", _card.ShiftName);
            AddCard("الخط", _card.LineName);
            AddCard("وقت البداية", _card.StartTime);
            AddCard("النهاية المتوقع", _card.ExpectedEndTime);
            AddCard("المخطط في الخطة", $"{_card.PlannedInPlanKg:N1} كجم / {_card.PlannedInPlanCartons:N0} كرتون");
            AddCard("كمية الأمر", $"{_card.OrderedKg:N1} كجم / {_card.OrderedCartons:N0} كرتون");
            // §v1.50.24: حقول الفعلي/المقبول/المرفوض/المتبقي ليس لها دخل بأمر الإنتاج —
            // أُزيلت من هنا؛ مكانها الطبيعي شاشة تسليم الإنتاج وشاشة الجودة.
            AddCard("المعدل", $"{_card.RatePerHour:N0} كرتون/س — {_card.ExpectedHours:N1} س متوقعة");
            AddCard("أنشأه", $"{_card.CreatedBy} — {_card.CreatedDate}");

            _progress.Value = _card.ProgressPct;
            _progressText.Text = $"شريط التقدم: {_card.ProducedKg:N1} / {_card.OrderedKg:N1} كجم — {_card.ProgressPct:N0}%";

            // البنود بالهوية الكاملة — §B80 صفوف قابلة لتعديل الكراتين لأمر المسودة + دعم تعدد المخازن والدفعات
            var items = db.ProductionOrderItems.AsNoTracking().Include(i => i.Allocations).Where(i => i.OrderId == _orderId).ToList();
            var whNames = db.Warehouses.AsNoTracking().ToDictionary(w => w.Id, w => w.WarehouseNameAr);
            _itemRows = items.Select((it, idx) =>
            {
                var lotIds = it.Allocations.Count > 0
                    ? it.Allocations.Select(a => a.LotId).Distinct().ToList()
                    : (it.LotId != null ? new List<int> { it.LotId.Value } : new List<int>());
                var lots = lotIds.Count > 0 ? db.Lots.AsNoTracking().Where(l => lotIds.Contains(l.Id)).ToList() : new List<DatesErp.Core.Domain.Entities.Lot>();
                var whIds = it.Allocations.Where(a => a.WarehouseId > 0).Select(a => a.WarehouseId).Distinct().ToList();
                if (whIds.Count == 0 && lots.Count > 0)
                {
                    whIds = db.StockBalances.AsNoTracking()
                        .Where(b => lotIds.Contains(b.LotId ?? 0) && b.QtyKg > 0.001)
                        .Select(b => b.WarehouseId).Distinct().ToList();
                }
                string whText = whIds.Count > 0
                    ? string.Join(" + ", whIds.Where(whNames.ContainsKey).Select(id => whNames[id]))
                    : "-";
                string lotText = lots.Count > 0 ? string.Join(" + ", lots.Select(l => l.LotCode)) : "-";
                var rawProdIds = lots.Select(l => l.ProductId).Distinct().ToList();
                string rawText = rawProdIds.Count > 0
                    ? string.Join(" + ", db.Products.AsNoTracking().Where(p => rawProdIds.Contains(p.Id)).Select(p => p.ProductNameAr).ToList())
                    : "-";
                return new OrderItemEditRow
                {
                    LineNo = idx + 1,
                    Id = it.Id,
                    Customer = it.CustomerId != null ? db.Customers.AsNoTracking().Where(c => c.Id == it.CustomerId).Select(c => c.CustomerName).FirstOrDefault() : "-",
                    Shipment = it.ShipmentId != null ? db.Shipments.AsNoTracking().Where(s => s.Id == it.ShipmentId).Select(s => s.DocumentNumber).FirstOrDefault() : "-",
                    Warehouse = string.IsNullOrWhiteSpace(whText) ? "-" : whText,
                    Lot = lotText,
                    Raw = rawText,
                    Product = db.Products.AsNoTracking().Where(p => p.Id == it.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-",
                    Pack = it.PackagingTypeId != null ? db.PackagingTypes.AsNoTracking().Where(p => p.Id == it.PackagingTypeId).Select(p => p.PackageNameAr).FirstOrDefault() : "-",
                    PlannedKg = it.PlannedQtyKg,
                    PlannedCartons = it.PlannedCartons,
                    Cartons = it.PlannedCartons,
                    ProducedKg = it.ProducedQtyKg,
                    StatusAr = DocStatuses.ToArabic(it.Status)
                };
            }).ToList();
            _itemsGrid.ItemsSource = _itemRows;

            try
            {
                var auxSvc = scope.ServiceProvider.GetRequiredService<DatesErp.Application.Services.AuxiliaryManagementService>();
                var needs = auxSvc.CalculateNeedsForOrder(_orderId);
                _materialsGrid.ItemsSource = needs.Select((n, idx) => new
                {
                    LineNo = idx + 1,
                    Name = n.AuxiliaryProductName,
                    Required = n.RequiredQty,
                    Issued = n.IssuedQty,
                    Remaining = n.RemainingQty,
                    Available = n.AvailableQty,
                    StockStatus = n.StockStatusAr,
                    Unit = n.Unit,
                    Details = n.CalculationDetails,
                    AuxiliaryProductId = n.AuxiliaryProductId
                }).ToList();
            }
            catch
            {
                // fallback للقديم
                _materialsGrid.ItemsSource = db.ProductionOrderMaterials.AsNoTracking().Where(m => m.OrderId == _orderId).ToList()
                    .Select((m, idx) => new
                    {
                        LineNo = idx + 1,
                        Name = m.AuxiliaryProductId != null ? db.Products.Where(p => p.Id == m.AuxiliaryProductId).Select(p => p.ProductNameAr).FirstOrDefault() : db.AuxiliaryMaterials.AsNoTracking().Where(a => a.Id == m.MaterialId).Select(a => a.MaterialNameAr).FirstOrDefault(),
                        Required = m.CalculatedQty,
                        Issued = m.ActualIssuedQty,
                        Remaining = m.CalculatedQty - m.ActualIssuedQty,
                        Available = 0.0,
                        StockStatus = "-",
                        Unit = m.UnitOfMeasure,
                        Details = "",
                        AuxiliaryProductId = m.AuxiliaryProductId
                    }).ToList();
            }

            var events = svc.GetOrderEvents(_orderId);
            _eventsGrid.ItemsSource = events?.Select((e, idx) => new { LineNo = idx + 1, e.Time, e.User, e.Action, e.Detail }).ToList();
            _notesBox.Text = db.ProductionOrders.AsNoTracking().Where(o => o.Id == _orderId).Select(o => o.Notes).FirstOrDefault() ?? "";

            BuildActions();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "OrderWindow.Refresh"); }
    }

    private void AddCard(string label, string value)
    {
        var b = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF2, 0xE8)),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 4, 6, 2),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD8, 0xD0, 0xBB)), BorderThickness = new Thickness(1)
        };
        b.Child = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = label, FontSize = 10, Foreground = Brushes.Gray },
                new TextBlock { Text = value ?? "-", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x24, 0x6A)) }
            }
        };
        _cardPanel.Children.Add(b);
    }

    private void BuildActions()
    {
        _actionsPanel.Children.Clear();
        string st = _card.Status;
        Button Btn(string text, string style, Action act, bool enabled)
        {
            // §B110: الحشوة من الثيم الموحد (12,5) بدل المضمّنة
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0), IsEnabled = enabled };
            b.Style = (Style)System.Windows.Application.Current.FindResource(style);
            b.Click += (_, _) => { act(); };
            return b;
        }

        if (st == DocStatuses.Draft)
            _actionsPanel.Children.Add(Btn("🔒 اعتماد الأمر (صرف المواد المساعدة)", "ErpApproveButton", () => Do(s => s.ApproveOrder(_orderId)), Can("production", "Approve")));
        if (st is DocStatuses.Approved or DocStatuses.Scheduled)
        {
            _actionsPanel.Children.Add(Btn("🏭 بدء الإنتاج", "ErpPrimaryButton", () => Do(s => s.StartOrder(_orderId)), Can("execution", "Create")));
            // التسجيل من أمر متأخر لا يحتاج خطوة بدء منفصلة؛ الخدمة تبدأه ذرياً عند الحفظ.
            _actionsPanel.Children.Add(Btn("📤 تسجيل فعلي اليوم (بدء وإقفال اليوم)", "ErpPrimaryButton", CloseDay, Can("execution", "Edit")));
            _actionsPanel.Children.Add(Btn("↩ إلغاء الاعتماد", "ErpDangerButton", () => Do(s => s.UnapproveOrder(_orderId)), Can("production", "Cancel")));
        }
        if (st == DocStatuses.InProgress)
        {
            _actionsPanel.Children.Add(Btn("⏸ إيقاف مؤقت", "ErpDangerButton", () => Do(s => s.StopOrder(_orderId, null)), Can("execution", "Edit")));
            _actionsPanel.Children.Add(Btn("📤 تسجيل فعلي اليوم (إقفال اليوم)", "ErpPrimaryButton", CloseDay, Can("execution", "Edit")));
        }
        if (st == DocStatuses.Stopped)
        {
            _actionsPanel.Children.Add(Btn("▶ استئناف الإنتاج", "ErpApproveButton", () => Do(s => s.ResumeOrder(_orderId)), Can("execution", "Edit")));
            _actionsPanel.Children.Add(Btn("📤 تسجيل فعلي اليوم (إقفال اليوم)", "ErpPrimaryButton", CloseDay, Can("execution", "Edit")));
        }
        if (st is DocStatuses.Draft or DocStatuses.Approved or DocStatuses.Scheduled)
            _actionsPanel.Children.Add(Btn("✖ إلغاء الأمر", "ErpDangerButton", CancelWithReason, Can("production", "Cancel")));
        if (st is DocStatuses.Completed or DocStatuses.InProgress)
            _actionsPanel.Children.Add(Btn("🔒 إغلاق الأمر", "ErpButton", () => CloseOrderWithReason(), Can("production", "Cancel")));

        RefreshActionStates();
    }

    // ═══ §v1.50.37 — أدوات المستند فقط (بدون CRUD مكرر) ═══
    private WrapPanel BuildDocTools()
    {
        Button BarBtn(string text, string style, RoutedEventHandler click, string tip = null)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 4), ToolTip = tip };
            try { b.Style = (Style)System.Windows.Application.Current.FindResource(style); } catch { }
            b.Click += click;
            return b;
        }
        var bar = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        bar.Children.Add(BarBtn("↩ تحديث المستند", "ErpButton", (_, _) => Refresh()));
        bar.Children.Add(BarBtn("🖨 طباعة الأمر", "ErpButton", (_, _) => Print()));
        bar.Children.Add(BarBtn("تقرير التنفيذ والإقفال", "ErpButton", (_, _) => PrintExecution()));
        bar.Children.Add(BarBtn("📄 PDF", "ErpButton", (_, _) => Pdf()));
        _editBtn = BarBtn("✏️ تعديل", "ErpButton", Edit_Click);
        _saveBtn = BarBtn("💾 حفظ", "ErpApproveButton", Save_Click);
        _deleteBtn = BarBtn("🗑 حذف المسودة", "ErpDangerButton", Delete_Click);
        _editBtn.Visibility = _saveBtn.Visibility = _deleteBtn.Visibility = Visibility.Collapsed;
        return bar;
    }

    private void RefreshActionStates()
    {
        if (_editBtn == null) return;
        bool locked = _card == null || _card.Status == DocStatuses.Closed || _card.Status == DocStatuses.Cancelled;
        _editBtn.IsEnabled = !locked && Can("production", "Edit") && !_editing;
        _saveBtn.IsEnabled = _editing && Can("production", "Edit");
        _deleteBtn.IsEnabled = IsDraft && Can("production", "Delete");
    }

    public bool IsEditing => _editing;
    public bool CanEdit => _card != null && _card.Status != DocStatuses.Closed && _card.Status != DocStatuses.Cancelled && !_editing;
    public bool CanDelete => IsDraft;
    public void InvokeEdit() => Edit_Click(null, null);
    public void InvokeSave() => Save_Click(null, null);
    public void InvokeDelete() => Delete_Click(null, null);
    public void InvokePrint() => Print();
    public void InvokeRefresh() => Refresh();

    public static void PromptSearch(Window owner, Action<int> navigator)
        => ShowOrderSearch(owner, navigator);

    public static void PromptAddFromPlan(Window owner, Action<TodayPendingGroupDto> selected)
    {
        var p = new OrderDocumentPanel(0) { _dialogOwner = owner };
        p.AddFromPlan_Click(selected);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_card == null || _card.Status == DocStatuses.Closed || _card.Status == DocStatuses.Cancelled)
        { AppContainer.Get<DialogService>().Error("أوامر مغلقة أو ملغاة لا تُعدَّل."); return; }
        _editing = true;
        _notesBox.IsReadOnly = false;
        RefreshActionStates();
        AppContainer.Get<DialogService>().Info("وضع التعديل مفعّل — عدّل «ملاحظات الأمر» ثم اضغط «💾 حفظ» من رأس الشاشة. كميات البنود ثابتة من الخطة المعتمدة وراجعها جهة التخطيط.");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_editing) { AppContainer.Get<DialogService>().Info("لا تعديل مفعّل — اضغط «✏️ تعديل» أولاً."); return; }
        try
        {
            using var scope = AppContainer.NewScope();
            var r = scope.ServiceProvider.GetRequiredService<IProductionOrderService>().UpdateOrderHeader(_orderId, notes: _notesBox.Text ?? "");
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            _editing = false;
            _notesBox.IsReadOnly = true;
            Refresh();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "OrderDoc.Save"); }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!IsDraft) { AppContainer.Get<DialogService>().Error("الحذف لأمر المسودة فقط — الأمر المعتمد يُلغى ولا يُحذف."); return; }
        if (!AppContainer.Get<DialogService>().Confirm("حذف أمر الإنتاج (المسودة) نهائياً؟")) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var r = scope.ServiceProvider.GetRequiredService<IProductionOrderService>().DeleteOrder(_orderId);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            Changed?.Invoke();
            ReturnToList?.Invoke();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "OrderDoc.Delete"); }
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        PromptSearch(_dialogOwner ?? Window.GetWindow(this), Navigator);
    }

    private static void ShowOrderSearch(Window owner, Action<int> navigator, string initialTerm = null)
    {
        var win = new DocSearchWindow("أوامر الإنتاج المحفوظة",
            new List<SearchFieldDef>
            {
                new() { Key = "term", LabelAr = "رقم الأمر أو العميل", DefaultValue = initialTerm }
            }, filters =>
            {
                using var scope = AppContainer.NewScope();
                var rows = scope.ServiceProvider.GetRequiredService<IProductionOrderService>()
                    .SearchOrders(filters.GetValueOrDefault("term") ?? "", 10000);
                return new SearchResult
                {
                    Columns = new List<string> { "رقم المستند", "التاريخ", "العميل", "الحالة" },
                    Rows = rows.Select(r => (r.OrderId, new object[] { r.DocumentNumber, r.ProductionDate, r.CustomerName, r.StatusAr })).ToList()
                };
            }) { Owner = owner };
        if (win.ShowDialog() == true && win.SelectedId is int id) navigator?.Invoke(id);
    }

    private void AddFromPlan_Click(Action<TodayPendingGroupDto> selectedCallback)
    {
        var win = new Window
        {
            Title = "اختيار خطة معتمدة لإنشاء أمر إنتاج",
            Width = 1060,
            Height = 560,
            MinWidth = 940,
            MinHeight = 500,
            ResizeMode = ResizeMode.CanResize,
            FlowDirection = FlowDirection.RightToLeft,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = _dialogOwner ?? Window.GetWindow(this)
        };
        var groups = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
            SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow,
            Height = 112, RowHeight = 28, ColumnHeaderHeight = 32, Margin = new Thickness(0, 6, 0, 4)
        };
        StyleShapeGrid(groups);
        groups.Columns.Add(new DataGridTextColumn { Header = "تاريخ التشغيل", Binding = new System.Windows.Data.Binding("ScheduledDate"), Width = 90 });
        groups.Columns.Add(new DataGridTextColumn { Header = "رقم الخطة", Binding = new System.Windows.Data.Binding("PlanNumber"), Width = 82 });
        groups.Columns.Add(new DataGridTextColumn { Header = "اسم الخطة", Binding = new System.Windows.Data.Binding("PlanTitle"), Width = new DataGridLength(1.0, DataGridLengthUnitType.Star) });
        groups.Columns.Add(new DataGridTextColumn { Header = "العملاء", Binding = new System.Windows.Data.Binding("Customers"), Width = new DataGridLength(1.35, DataGridLengthUnitType.Star) });
        groups.Columns.Add(new DataGridTextColumn { Header = "الوردية", Binding = new System.Windows.Data.Binding("ShiftName"), Width = 92 });
        groups.Columns.Add(new DataGridTextColumn { Header = "خط الإنتاج", Binding = new System.Windows.Data.Binding("LineName"), Width = 90 });
        groups.Columns.Add(new DataGridTextColumn { Header = "عدد العملاء", Binding = new System.Windows.Data.Binding("CustomerCount"), Width = 62 });
        groups.Columns.Add(new DataGridTextColumn { Header = "البنود", Binding = new System.Windows.Data.Binding("ItemsCount"), Width = 58 });
        groups.Columns.Add(new DataGridTextColumn { Header = "الكراتين", Binding = new System.Windows.Data.Binding("Cartons"), Width = 72 });
        groups.Columns.Add(new DataGridTextColumn { Header = "الوزن كجم", Binding = new System.Windows.Data.Binding("PlannedKg"), Width = 78 });
        groups.Columns.Add(new DataGridTextColumn { Header = "الحالة", Binding = new System.Windows.Data.Binding("AvailabilityStatus"), Width = 92 });

        var detailTitle = new TextBlock
        {
            Text = "اختر مجموعة من الأعلى لمراجعة جميع عملائها وبنودها قبل تحميلها إلى أمر الإنتاج.",
            FontWeight = FontWeights.Bold, FontSize = 12, Margin = new Thickness(0, 8, 0, 4),
            Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x24, 0x6A)), TextWrapping = TextWrapping.Wrap
        };
        var details = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
            SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow,
            Height = 190, RowHeight = 27, ColumnHeaderHeight = 32, Margin = new Thickness(0, 3, 0, 3)
        };
        StyleShapeGrid(details);
        details.Columns.Add(new DataGridTextColumn { Header = "العميل", Binding = new System.Windows.Data.Binding("CustomerName"), Width = new DataGridLength(1.15, DataGridLengthUnitType.Star) });
        details.Columns.Add(new DataGridTextColumn { Header = "الصنف الخام", Binding = new System.Windows.Data.Binding("RawName"), Width = new DataGridLength(1.05, DataGridLengthUnitType.Star) });
        details.Columns.Add(new DataGridTextColumn { Header = "الدفعة", Binding = new System.Windows.Data.Binding("LotCode"), Width = 72 });
        details.Columns.Add(new DataGridTextColumn { Header = "الشحنة", Binding = new System.Windows.Data.Binding("ShipmentNo"), Width = 78 });
        details.Columns.Add(new DataGridTextColumn { Header = "المخزن", Binding = new System.Windows.Data.Binding("WarehouseName"), Width = 82 });
        details.Columns.Add(new DataGridTextColumn { Header = "المنتج التام", Binding = new System.Windows.Data.Binding("ProductName"), Width = new DataGridLength(1.3, DataGridLengthUnitType.Star) });
        details.Columns.Add(new DataGridTextColumn { Header = "العبوة", Binding = new System.Windows.Data.Binding("PackName"), Width = 82 });
        details.Columns.Add(new DataGridTextColumn { Header = "الكراتين", Binding = new System.Windows.Data.Binding("PlannedCartons"), Width = 68 });
        details.Columns.Add(new DataGridTextColumn { Header = "الوزن كجم", Binding = new System.Windows.Data.Binding("PlannedKg"), Width = 75 });
        details.Columns.Add(new DataGridTextColumn { Header = "الوردية", Binding = new System.Windows.Data.Binding("ShiftName"), Width = 85 });
        details.Columns.Add(new DataGridTextColumn { Header = "الخط", Binding = new System.Windows.Data.Binding("LineName"), Width = 78 });

        var hint = new TextBlock
        {
            Text = "لم يُنشأ أمر بعد. حدّد الخطة ثم راجع البنود والعملاء، وبعدها اضغط تحميل للخطة.",
            Margin = new Thickness(0, 5, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(0x53, 0x69, 0x70)),
            TextWrapping = TextWrapping.Wrap
        };
        var loadBtn = new Button
        {
            Content = "⬇ تحميل المعاينة إلى شاشة الأمر",
            Style = (Style)System.Windows.Application.Current.FindResource("ErpPrimaryButton"),
            Margin = new Thickness(0, 5, 0, 0), Padding = new Thickness(14, 6, 14, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
            FontSize = 13, FontWeight = FontWeights.Bold, IsEnabled = false
        };
        var allGroups = new List<TodayPendingGroupDto>();
        TodayPendingGroupDto selected = null;

        groups.SelectionChanged += (_, _) =>
        {
            selected = groups.SelectedItem as TodayPendingGroupDto;
            details.ItemsSource = null;
            loadBtn.IsEnabled = false;
            if (selected == null) return;
            try
            {
                using var scope = AppContainer.NewScope();
                var sheet = scope.ServiceProvider.GetRequiredService<IProductionOrderService>().GetScheduledProduction();
                var items = sheet.Rows.Where(r => r.PlanId == selected.PlanId
                    && r.ScheduledDate == selected.ScheduledDate
                    && r.ShiftId == selected.ShiftId && r.LineId == selected.LineId).ToList();
                details.ItemsSource = items;
                detailTitle.Text = $"تفاصيل الخطة {selected.PlanNumber} — {selected.PlanTitle} | {selected.Customers} | {items.Count} بنداً | {selected.Cartons:N0} كرتون / {selected.PlannedKg:N1} كجم";
                hint.Text = items.Count == 0
                    ? "لم تعد لهذه المجموعة بنود قابلة للإصدار. حدّث القائمة واختر مجموعة أخرى."
                    : !selected.CanIssue
                        ? "الخطة ظاهرة للمراجعة لكنها غير جاهزة للإصدار: " + (selected.BlockReason ?? "صحح بيانات الخام في الخطة ثم أعد المحاولة.")
                        : "تحقق من أسماء العملاء والأصناف والدفعات والكميات والوردية والخط، ثم حمّل المجموعة. سيتم حفظ/إصدار الأمر من زر «حفظ» في الشاشة الرئيسية.";
                loadBtn.IsEnabled = items.Count > 0 && selected.CanIssue && PermissionGate.Can("production", "Create");
            }
            catch (Exception ex)
            {
                hint.Text = "تعذر تحميل تفاصيل المجموعة: " + ex.Message;
            }
        };

        loadBtn.Click += (_, _) =>
        {
            if (selected == null || details.Items.Count == 0)
            { AppContainer.Get<DialogService>().Info("اختر مجموعة وتأكد من ظهور تفاصيلها قبل التحميل."); return; }
            selectedCallback?.Invoke(selected);
            win.DialogResult = true;
            win.Close();
        };

        var root = new DockPanel { Margin = new Thickness(14) };
        var footer = new StackPanel();
        footer.Children.Add(hint);
        footer.Children.Add(loadBtn);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = "خطوات العمل: ١) اختر مجموعة خطة معتمدة غير صادرة. ٢) راجع جميع العملاء والتفاصيل. ٣) حمّلها ثم احفظ الأمر من الشاشة الرئيسية.",
            FontWeight = FontWeights.Bold, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x16, 0x28, 0x3C))
        });
        content.Children.Add(groups);
        content.Children.Add(detailTitle);
        content.Children.Add(details);
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        win.Content = root;

        win.Loaded += (_, _) =>
        {
            try
            {
                using var scope = AppContainer.NewScope();
                allGroups = scope.ServiceProvider.GetRequiredService<IProductionOrderService>().GetPendingPlanGroups();
                groups.ItemsSource = allGroups;
                if (allGroups.Count > 0) groups.SelectedIndex = 0;
                if (allGroups.Count == 0)
                    hint.Text = "لم تُعثر على خطة معتمدة مجدولة غير صادرة. تظهر الخطط السابقة أيضاً؛ تحقق من اعتماد الخطة وتاريخ بنودها، ومن عدم وجود أمر صادر لها.";
                else
                    hint.Text = $"توجد {allGroups.Count} مجموعة من الخطط السابقة والحالية والقادمة؛ اختر واحدة لعرض كل التفاصيل قبل التحميل.";
            }
            catch (Exception ex)
            {
                hint.Text = "تعذر تحميل الخطط: " + ex.Message;
                AppContainer.Get<DialogService>().HandleException(ex, "OrderDoc.AddLoad");
            }
        };
        win.ShowDialog();
    }

    private void Do(Func<IProductionOrderService, OpResult> op)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var res = op(scope.ServiceProvider.GetRequiredService<IProductionOrderService>());
            if (!res.Ok) AppContainer.Get<DialogService>().Error(res.Message);
            else AppContainer.Get<DialogService>().Info(res.Message);
            Refresh();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "OrderWindow.Action"); }
    }



    private void CancelWithReason()
    {
        if (!AppContainer.Get<DialogService>().Confirm("إلغاء أمر الإنتاج؟ إن كان معتمداً سيُعكس الصرف ويعود المتبقي للخطة.")) return;
        var dlg = new InputDialog("إلغاء أمر الإنتاج", "سبب الإلغاء (اختياري):");
        string reason = dlg.ShowDialog() == true ? dlg.Value : "";
        Do(s => s.CancelOrder(_orderId, reason));
    }

    /// <summary>§B95 — إغلاق الأمر: السبب فارغ عند الاكتمال وإجباري عند العجز (تسوية موثقة تُحفظ في الأمر).</summary>
    private void CloseOrderWithReason()
    {
        var dlg = new InputDialog("إغلاق أمر الإنتاج", "سبب الإغلاق (فارغ عند اكتمال الإنتاج — إجباري عند وجود عجز):");
        if (dlg.ShowDialog() != true) return;
        Do(s => s.CloseOrder(_orderId, dlg.Value));
    }

    private void CloseDay()
    {
        Services.ErrorLog.WriteInfo($"OrdersWindow.CloseDay CLICK OrderId={_orderId}");
        ProductionActualCloseView.OpenForOrder(_orderId, Window.GetWindow(this));
    }

    private ReportResult BuildReport()
    {
        using var scope = AppContainer.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var items = db.ProductionOrderItems.AsNoTracking().Where(i => i.OrderId == _orderId).ToList();
        var report = new ReportResult
        {
            TitleAr = $"أمر إنتاج رقم: {_card.OrderNumber}",
            Columns = new List<string> { "العميل", "الدفعة", "الصنف المستلم", "المنتج النهائي", "المخطط (كجم)", "الكراتين", "المنتَج (كجم)" }
        };
        foreach (var it in items)
            report.Rows.Add(new object[]
            {
                it.CustomerId != null ? db.Customers.AsNoTracking().Where(c => c.Id == it.CustomerId).Select(c => c.CustomerName).FirstOrDefault() : "-",
                db.Lots.AsNoTracking().Where(l => l.Id == it.LotId).Select(l => l.LotCode).FirstOrDefault() ?? "-",
                db.Lots.AsNoTracking().Where(l => l.Id == it.LotId).Join(db.Products, l => l.ProductId, p => p.Id, (l, p) => p.ProductNameAr).FirstOrDefault() ?? "-",
                db.Products.AsNoTracking().Where(p => p.Id == it.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-",
                it.PlannedQtyKg, it.PlannedCartons, it.ProducedQtyKg
            });
        report.Summary["الخطة"] = _card.PlanNumber;
        report.Summary["العميل"] = _card.CustomerName;
        report.Summary["التاريخ والوردية"] = $"{_card.ProductionDate} — {_card.ShiftName}";
        report.Summary["وقت البداية / النهاية المتوقع"] = $"{_card.StartTime} / {_card.ExpectedEndTime}";
        report.Summary["الحالة"] = _card.StatusAr;
        report.Summary["توقيع مدير الإنتاج"] = "____________________";
        report.Summary["توقيع مشرف الوردية"] = "____________________";
        report.Summary["توقيع مسؤول الجودة"] = "____________________";
        return report;
    }

    private void PrintExecution()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var m = Printing.StoredPrintModels.Execution(db, _orderId);
            new PrintPreviewWindow(PhasePrint.Build(m), $"{m.DocTitle} {m.DocNo}") { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Execution.Print"); }
    }

    private void Print()
    {
        try
        {
            using var scope=AppContainer.NewScope();
            var m=Printing.StoredPrintModels.Order(scope.ServiceProvider.GetRequiredService<DatesErpDbContext>(),_orderId);
            new PrintPreviewWindow(PhasePrint.Build(m),$"{m.DocTitle} {m.DocNo}") { Owner=Window.GetWindow(this) }.ShowDialog();
        }
        catch(Exception ex) { AppContainer.Get<DialogService>().HandleException(ex,"Order.Print"); }
    }
    private void Pdf() => Print();
}
