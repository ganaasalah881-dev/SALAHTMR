using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Views.Screens;

public class FrLineUi : INotifyPropertyChanged
{
    public int LineNo { get; set; }
    public int ItemId { get; set; }         // معرف بند السند بعد الحفظ
    public int? DeliveryItemId { get; set; }
    public int? OrderItemId { get; set; }
    public int? ByProductId { get; set; }
    public int? RawProductId { get; set; }
    public int? RawLotId { get; set; }
    public int? SourceOrderItemId { get; set; }
    public bool IsByProduct { get; set; }
    public string BlockReason { get; set; }

    public int? WarehouseId { get; set; }
    public string WarehouseName { get; set; }
    public string Unit { get; set; } = "كجم";
    public string OutputType => IsByProduct ? "مخرج ثانوي" : "منتج تام";
    public int ProductId { get; set; }
    public int? LotId { get; set; }
    public int? PackagingTypeId { get; set; }
    public double CartonWeightKg { get; set; }
    public string PackagingName { get; set; }
    public string ProductName { get; set; }
    public string LotCode { get; set; }
    public int? CustomerId { get; set; }
    public string CustomerName { get; set; }
    public double Remaining { get; set; }
    /// <summary>الكمية المتاحة للمنتج التام بالكرتون؛ المخرج الثانوي يعرض كميته بالكجم.</summary>
    public int Packages { get; set; }
    private int _receiptPackages;
    /// <summary>عدد كراتين هذا السند؛ الوزن الرقابي مشتق منه ولا يُدخله المستخدم.</summary>
    public int ReceiptPackages
    {
        get => _receiptPackages;
        set
        {
            _receiptPackages = Math.Max(0, value);
            OnChanged(nameof(ReceiptPackages));
            if (!IsByProduct) ReceiveNow = Math.Round(_receiptPackages * CartonWeightKg, 1);
        }
    }
    public int ReceivedPackages { get; set; }
    public string AvailableDisplay => IsByProduct ? $"{Remaining:N1} كجم" : $"{Packages:N0} كرتون";
    public string ReceivedDisplay => IsByProduct ? $"{Received:N1} كجم"
        : $"{(ReceivedPackages > 0 ? ReceivedPackages : CartonWeightKg > 0 ? (int)Math.Round(Received / CartonWeightKg, MidpointRounding.AwayFromZero) : 0):N0} كرتون";
    private bool _included = true;
    public bool Included { get => _included; set { _included = value; OnChanged(nameof(Included)); } }
    private double _qty;
    public double Qty { get => _qty; set { _qty = value; OnChanged(nameof(Qty)); } }
    private double _receiveNow;
    public double ReceiveNow { get => _receiveNow; set { _receiveNow = value; OnChanged(nameof(ReceiveNow)); } }
    private double _received;
    public double Received { get => _received; set { _received = value; OnChanged(nameof(Received)); } }

    public event PropertyChangedEventHandler PropertyChanged;
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>
/// استلام الإنتاج للمخازن: اختيار إقفال مباشر أو فحص معتمد؛ حفظ السند يرحّل للمخزون بلا مرحلة مسودة.
/// </summary>
public partial class FGReceiveView : UserControl
{
    public static int? PendingDeliveryId { get; set; }
    private readonly ObservableCollection<FrLineUi> _lines = new();
    private List<WarehouseOptionDto> _warehouses = new();
    private List<int> _recordIds = new();
    private Views.ErpToolbar _toolbar;
    private List<FinishedGoodsReceiptSourceDto> _directSources = new();
    private int _currentId, _currentOrderId, _currentExecutionId;
    private int? _currentQualityCheckId;
    private string _currentSourceType;
    private bool _initializing;
    private bool _loadingPicks;

    public FGReceiveView()
    {
        InitializeComponent();
        ItemsGrid.ItemsSource = _lines;
        Loaded += (_, _) => Load();
    }

    public void AttachChrome(Views.ErpChrome chrome)
    {
        chrome.SetModule("أوامر استلام الإنتاج");
        chrome.SetScreenCode("MRPINV1006");
        var tb = new Views.ErpToolbar()
            .ForModule("finishedgoods")
            .WithNew((_, _) => NewForm(), "سند استلام جديد (F2)")
            .WithSaveForCreate((_, _) => Save(), "حفظ وترحيل للمخزون (F10)")
            .WithApprove((_, _) => ApproveReceipt(), "اعتماد سند الاستلام")
            .WithCustom("🚛 تسليم العملاء", "ErpButton", (_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("delivery"), "فتح شاشة إذن خروج وتسليم بضاعة للعملاء (MRPINV1005)")
            .WithPrint((_, _) => Print(), "طباعة سند الاستلام المحفوظ (Ctrl+P)")
            .WithSearch((_, _) => OpenReceiptSearch(), "بحث وعرض سندات الاستلام (F9)")
            .WithUndo((_, _) => UndoSmart(), "تراجع: يعيد السند المفتوح أو يجهز سنداً جديداً")
            .WithNavigation((_, _) => Nav(0), (_, _) => Nav(-1), (_, _) => Nav(1), (_, _) => Nav(int.MaxValue))
            .WithExit((_, _) => (Window.GetWindow(this) as MainWindow)?.OpenPreviousScreen());
        _toolbar = tb;
        chrome.SetPermissionModule("finishedgoods");
        chrome.SetToolbar(tb);
        chrome.SetBody(this);
        chrome.CloseRequested += (_, _) => (Window.GetWindow(this) as MainWindow)?.OpenPreviousScreen();
    }

    private void Load()
    {
        try
        {
            _initializing = true;
            ModeBox.ItemsSource = new List<string> { "إقفال إنتاج فعلي", "محضر فحص معتمد" };
            ModeBox.SelectedIndex = 0;
            _initializing = false;
            DateBox.SelectedDate = DateTime.Now;
            using (var scope = AppContainer.NewScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                var allActive = db.Warehouses.AsNoTracking().Where(w => w.IsActive)
                    .OrderBy(w => w.WarehouseCode == "WFG" ? 0 : 1).ThenBy(w => w.WarehouseNameAr)
                    .ToList();
                var fgWhs = allActive
                    .Where(w => DatesErp.Application.Services.UnitsPolicy.IsFinishedWarehouseType(w.WarehouseType) || w.WarehouseCode == "WFG")
                    .ToList();
                _warehouses = (fgWhs.Count > 0 ? fgWhs : allActive)
                    .Select(w => new WarehouseOptionDto { Id = w.Id, Code = w.WarehouseCode, Name = w.WarehouseNameAr, Type = w.WarehouseType })
                    .ToList();
            }
            foreach (var column in ItemsGrid.Columns.OfType<DataGridComboBoxColumn>())
                column.ItemsSource = _warehouses;
            NewForm();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "FR.Load"); }
    }

    private string SelectedSourceType() => ModeBox.SelectedIndex == 1 ? DeliverySources.FromCheck : DeliverySources.FromActual;

    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        int selectedIndex = -1;
        try
        {
            _loadingPicks = true;
            _currentId = 0;
            _currentOrderId = 0;
            _currentExecutionId = 0;
            _currentQualityCheckId = null;
            _currentSourceType = SelectedSourceType();
            PickLabel.Text = ModeBox.SelectedIndex == 1 ? "محاضر الفحص المعتمدة:" : "إقفالات الإنتاج الفعلي:";
            _directSources.Clear();
            _lines.Clear();
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IFinishedGoodsService>();
            _directSources = svc.GetDirectReceiptSources(_currentSourceType);
            PickBox.ItemsSource = _directSources.Select(source => source.Label).ToList();
            selectedIndex = _directSources.FindIndex(s => s.CanReceive);
            if (selectedIndex < 0 && _directSources.Count > 0) selectedIndex = 0;
            PickBox.SelectedIndex = selectedIndex;
            if (_directSources.Count == 0)
                SourceHint.Text = ModeBox.SelectedIndex == 1
                    ? "لا توجد محاضر فحص معتمدة لإقفال إنتاج حتى الآن. لا يُرحّل أي مخزون قبل اعتماد الجودة."
                    : "لا توجد إقفالات اختير لها المسار المباشر؛ الإقفالات الموجهة للجودة تظهر فقط بعد اعتماد محضرها.";
            else
                SourceHint.Text = ModeBox.SelectedIndex == 1
                    ? "تظهر هنا إقفالات أُرسلت للجودة بعد اكتمال الفحص واعتماده. المتاح هو المقبول فقط بعد خصم الاستلامات السابقة؛ الحفظ يرحّل مباشرة."
                    : "تظهر هنا الإقفالات التي اختير لها المسار المباشر عند الحفظ؛ لا يشترط فحص الجودة. السقف هو الفعلي المسجل للجلسة بعد خصم الاستلامات السابقة.";
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "FR.Mode"); }
        finally { _loadingPicks = false; }
        if (selectedIndex >= 0) Download();
    }

    private void Pick_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingPicks) Download();
    }

    private void Download_Click(object sender, RoutedEventArgs e) => Download();

    private void Download()
    {
        try
        {
            if (_currentId > 0) return;
            if (PickBox.SelectedIndex < 0 || PickBox.SelectedIndex >= _directSources.Count) return;
            var source = _directSources[PickBox.SelectedIndex];
            _currentOrderId = source.OrderId;
            _currentExecutionId = source.ExecutionId;
            _currentQualityCheckId = source.QualityCheckId;
            _currentSourceType = source.SourceType;
            _lines.Clear();
            foreach (var line in source.Items)
            {
                int? warehouseId = DefaultWarehouseId();
                _lines.Add(new FrLineUi
                {
                    LineNo = _lines.Count + 1,
                    OrderItemId = line.OrderItemId,
                    CartonWeightKg = line.CartonWeightKg,
                    ByProductId = line.ByProductId,
                    RawProductId = line.RawProductId,
                    RawLotId = line.RawLotId,
                    SourceOrderItemId = line.SourceOrderItemId,
                    IsByProduct = line.IsByProduct,
                    BlockReason = line.BlockReason,
                    ProductId = line.ProductId,
                    LotId = line.LotId,
                    PackagingTypeId = line.PackagingTypeId,
                    PackagingName = line.PackagingName ?? "—",
                    ProductName = line.ProductName,
                    Unit = line.Unit,
                    LotCode = line.LotCode ?? "—",
                    CustomerId = line.CustomerId,
                    CustomerName = line.CustomerName ?? "—",
                    WarehouseId = warehouseId,
                    WarehouseName = WarehouseName(warehouseId),
                    Remaining = Math.Round(line.AvailableQtyKg, 3),
                    Packages = line.AvailableCartons,
                    ReceiptPackages = line.IsByProduct || line.BlockReason != null ? 0 : line.AvailableCartons,
                    Qty = Math.Round(line.AvailableQtyKg, 3),
                    ReceiveNow = line.BlockReason != null ? 0 : line.IsByProduct
                        ? Math.Round(line.AvailableQtyKg, 3)
                        : Math.Round(line.AvailableCartons * line.CartonWeightKg, 1),
                    Included = line.BlockReason == null && (line.IsByProduct ? line.AvailableQtyKg > 0.001 : line.AvailableCartons > 0)
                });
            }
            DocNoBox.Text = "(تلقائي عند الترحيل)";
            SourceHint.Text = source.CanReceive
                ? $"{source.Label} — {source.Items.Count(i => i.IsByProduct)} مخرجاً ثانوياً (إن وُجد)؛ تأكد من المخزن والكمية ثم احفظ لترحيلها."
                : source.BlockReason ?? "المصدر غير متاح للاستلام.";
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "FR.Download"); }
    }

    private void Save()
    {
        try
        {
            if (_currentId > 0) { AppContainer.Get<DialogService>().Error("هذا السند محفوظ بالفعل؛ كل حفظ مباشر جديد ينشئ استلاماً ويرحّله للمخزون في معاملة واحدة."); return; }
            if (_currentOrderId == 0 || _currentExecutionId == 0
                || (_currentSourceType == DeliverySources.FromCheck && _currentQualityCheckId == null))
            { AppContainer.Get<DialogService>().Error("اختر مصدراً جاهزاً وفق المسار المختار عند إقفال الإنتاج."); return; }
            var invalid = _lines.Where(l => l.Included && (l.WarehouseId == null || l.ReceiveNow <= 0
                || (!l.IsByProduct && (l.ReceiptPackages <= 0 || l.ReceiptPackages > l.Packages)))).ToList();
            if (invalid.Count > 0)
            { AppContainer.Get<DialogService>().Error("تحقق من عدد الكراتين ضمن المتاح، أو كمية المخرج الثانوي بالكيلوجرام، والمخزن لكل بند محدد."); return; }
            var blocked = _lines.Where(l => l.Included && !string.IsNullOrWhiteSpace(l.BlockReason)).ToList();
            if (blocked.Count > 0)
            { AppContainer.Get<DialogService>().Error(blocked[0].BlockReason); return; }
            var selected = _lines.Where(l => l.Included && l.ReceiveNow > 0.001).ToList();
            if (selected.Count == 0) { AppContainer.Get<DialogService>().Error("ضمّن بنداً واحداً على الأقل بكمية أكبر من صفر."); return; }
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IFinishedGoodsService>();
            var r = svc.SaveAndReceiveDirect(_currentOrderId, _currentExecutionId, _currentQualityCheckId, _currentSourceType,
                (DateBox.SelectedDate ?? DateTime.Now).ToString("dd/MM/yyyy"),
                selected.Select(l => new FinishedGoodsItemDto
                {
                    ProductId = l.ProductId, LotId = l.LotId, PackagingTypeId = l.PackagingTypeId,
                    PackageCount = l.IsByProduct ? 0 : l.ReceiptPackages, NetWeightKg = l.ReceiveNow,
                    CustomerId = l.CustomerId, WarehouseId = l.WarehouseId,
                    OrderItemId = l.OrderItemId, ByProductId = l.ByProductId,
                    RawProductId = l.RawProductId, RawLotId = l.RawLotId, SourceOrderItemId = l.SourceOrderItemId
                }).ToList());
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            RefreshList();
            OpenReceipt(r.Id);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "FR.Save"); }
    }

    private void ApproveReceipt()
    {
        try
        {
            if (_currentId <= 0)
            {
                AppContainer.Get<DialogService>().Error("احفظ سند الاستلام أولاً؛ الاعتماد متاح بعد الترحيل المباشر للمخزون.");
                return;
            }
            if (!PermissionGate.Can("finishedgoods", "Approve"))
            {
                AppContainer.Get<DialogService>().Error("لا توجد صلاحية اعتماد سندات استلام الإنتاج.");
                return;
            }
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IFinishedGoodsService>();
            var r = svc.ApproveReceipt(_currentId);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            RefreshList();
            OpenReceipt(_currentId);
            AppContainer.Get<DialogService>().Info(r.Message);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "FR.Approve"); }
    }

    private void Issue()
    {
        try
        {
            if (_currentId == 0) { AppContainer.Get<DialogService>().Error("لا يلزم إصدار منفصل: حفظ الاستلام المباشر يرحّل للمخزون فوراً."); return; }
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IFinishedGoodsService>();
            var r = svc.Issue(_currentId);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message + "\n(الإصدار لم يؤثر على الأرصدة — نفّذ الاستلام)");
            RefreshList();
            OpenReceipt(_currentId);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "FR.Issue"); }
    }

    private void LegacyIssue_Click(object sender, RoutedEventArgs e) => Issue();

    private void Receive_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_currentId == 0) { AppContainer.Get<DialogService>().Error("افتح سند استلام مُصدراً."); return; }
            // الخريطة الجزئية صريحة: السطر الغائب = صفر، وليس «استلم المتبقي كله».
            // الاستلام الكامل عبر API القديم (null) لا يُنشأ من هذه الشاشة ولا يُستخدم
            // إلا بأمر صريح من المستدعي المتوافق مع المسار القديم.
            if (_lines.Any(l => l.ItemId > 0 && (l.ReceiveNow < 0 || l.ReceiptPackages < 0)))
            {
                AppContainer.Get<DialogService>().Error("لا يمكن تنفيذ الاستلام — الكمية أو عدد الكراتين لا يمكن أن يكون سالباً.");
                return;
            }
            var entered = _lines.Where(l => l.ItemId > 0 && l.ReceiveNow > 0.001).ToList();
            if (entered.Count == 0)
            {
                AppContainer.Get<DialogService>().Error("لم تُدخل أي كمية مستلمة.");
                return;
            }
            var map = entered.ToDictionary(l => l.ItemId, l => l.ReceiveNow);

            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IFinishedGoodsService>();
            var r = svc.Receive(_currentId, map);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            foreach (var l in _lines) l.ReceiveNow = 0;
            RefreshList();
            OpenReceipt(_currentId);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "FR.Receive"); }
    }

    private void Unapprove_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_currentId == 0) { AppContainer.Get<DialogService>().Error("افتح سنداً أولاً."); return; }
            if (!AppContainer.Get<DialogService>().Confirm("سيُلغى سند الاستلام ويُعكس أثره المخزني إن وُجد. لا يمكن التراجع عن هذا الإجراء من نفس الشاشة. متابعة؟")) return;
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IFinishedGoodsService>();
            var r = svc.Unapprove(_currentId);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            RefreshList();
            OpenReceipt(_currentId);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "FR.Unapprove"); }
    }

    private void RefreshList()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            _recordIds = db.FinishedGoodsReceipts.AsNoTracking()
                .OrderByDescending(r => r.Id).Select(r => r.Id).Take(1000).ToList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "FR.List"); }
    }

    private void OpenReceiptSearch()
    {
        try
        {
            var win = new DocSearchWindow("سندات استلام الإنتاج",
                new List<SearchFieldDef>
                {
                    new() { Key = "number", LabelAr = "رقم السند / رقم الترحيل" },
                    new() { Key = "order", LabelAr = "رقم أمر الإنتاج" },
                    new() { Key = "source", LabelAr = "المصدر", Kind = "combo", Options = new[] { "إقفال إنتاج فعلي", "محضر فحص معتمد", "تاريخي" } },
                    new() { Key = "status", LabelAr = "الحالة", Kind = "combo", Options = new[] { "بانتظار الاعتماد", "معتمد", "ملغى" } }
                }, conditions =>
                {
                    using var scope = AppContainer.NewScope();
                    var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                    var query = db.FinishedGoodsReceipts.AsNoTracking().AsQueryable();
                    string number = conditions.GetValueOrDefault("number")?.Trim();
                    if (!string.IsNullOrWhiteSpace(number))
                        query = query.Where(r => r.DocumentNumber.Contains(number)
                            || (r.ReceiptNumber != null && r.ReceiptNumber.Contains(number)));
                    string order = conditions.GetValueOrDefault("order")?.Trim();
                    if (!string.IsNullOrWhiteSpace(order))
                    {
                        var orderIds = db.ProductionOrders.AsNoTracking()
                            .Where(o => o.DocumentNumber.Contains(order)).Select(o => o.Id).ToList();
                        query = query.Where(r => orderIds.Contains(r.OrderId));
                    }
                    switch (conditions.GetValueOrDefault("source"))
                    {
                        case "إقفال إنتاج فعلي": query = query.Where(r => r.SourceType == DeliverySources.FromActual); break;
                        case "محضر فحص معتمد": query = query.Where(r => r.SourceType == DeliverySources.FromCheck); break;
                        case "تاريخي": query = query.Where(r => r.SourceType == null); break;
                    }
                    switch (conditions.GetValueOrDefault("status"))
                    {
                        case "بانتظار الاعتماد": query = query.Where(r => !r.IsApproved && r.Status != DocStatuses.Cancelled); break;
                        case "معتمد": query = query.Where(r => r.IsApproved); break;
                        case "ملغى": query = query.Where(r => r.Status == DocStatuses.Cancelled); break;
                    }

                    var receipts = query.OrderByDescending(r => r.Id).Take(1000).ToList();
                    var ids = receipts.Select(r => r.Id).ToList();
                    var orderIdsForNames = receipts.Select(r => r.OrderId).Distinct().ToList();
                    var orderNos = db.ProductionOrders.AsNoTracking().Where(o => orderIdsForNames.Contains(o.Id))
                        .ToDictionary(o => o.Id, o => o.DocumentNumber);
                    var receiptItems = db.FinishedGoodsReceiptItems.AsNoTracking().Where(i => ids.Contains(i.ReceiptId)).ToList();
                    var productIds = receiptItems.Select(i => i.ProductId).Distinct().ToList();
                    var productNames = db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id))
                        .ToDictionary(p => p.Id, p => p.ProductNameAr);
                    var byReceipt = receiptItems.GroupBy(i => i.ReceiptId).ToDictionary(g => g.Key, g => g.ToList());
                    var result = new SearchResult
                    {
                        Columns = new List<string> { "رقم السند", "رقم الترحيل", "أمر الإنتاج", "المصدر", "الأصناف والكميات", "الإجمالي (كرتون)", "التاريخ", "الحالة" }
                    };
                    foreach (var receipt in receipts)
                    {
                        var lines = byReceipt.GetValueOrDefault(receipt.Id) ?? new List<FinishedGoodsReceiptItem>();
                        string summary = string.Join("، ", lines.GroupBy(i => new { i.ProductId, i.ByProductId })
                            .Select(g =>
                            {
                                string product = productNames.GetValueOrDefault(g.Key.ProductId) ?? $"#{g.Key.ProductId}";
                                return g.Key.ByProductId != null
                                    ? $"{product} ({g.Sum(i => i.NetWeightKg):N1} كجم)"
                                    : $"{product} ({g.Sum(i => i.PackageCount):N0} كرتون)";
                            }));
                        string source = receipt.SourceType == DeliverySources.FromCheck ? "محضر فحص معتمد"
                            : receipt.SourceType == DeliverySources.FromActual ? "إقفال إنتاج فعلي" : "تاريخي";
                        string state = receipt.Status == DocStatuses.Cancelled ? "ملغى"
                            : receipt.IsApproved ? "معتمد ومقفل" : "بانتظار الاعتماد";
                        result.Rows.Add((receipt.Id, new object[]
                        {
                            receipt.DocumentNumber ?? "—", receipt.ReceiptNumber ?? "—",
                            orderNos.GetValueOrDefault(receipt.OrderId) ?? $"#{receipt.OrderId}", source,
                            summary, lines.Where(i => i.ByProductId == null).Sum(i => i.PackageCount).ToString("N0"),
                            receipt.DeliveryDate?.ToString("dd/MM/yyyy") ?? "—", state
                        }));
                    }
                    return result;
                });
            win.Owner = Window.GetWindow(this);
            if (win.ShowDialog() == true && win.SelectedId is int id)
                OpenReceipt(id);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "FR.Search"); }
    }

    private void SetApproveEnabled(bool enabled)
    {
        if (_toolbar?.ApproveBtn != null)
            _toolbar.ApproveBtn.IsEnabled = enabled && !_toolbar.IsDenied(_toolbar.ApproveBtn);
    }

    private void Print()
    {
        if (_currentId <= 0)
        {
            AppContainer.Get<DialogService>().Error("احفظ سند الاستلام أولاً؛ الطباعة تستخدم النسخة المحفوظة فقط.");
            return;
        }
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var model = Printing.StoredPrintModels.FinishedReceipt(db, _currentId);
            new PrintPreviewWindow(PhasePrint.Build(model), $"{model.DocTitle} {model.DocNo}")
            { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "FGReceive.Print"); }
    }

    private void OpenReceipt(int id)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var r = db.FinishedGoodsReceipts.Include(x => x.Items).FirstOrDefault(x => x.Id == id);
            if (r == null) return;
            _currentId = r.Id;
            _currentOrderId = r.OrderId;
            _currentExecutionId = r.ProductionExecutionId ?? 0;
            _currentQualityCheckId = r.QualityCheckId;
            _currentSourceType = r.SourceType;
            _initializing = true;
            _loadingPicks = true;
            ModeBox.SelectedIndex = r.SourceType == DeliverySources.FromCheck ? 1 : 0;
            PickLabel.Text = r.SourceType == DeliverySources.FromCheck ? "محضر الفحص المعتمد:" : "إقفال الإنتاج الفعلي:";
            PickBox.ItemsSource = new List<string> { r.SourceType == DeliverySources.FromCheck
                ? $"محضر فحص #{r.QualityCheckId} — أمر #{r.OrderId}"
                : $"إقفال إنتاج #{r.ProductionExecutionId} — أمر #{r.OrderId}" };
            PickBox.SelectedIndex = 0;
            _loadingPicks = false;
            _initializing = false;
            LegacyIssueButton.Visibility = r.DeliveryId != null && r.Status == DocStatuses.Draft ? Visibility.Visible : Visibility.Collapsed;
            ReceiveButton.Visibility = r.DeliveryId != null && r.Status == DocStatuses.Issued ? Visibility.Visible : Visibility.Collapsed;
            UnapproveButton.Visibility = r.Status != DocStatuses.Cancelled
                && DatesErp.Desktop.Views.PermissionGate.Can("finishedgoods", "Cancel") ? Visibility.Visible : Visibility.Collapsed;
            SetApproveEnabled(!r.IsApproved && r.Status == DocStatuses.Submitted);
            ItemsGrid.IsReadOnly = true;
            ModeBox.IsEnabled = PickBox.IsEnabled = DateBox.IsEnabled = false;
            DocNoBox.Text = $"{r.DocumentNumber ?? "—"} / {r.ReceiptNumber ?? "بانتظار رقم الترحيل"}";
            DateBox.SelectedDate = r.DeliveryDate;
            SourceHint.Text = r.IsApproved
                ? "✅ السند معتمد ومقفل؛ التعديل غير متاح. للتصحيح استخدم الإلغاء والعكس بصلاحية الإلغاء."
                : r.Status == DocStatuses.Submitted
                    ? "🟠 تم ترحيل الكمية مباشرة للمخزون؛ السند بانتظار الاعتماد النهائي. الاعتماد يقفل السند ويطلقه لمسار تسليم العميل."
                    : $"حالة السند: {Core.Common.DocStatuses.ToArabic(r.Status)} — راجع بنود الاستلام أدناه.";
            _lines.Clear();
            foreach (var i in r.Items)
                _lines.Add(new FrLineUi
                {
                    LineNo = _lines.Count + 1,
                    ItemId = i.Id,
                    DeliveryItemId = i.DeliveryItemId,
                    OrderItemId = i.OrderItemId,
                    ByProductId = i.ByProductId,
                    RawProductId = i.RawProductId,
                    RawLotId = i.RawLotId,
                    SourceOrderItemId = i.SourceOrderItemId,
                    IsByProduct = i.ByProductId != null,
                    WarehouseId = i.WarehouseId ?? r.WarehouseId,
                    WarehouseName = WarehouseName(i.WarehouseId ?? r.WarehouseId),
                    Unit = i.ByProductId != null ? "كجم" : "كرتون",
                    CartonWeightKg = i.CartonWeightKg,
                    ProductId = i.ProductId,
                    LotId = i.LotId,
                    PackagingTypeId = i.PackagingTypeId,
                    PackagingName = i.PackagingTypeId != null
                        ? db.PackagingTypes.Where(p => p.Id == i.PackagingTypeId.Value).Select(p => p.PackageNameAr).FirstOrDefault() ?? "غير محددة"
                        : "غير محددة",
                    ProductName = db.Products.Where(p => p.Id == i.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-",
                    LotCode = i.ByProductId != null
                        ? $"خام: {db.Products.Where(p => p.Id == i.RawProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "—"} — دفعة {db.Lots.Where(l => l.Id == i.RawLotId).Select(l => l.LotCode).FirstOrDefault() ?? "—"}"
                        : db.Lots.Where(l => l.Id == i.LotId).Select(l => l.LotCode).FirstOrDefault() ?? "—",
                    CustomerId = i.CustomerId,
                    CustomerName = db.Customers.Where(c => c.Id == i.CustomerId).Select(c => c.CustomerName).FirstOrDefault() ?? "—",
                    Remaining = Math.Max(0, Math.Round(i.NetWeightKg - i.ReceivedQtyKg, 1)),
                    Packages = i.PackageCount,
                    ReceiptPackages = i.PackageCount,
                    ReceivedPackages = i.ReceivedPackageCount,
                    Included = false,
                    Qty = i.NetWeightKg,
                    Received = i.ReceivedQtyKg
                });
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "FR.Open"); }
    }

    private int? DefaultWarehouseId()
    {
        var finished = _warehouses.FirstOrDefault(w => string.Equals(w.Code, "WFG", StringComparison.OrdinalIgnoreCase))
            ?? _warehouses.FirstOrDefault(w => (w.Type ?? "").Contains("Finished", StringComparison.OrdinalIgnoreCase))
            ?? _warehouses.FirstOrDefault();
        return finished?.Id;
    }

    private string WarehouseName(int? id)
        => id is int value ? _warehouses.FirstOrDefault(w => w.Id == value)?.DisplayName ?? $"مخزن #{value}" : "غير محدد";

    private void NewForm()
    {
        _currentId = 0;
        _currentOrderId = 0;
        _currentExecutionId = 0;
        _currentQualityCheckId = null;
        LegacyIssueButton.Visibility = Visibility.Collapsed;
        ReceiveButton.Visibility = Visibility.Collapsed;
        UnapproveButton.Visibility = Visibility.Collapsed;
        SetApproveEnabled(false);
        ItemsGrid.IsReadOnly = false;
        ModeBox.IsEnabled = true;
        PickBox.IsEnabled = true;
        DateBox.IsEnabled = true;
        try
        {
            using var _numScope = AppContainer.NewScope();
            var _num = _numScope.ServiceProvider.GetRequiredService<INumberingService>().Peek("FGR");
            DocNoBox.Text = $"{_num} / بعد الترحيل";
        }
        catch { DocNoBox.Text = "يُنشأ تلقائياً عند الحفظ والترحيل"; }
        DateBox.SelectedDate = DateTime.Now;
        _lines.Clear();
        if (ModeBox.SelectedIndex < 0) ModeBox.SelectedIndex = 0;
        else Mode_Changed(null, null);
    }

    private void UndoSmart()
    {
        if (_currentId > 0) OpenReceipt(_currentId);
        else NewForm();
    }

    private void Nav(int move)
    {
        if (_recordIds.Count == 0) RefreshList();
        if (_recordIds.Count == 0) return;
        int i = _recordIds.IndexOf(_currentId);
        int n = move == 0 ? 0 : move == int.MaxValue ? _recordIds.Count - 1 : Math.Min(_recordIds.Count - 1, Math.Max(0, i + move));
        OpenReceipt(_recordIds[n]);
    }
}
