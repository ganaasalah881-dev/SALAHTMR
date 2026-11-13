using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// §التصميم المعتمد (MRPMPS1021 — production_screens_shape-2):
/// تسليم الإنتاج — تسجيل الفعلي وتوجيه مخازن المنتج التام وإرساله للفحص والاستلام المخزني.
/// </summary>
public partial class ProductionDeliveryView : UserControl
{
    public static int? PendingOrderId { get; set; }

    public ObservableCollection<WarehouseOptionDto> FinishedWarehouses { get; } = new();
    public ObservableCollection<ActualByProductDefinitionDto> ByProductDefinitions { get; } = new();

    private readonly ObservableCollection<ActualProductionRow> _items = new();
    private readonly ObservableCollection<ActualSecondaryRow> _secondary = new();
    private readonly Func<IProductionDeliveryService> _serviceResolver;
    private readonly Action<string> _notifyInfo;

    private List<ActualDeliveryOrderDto> _orders = new();
    private List<ProductionDeliveryCard> _deliveries = new();
    private List<ActualByProductDefinitionDto> _activeDefinitions = new();
    private ActualDeliveryOrderDto _currentOrder;
    private ProductionDeliveryCard _currentDelivery;
    private bool _loading;
    private bool _saving;
    private bool _applyingDefaultWarehouse;

    private static bool CanProduction(string action)
        => DatesErp.Desktop.Views.PermissionGate.Can("production", action);
    private static bool CanExecution(string action)
        => DatesErp.Desktop.Views.PermissionGate.Can("execution", action);
    private static bool CanFinishedGoods(string action)
        => DatesErp.Desktop.Views.PermissionGate.Can("finishedgoods", action);

    public ProductionDeliveryView() : this(null, null) { }

    public ProductionDeliveryView(Func<IProductionDeliveryService> serviceResolver, Action<string> notifyInfo = null)
    {
        _serviceResolver = serviceResolver;
        _notifyInfo = notifyInfo ?? (msg => AppContainer.Get<DialogService>().Info(msg));
        InitializeComponent();
        ItemsGrid.ItemsSource = _items;
        SecondaryGrid.ItemsSource = _secondary;
        _secondary.CollectionChanged += Secondary_CollectionChanged;
        DeliveryDateBox.SelectedDate = DateTime.Today;
        Loaded += (_, _) => LoadData(PendingOrderId, null);
    }

    public void AttachChrome(Views.ErpChrome chrome)
    {
        chrome.SetModule("تسليم الإنتاج — تسجيل الفعلي");
        chrome.SetPermissionModule("production");
        chrome.SetScreenCode("MRPMPS1021");
        var toolbar = new Views.ErpToolbar()
            .WithNew((_, _) => CreateDraftOnly(), "إنشاء مسودة أمر تسليم من فعلي مسجل")
            .WithEdit((_, _) => EnableDraftEdit())
            .WithSave((_, _) => Save_Click(null, null), "حفظ الفعلي أو تحديث مسودة التسليم")
            .WithCustom("📤 تحرير للمخزن", "ErpApproveButton", (_, _) => IssueDelivery_Click(null, null),
                "اعتماد وتحرير أمر التسليم للمخزن دون ترحيل مباشر للأرصدة", "Approve")
            .WithCustom("✖ إلغاء التسليم", "ErpDangerButton", (_, _) => CancelDelivery(),
                "إلغاء أمر التسليم غير المستلم", "Cancel")
            .WithPrint((_, _) => Print(), "طباعة مستند تسليم الإنتاج أو التنفيذ المحفوظ")
            .WithSearch((_, _) => OpenSearchWindow(SearchBox.Text), "البحث برقم الأمر أو العميل أو مستند التسليم")
            .WithUndo((_, _) => Undo(), "إعادة تحميل آخر حالة محفوظة")
            .WithList((_, _) => LoadData(_currentOrder?.OrderId, _currentDelivery?.Id), "تحديث أوامر الإنتاج والتسليم")
            .WithExit((_, _) => (Window.GetWindow(this) as MainWindow)?.OpenPreviousScreen());
        chrome.SetToolbar(toolbar);
        chrome.SetBody(this);
        chrome.CloseRequested += (_, _) => (Window.GetWindow(this) as MainWindow)?.OpenPreviousScreen();
    }

    /// <summary>Compatibility redirect: the retired delivery editor now opens the sole actual-entry screen.</summary>
    public static void OpenForOrder(int orderId, Window source)
        => ProductionActualCloseView.OpenForOrder(orderId, source);

    private T WithService<T>(Func<IProductionDeliveryService, T> action)
    {
        if (_serviceResolver != null) return action(_serviceResolver());
        using var scope = AppContainer.NewScope();
        return action(scope.ServiceProvider.GetRequiredService<IProductionDeliveryService>());
    }

    private int? DefaultWarehouseId => (DefaultWarehouseBox?.SelectedItem as WarehouseOptionDto)?.Id
        ?? FinishedWarehouses.FirstOrDefault()?.Id;

    private void LoadData(int? selectedOrderId, int? selectedDeliveryId)
    {
        try
        {
            _loading = true;
            if (!selectedOrderId.HasValue && !selectedDeliveryId.HasValue) SearchBox.Text = "";
            var warehouses = WithService(s => s.GetProductionDeliveryWarehouses()) ?? new List<WarehouseOptionDto>();
            int? prevDefaultWh = DefaultWarehouseId;
            FinishedWarehouses.Clear();
            foreach (var wh in warehouses) FinishedWarehouses.Add(wh);
            _applyingDefaultWarehouse = true;
            DefaultWarehouseBox.ItemsSource = FinishedWarehouses;
            DefaultWarehouseBox.SelectedItem = FinishedWarehouses.FirstOrDefault(w => w.Id == prevDefaultWh)
                ?? FinishedWarehouses.FirstOrDefault();
            _applyingDefaultWarehouse = false;

            _activeDefinitions = WithService(s => s.GetActualByProducts()) ?? new List<ActualByProductDefinitionDto>();
            ByProductDefinitions.Clear();
            foreach (var def in _activeDefinitions) ByProductDefinitions.Add(def);
            RefreshSecondarySummary();

            _orders = WithService(s => s.GetActualDeliveryOrders())?.ToList() ?? new List<ActualDeliveryOrderDto>();
            if (selectedOrderId is int wantedId && _orders.All(o => o.OrderId != wantedId))
            {
                var targeted = WithService(s => s.GetActualDeliveryOrders((int?)wantedId, true));
                foreach (var extra in targeted.Where(x => _orders.All(o => o.OrderId != x.OrderId)))
                    _orders.Insert(0, extra);
            }
            _deliveries = WithService(s => s.GetDeliveries(null)) ?? new List<ProductionDeliveryCard>();
            PendingOrderId = null;

            bool hasAnyDocuments = _orders.Count > 0 || _deliveries.Count > 0;
            EmptyGuide.Visibility = !hasAnyDocuments ? Visibility.Visible : Visibility.Collapsed;

            // لا نعرض ولا نحمّل مستنداً قديماً تلقائياً عند فتح الشاشة؛ يلزم بحث صريح،
            // أو معرّف صريح مرّرته شاشة سابقة (مثل زر «تسجيل فعلي اليوم»).
            OrderBox.ItemsSource = Array.Empty<ActualDeliveryOrderDto>();
            DeliveryBox.ItemsSource = Array.Empty<ProductionDeliveryCard>();
            OrderBox.SelectedIndex = -1;
            DeliveryBox.SelectedIndex = -1;

            if (selectedDeliveryId is > 0)
            {
                var deliv = _deliveries.FirstOrDefault(d => d.Id == selectedDeliveryId.Value);
                if (deliv == null)
                {
                    // قد يكون أمر التسليم خارج القائمة المبسّطة (مكتمل أو قديم) — اجلبه مباشرة
                    var directCard = WithService(s => s.GetDelivery(selectedDeliveryId.Value));
                    if (directCard != null) _deliveries.Insert(0, new ProductionDeliveryCard { Id = directCard.Id, OrderId = directCard.OrderId, DocumentNumber = directCard.DocumentNumber, Status = directCard.Status, StatusAr = directCard.StatusAr });
                    deliv = _deliveries.FirstOrDefault(d => d.Id == selectedDeliveryId.Value);
                }
                if (deliv != null)
                {
                    OrderBox.ItemsSource = _orders;
                    DeliveryBox.ItemsSource = _deliveries;
                    DeliveryBox.SelectedItem = deliv;
                    _loading = false;
                    BindDelivery(deliv.Id);
                    return;
                }
            }

            if (selectedOrderId is int explicitOrderId)
            {
                var targetOrder = _orders.FirstOrDefault(o => o.OrderId == explicitOrderId);
                if (targetOrder != null)
                {
                    OrderBox.ItemsSource = _orders;
                    DeliveryBox.ItemsSource = _deliveries;
                    OrderBox.SelectedItem = targetOrder;
                    _loading = false;
                    BindOrder(targetOrder);
                    return;
                }
            }

            ClearSelectionForSearch();
            StatusLabel.Text = hasAnyDocuments
                ? "اكتب رقم أمر الإنتاج أو العميل أو مستند التسليم ثم اضغط 🔍 لعرض السجل المطلوب."
                : "لا توجد أوامر إنتاج أو مستندات تسليم للعرض حالياً.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "تعذر تحميل بيانات تسليم الإنتاج: " + ex.Message;
            ErrorLog.Write(ex, "ProductionDelivery.LoadData");
        }
        finally
        {
            _loading = false;
        }
    }

    private void ClearSelectionForSearch()
    {
        _loading = true;
        try
        {
            OrderBox.SelectedIndex = -1;
            DeliveryBox.SelectedIndex = -1;
            _currentOrder = null;
            _currentDelivery = null;
            _items.Clear();
            _secondary.Clear();
            RawBox.Text = "";
            DowntimeBox.Text = "0";
            ReasonBox.Text = "";
            NotesBox.Text = "";
            ActualFieldsOuter.Visibility = Visibility.Collapsed;
            RecordedSummaryBar.Visibility = Visibility.Collapsed;
            CustChip.Text = "👤 العميل: —";
            PlanChip.Text = "📋 الخطة: —";
            ShiftChip.Text = "⏰ الوردية: —";
            DocNoBadge.Text = "تسجيل الفعلي";
            RefreshSecondarySummary();
        }
        finally
        {
            _loading = false;
        }
        UpdateActionButtons();
    }

    private void Order_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ItemsGrid == null) return;
        if (OrderBox.SelectedItem is ActualDeliveryOrderDto order)
            BindOrder(order);
    }

    private void Delivery_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ItemsGrid == null) return;
        if (DeliveryBox.SelectedItem is ProductionDeliveryCard card)
            BindDelivery(card.Id);
    }

    private void DefaultWarehouse_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingDefaultWarehouse || _loading) return;
        if (DefaultWarehouseBox.SelectedItem is not WarehouseOptionDto wh) return;
        bool canModifyWh = (_currentOrder?.CanRecord == true && CanProduction("Create"))
            || (_currentOrder is { Recorded: true, CanCreateDelivery: true } && _currentDelivery == null && CanProduction("Create"))
            || (_currentDelivery?.Status == DocStatuses.Draft && CanProduction("Edit"));
        if (!canModifyWh) return;
        foreach (var row in _items)
        {
            row.WarehouseId = wh.Id;
            row.WarehouseName = wh.DisplayName;
        }
    }

    private void BindOrder(ActualDeliveryOrderDto order)
    {
        _loading = true;
        try
        {
            _currentOrder = order;
            _currentDelivery = null;
            if (order != null && order.ProductionDeliveryId > 0)
            {
                _currentDelivery = WithService(s => s.GetDelivery(order.ProductionDeliveryId));
                DeliveryBox.SelectedItem = _deliveries.FirstOrDefault(d => d.Id == order.ProductionDeliveryId);
            }
            else if (order != null)
            {
                var existing = _deliveries.FirstOrDefault(d => d.OrderId == order.OrderId && d.Status != DocStatuses.Cancelled);
                if (existing != null)
                {
                    _currentDelivery = WithService(s => s.GetDelivery(existing.Id));
                    DeliveryBox.SelectedItem = existing;
                }
                else
                {
                    DeliveryBox.SelectedIndex = -1;
                }
            }

            PopulateFromOrderAndCard(order, _currentDelivery);
        }
        finally
        {
            _loading = false;
        }
    }

    private void BindDelivery(int deliveryId)
    {
        _loading = true;
        try
        {
            var card = WithService(s => s.GetDelivery(deliveryId));
            if (card == null)
            {
                StatusLabel.Text = "مستند التسليم غير موجود؛ حدّث القائمة.";
                return;
            }
            _currentDelivery = card;
            _currentOrder = _orders.FirstOrDefault(o => o.OrderId == card.OrderId)
                ?? _orders.FirstOrDefault(o => o.ProductionDeliveryId == card.Id);
            if (_currentOrder == null && card.OrderId is int wantedId)
                _currentOrder = WithService(s => s.GetActualDeliveryOrders((int?)wantedId)).FirstOrDefault(o => o.OrderId == wantedId);

            OrderBox.SelectedItem = _currentOrder != null ? _orders.FirstOrDefault(o => o.OrderId == _currentOrder.OrderId) : null;
            if (DatesErp.Core.Common.UiFormat.TryParseDate(card.DeliveryDate, out var dt))
                DeliveryDateBox.SelectedDate = dt;
            PopulateFromOrderAndCard(_currentOrder, card);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "تعذر فتح مستند التسليم: " + ex.Message;
            ErrorLog.Write(ex, "ProductionDelivery.BindDelivery");
        }
        finally
        {
            _loading = false;
        }
    }

    private void PopulateFromOrderAndCard(ActualDeliveryOrderDto order, ProductionDeliveryCard card)
    {
        _items.Clear();
        _secondary.Clear();
        RawBox.Text = "";
        DowntimeBox.Text = "0";
        ReasonBox.Text = "";
        NotesBox.Text = card?.Notes ?? order?.Notes ?? "";

        ByProductDefinitions.Clear();
        foreach (var def in _activeDefinitions) ByProductDefinitions.Add(def);

        string cust = order?.Customer ?? card?.CustomerSummary ?? "—";
        string plan = order?.PlanNumber ?? card?.PlanNumber ?? "—";
        string shiftLine = string.Join(" · ", new[]
        {
            order?.Shift ?? card?.ShiftName,
            order?.LineName ?? card?.LineName
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
        CustChip.Text = $"👤 العميل: {cust}";
        PlanChip.Text = $"📋 الخطة: {plan}";
        ShiftChip.Text = $"⏰ الوردية: {(string.IsNullOrWhiteSpace(shiftLine) ? "—" : shiftLine)}";

        DocNoBadge.Text = card?.DocumentNumber
            ?? (order?.CanRecord == true ? $"{order.OrderNumber} · قيد التسجيل" : order?.OrderNumber ?? "تسجيل الفعلي");

        bool canRecord = order?.CanRecord == true && CanProduction("Create") && CanExecution("Edit");
        bool continuation = order != null && order.Recorded && order.CanRecord;
        bool historical = order != null && order.Recorded && !order.CanRecord;

        int? fallbackWh = DefaultWarehouseId;
        if (order != null)
        {
            if (order.Items.Count == 0)
            {
                StatusLabel.Text = $"⚠ الأمر {order.OrderNumber} بلا بنود خطة صالحة للتسجيل — راجع أمر الإنتاج والخطة المعتمدة.";
            }
            else
            {
                int itemIndex = 1;
                foreach (var item in order.Items)
                {
                    var row = new ActualProductionRow(item, historical, continuation);
                    row.LineNo = itemIndex++;
                    var matchedLine = card?.Lines?.FirstOrDefault(l =>
                        l.ByProductId == null
                        && l.ProductId == item.ProductId
                        && l.LotId == item.LotId
                        && l.CustomerId == item.CustomerId
                        && l.PackagingTypeId == item.PackagingTypeId);
                    row.WarehouseId = matchedLine?.WarehouseId ?? fallbackWh;
                    row.WarehouseName = matchedLine?.WarehouseName
                        ?? FinishedWarehouses.FirstOrDefault(w => w.Id == row.WarehouseId)?.DisplayName
                        ?? "—";
                    _items.Add(row);
                }
            }
        }
        else if (card != null)
        {
            int itemIndex = 1;
            foreach (var line in card.Lines.Where(l => l.ByProductId == null))
            {
                var synthetic = new ActualDeliveryItemDto
                {
                    OrderItemId = 0,
                    ProductId = line.ProductId,
                    LotId = line.LotId,
                    CustomerId = line.CustomerId,
                    PackagingTypeId = line.PackagingTypeId,
                    Customer = line.CustomerName ?? "—",
                    Product = line.ProductName ?? "—",
                    Unit = "كرتون",
                    PlannedCartons = line.PackageCount,
                    ActualCartons = line.PackageCount,
                    PlannedQtyKg = line.PlannedQtyKg > 0 ? line.PlannedQtyKg : line.QtyKg,
                    ActualQtyKg = line.ActualQtyKg > 0 ? line.ActualQtyKg : line.QtyKg
                };
                var row = new ActualProductionRow(synthetic, recorded: true, continuation: false)
                {
                    LineNo = itemIndex++,
                    WarehouseId = line.WarehouseId ?? fallbackWh,
                    WarehouseName = line.WarehouseName ?? "—"
                };
                _items.Add(row);
            }
        }

        if (canRecord && _secondary.Count == 0)
        {
            if (_activeDefinitions.Count > 0)
                _secondary.Add(new ActualSecondaryRow());
        }
        RefreshSecondarySummary();

        ActualColumn.Header = continuation ? "فعلي الجلسة ✏️" : "المنتج فعليًا ✏️";
        ActualColumn.IsReadOnly = !canRecord;
        bool canEditWarehouse = canRecord
            || (order is { Recorded: true, CanCreateDelivery: true } && card == null && CanProduction("Create"))
            || (card?.Status == DocStatuses.Draft && CanProduction("Edit"));
        WarehouseColumn.IsReadOnly = !canEditWarehouse;
        ItemsGrid.IsReadOnly = !canRecord && !canEditWarehouse;

        ActualFieldsOuter.Visibility = canRecord ? Visibility.Visible : Visibility.Collapsed;
        RecordedSummaryBar.Visibility = !canRecord && (order?.Recorded == true || card != null)
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (canRecord)
        {
            if (continuation)
            {
                double remKg = Math.Max(0, order.PlannedQtyKg - order.ActualQtyKg);
                StatusLabel.Text = $"{order.Status} — سبق تسجيل {order.ActualQtyKg:N1} كجم والمتبقي {remKg:N1} كجم؛ أدخل كميات هذه الجلسة فقط.";
            }
            else if (order.Items.Count > 0)
            {
                StatusLabel.Text = order.Status ?? "أدخل الفعلي فقط؛ المخطط ثابت من أمر الإنتاج.";
            }
        }
        else if (order?.Recorded == true || card != null)
        {
            double rawKg = order?.ConsumedRawKg ?? 0;
            double downHrs = order?.DowntimeHours ?? 0;
            string downReason = string.IsNullOrWhiteSpace(order?.DowntimeReason) ? "بلا توقف" : order.DowntimeReason;
            string bpSummary = order?.ByProducts is { Count: > 0 }
                ? $"{order.ByProducts.Sum(b => b.QtyKg):N1} كجم ({order.ByProducts.Count} بند)"
                : "لا يوجد";
            string pdlvInfo = card != null
                ? $"{card.DocumentNumber} ({card.StatusAr})"
                : (!string.IsNullOrWhiteSpace(order?.ProductionDeliveryNumber)
                    ? $"{order.ProductionDeliveryNumber} ({order.ProductionDeliveryStatus})"
                    : "لم يُنشأ بعد");
            string qcInfo = !string.IsNullOrWhiteSpace(order?.QualityNumber) ? order.QualityNumber : "بانتظار الفحص";

            RecordedRawChip.Text = $"الخام المستهلك: {rawKg:N1} كجم";
            RecordedDowntimeChip.Text = $"التوقف: {downHrs:N1} س ({downReason})";
            RecordedByProdChip.Text = $"المخلفات: {bpSummary}";
            RecordedDocsChip.Text = $"أمر التسليم: {pdlvInfo} · الفحص: {qcInfo}";

            if (card != null)
            {
                StatusLabel.Text = $"{card.DocumentNumber} — {card.StatusAr} — أمر الإنتاج: {card.OrderNumber ?? order?.OrderNumber ?? "—"} — الاستلام المخزني: {ReceiptStatusAr(card.ReceiptStatus)}";
            }
            else if (order != null)
            {
                StatusLabel.Text = $"{order.Status} — الفعلي مسجل ({order.ActualQtyKg:N1} كجم). أنشئ أو حرّر أمر التسليم لتوجيه المنتج إلى مخازن التام.";
            }
        }
        else if (order != null)
        {
            StatusLabel.Text = $"{order.Status} — التسجيل غير متاح للصلاحية الحالية أو لحالة الأمر.";
        }

        UpdateActionButtons();
    }

    private void UpdateActionButtons()
    {
        bool canRecord = _currentOrder?.CanRecord == true && CanProduction("Create") && CanExecution("Edit");
        bool canCreateDeliveryFromRecorded = _currentOrder is { Recorded: true, CanCreateDelivery: true }
            && _currentDelivery == null && CanProduction("Create");
        bool canSaveDraft = _currentDelivery?.Status == DocStatuses.Draft && CanProduction("Edit");

        if (canRecord)
        {
            SaveButton.Content = "💾 حفظ الفعلي واستلام الإنتاج وإرساله للفحص";
            SaveButton.IsEnabled = true;
            FooterActionHint.Text = "يحفظ الإنتاج الفعلي ويُجهّز الفحص ومسودة أمر التسليم بالمخازن المختارة.";
        }
        else if (canCreateDeliveryFromRecorded)
        {
            SaveButton.Content = "💾 إنشاء مسودة أمر التسليم وتوجيه المخازن";
            SaveButton.IsEnabled = true;
            FooterActionHint.Text = "الفعلي مسجل مسبقاً — اضغط لإنشاء مسودة أمر التسليم وتثبيت مخزن التام الوجهة لكل بند.";
        }
        else if (canSaveDraft)
        {
            SaveButton.Content = "💾 حفظ توجيه المخازن على مسودة التسليم";
            SaveButton.IsEnabled = true;
            FooterActionHint.Text = $"المسودة {_currentDelivery.DocumentNumber} جاهزة — احفظ أي تعديل على المخازن ثم اضغط «📤 تحرير أمر التسليم للمخزن».";
        }
        else
        {
            SaveButton.Content = "💾 حفظ الفعلي واستلام الإنتاج وإرساله للفحص";
            SaveButton.IsEnabled = true; // يبقى مفعلاً ليعرض سبب المنع بوضوح عند النقر بدل الصمت
            FooterActionHint.Text = _currentDelivery?.Status is DocStatuses.Issued or DocStatuses.Completed
                ? $"تم تحرير {_currentDelivery.DocumentNumber} للمخزن — انتقل إلى «📥 سند الاستلام المخزني» أو «🔍 فحص الجودة»."
                : "اختر أمر إنتاج معتمداً قابلاً لتسجيل الفعلي من الأعلى.";
        }

        IssueDeliveryBtn.IsEnabled = _currentDelivery?.Status == DocStatuses.Draft && CanProduction("Approve");
        OpenReceiptBtn.IsEnabled = _currentDelivery != null
            && _currentDelivery.Status is (DocStatuses.Issued or DocStatuses.Completed)
            && CanFinishedGoods("View");
        OpenQualityBtn.IsEnabled = (_currentOrder?.Recorded == true || _currentDelivery != null)
            && DatesErp.Desktop.Views.PermissionGate.Can("quality", "View");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        // تعطيل زر الحفظ فور بدء الحفظ لمنع التكرار
        SaveButton.IsEnabled = false;
        try
        {
            ItemsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            ItemsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            SecondaryGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            SecondaryGrid.CommitEdit(DataGridEditingUnit.Row, true);

            // الحالة ١: تسجيل الإنتاج الفعلي للأمر ثم إنشاء مسودة أمر التسليم وتوجيه المخازن
            if (_currentOrder?.CanRecord == true)
            {
                if (!CanProduction("Create") || !CanExecution("Edit"))
                {
                    FailLoud("لا تملك صلاحية إنشاء الإنتاج وتعديل التنفيذ لتسجيل الفعلي.");
                    return;
                }
                if (_items.Count == 0)
                {
                    FailLoud("الأمر بلا بنود خطة صالحة للتسجيل.");
                    return;
                }
                if (_items.Any(i => !i.TryQuantity(out _)))
                {
                    FailLoud("أدخل عدد الكراتين المنتجة فعليًا في العمود الأصفر لكل بند (الصفر يُكتب 0 صراحةً ولا يتجاوز المخطط).");
                    return;
                }
                if (!ActualProductionRow.TryNonnegative(RawBox.Text, out var rawKg) || rawKg <= 0)
                {
                    FailLoud("أدخل الخام المستهلك فعليًا بالكيلوجرام (أكبر من صفر).");
                    return;
                }
                if (!ActualProductionRow.TryNonnegative(DowntimeBox.Text, out var downHours) || downHours > 24)
                {
                    FailLoud("أدخل ساعات توقف صحيحة بين 0 و 24 ساعة.");
                    return;
                }
                if (downHours > 0 && string.IsNullOrWhiteSpace(ReasonBox.Text))
                {
                    FailLoud("أدخل سبب التوقف في خانة السبب عند وجود ساعات توقف.");
                    return;
                }
                if (downHours == 0 && !string.IsNullOrWhiteSpace(ReasonBox.Text))
                {
                    FailLoud("أفرغ سبب التوقف طالما أن ساعات التوقف تساوي صفرًا.");
                    return;
                }

                var warehouseByItemKey = BuildWarehouseChoices();

                var input = new ActualProductionDto
                {
                    OrderId = _currentOrder.OrderId,
                    ConsumedRawKg = rawKg,
                    DowntimeHours = downHours,
                    DowntimeReason = ReasonBox.Text?.Trim(),
                    Notes = NotesBox.Text?.Trim()
                };
                foreach (var row in _items)
                {
                    row.TryQuantity(out var qty);
                    input.Items.Add(new ActualProductionItemDto
                    {
                        OrderItemId = row.Source.OrderItemId,
                        ActualCartons = qty
                    });
                }
                foreach (var sec in _secondary)
                {
                    if (sec.Definition == null && string.IsNullOrWhiteSpace(sec.Quantity)) continue;
                    if (sec.Definition == null || !ActualProductionRow.TryNonnegative(sec.Quantity, out var secQty) || secQty <= 0)
                    {
                        FailLoud("اختر المخرج الثانوي من التعريفات وأدخل كمية موجبة، أو احذف السطر بمفتاح Delete.");
                        return;
                    }
                    if (input.ByProducts.Any(b => b.ByProductId == sec.Definition.Id))
                    {
                        FailLoud($"المخرج الثانوي «{sec.Definition.Name}» مكرر — اجمعه في سطر واحد.");
                        return;
                    }
                    input.ByProducts.Add(new ByProductQtyDto { ByProductId = sec.Definition.Id, QtyKg = secQty });
                }

                _saving = true;
                var saveRes = WithService(s => s.SaveActualProduction(input));
                if (!saveRes.Ok)
                {
                    FailLoud(saveRes.Message);
                    return;
                }

                string deliveryNote = "";
                int? createdDeliveryId = null;
                var refreshedOrder = WithService(s => s.GetActualDeliveryOrders((int?)_currentOrder.OrderId))
                    .FirstOrDefault(o => o.OrderId == _currentOrder.OrderId);
                if (refreshedOrder is { Recorded: true, ExecutionId: > 0, CanCreateDelivery: true } && CanProduction("Create"))
                {
                    string delivDate = (DeliveryDateBox.SelectedDate ?? DateTime.Today).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
                    var draftRes = WithService(s => s.CreateDeliveryFromActual(refreshedOrder.ExecutionId, delivDate, input.Notes));
                    if (draftRes.Ok && draftRes.Id > 0)
                    {
                        createdDeliveryId = draftRes.Id;
                        ApplyWarehouseChoicesToDraft(draftRes.Id, delivDate, input.Notes, warehouseByItemKey);
                        deliveryNote = $"\n📄 مسودة أمر التسليم: {draftRes.DocumentNumber ?? ("#" + draftRes.Id)} (اضغط «📤 تحرير أمر التسليم للمخزن» لاعتمادها للمخازن).";
                    }
                }

                LoadData(null, null);
                string finalMsg = saveRes.Message + deliveryNote;
                StatusLabel.Text = finalMsg.Replace("\n", " — ");
                _notifyInfo(finalMsg);
                SaveButton.IsEnabled = false; // بعد الحفظ الناجح يُعطّل الزر حتى لا يُعاد الإرسال
                return;
            }

            // الحالة ٢: الفعلي مسجل مسبقاً ولا توجد مسودة تسليم بعد -> إنشاء مسودة وتثبيت المخازن
            if (_currentOrder is { Recorded: true, ExecutionId: > 0, CanCreateDelivery: true } && _currentDelivery == null)
            {
                if (!CanProduction("Create"))
                {
                    FailLoud("صلاحية production/Create مطلوبة لإنشاء مسودة أمر التسليم.");
                    return;
                }
                _saving = true;
                var whMap = BuildWarehouseChoices();
                string delivDate = (DeliveryDateBox.SelectedDate ?? DateTime.Today).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
                var draftRes = WithService(s => s.CreateDeliveryFromActual(_currentOrder.ExecutionId, delivDate, NotesBox.Text));
                if (!draftRes.Ok)
                {
                    FailLoud(draftRes.Message);
                    return;
                }
                ApplyWarehouseChoicesToDraft(draftRes.Id, delivDate, NotesBox.Text, whMap);
                LoadData(_currentOrder.OrderId, draftRes.Id);
                _notifyInfo(draftRes.Message);
                SaveButton.IsEnabled = false; // تم إنشاء المسودة — أعد التفعيل عبر UpdateActionButtons
                return;
            }

            // الحالة ٣: تحديث توجيه المخازن على مسودة أمر تسليم قائمة
            if (_currentDelivery is { Id: > 0, Status: DocStatuses.Draft })
            {
                if (!CanProduction("Edit"))
                {
                    FailLoud("صلاحية production/Edit مطلوبة لتعديل مسودة أمر التسليم.");
                    return;
                }
                _saving = true;
                var whMap = BuildWarehouseChoices();
                string delivDate = (DeliveryDateBox.SelectedDate ?? DateTime.Today).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
                var upd = ApplyWarehouseChoicesToDraft(_currentDelivery.Id, delivDate, NotesBox.Text, whMap);
                if (!upd.Ok)
                {
                    FailLoud(upd.Message);
                    return;
                }
                int id = _currentDelivery.Id;
                LoadData(_currentDelivery.OrderId ?? _currentOrder?.OrderId, id);
                StatusLabel.Text = upd.Message;
                _notifyInfo(upd.Message);
                return;
            }

            string reason = _currentOrder == null
                ? "اختر أمر إنتاج معتمداً من القائمة العلوية أولاً."
                : _currentDelivery != null && _currentDelivery.Status != DocStatuses.Draft
                    ? $"أمر التسليم {_currentDelivery.DocumentNumber} في حالة «{_currentDelivery.StatusAr}» ومقفل للتعديل؛ استخدم أزرار الاستلام المخزني أو الجودة."
                    : (_currentOrder.Status ?? "الأمر الحالي غير قابل لتسجيل الفعلي أو تعديل المسودة.");
            FailLoud(reason);
        }
        catch (Exception ex)
        {
            FailLoud(ex.Message);
            ErrorLog.Write(ex, "ProductionDelivery.Save");
        }
        finally
        {
            _saving = false;
            // أعد تقييم حالة الزر بعد انتهاء العملية (نجاحاً أو فشلاً)
            UpdateActionButtons();
        }
    }

    private Dictionary<(int ProductId, int? LotId, int? CustomerId, int? PackagingTypeId), int?> BuildWarehouseChoices()
        => _items.GroupBy(r => (r.Source.ProductId, r.Source.LotId, r.Source.CustomerId, r.Source.PackagingTypeId))
            .ToDictionary(g => g.Key,
                g => g.Select(r => r.WarehouseId).FirstOrDefault(warehouseId => warehouseId is > 0) ?? DefaultWarehouseId);

    private OpResult ApplyWarehouseChoicesToDraft(
        int deliveryId,
        string deliveryDate,
        string notes,
        Dictionary<(int ProductId, int? LotId, int? CustomerId, int? PackagingTypeId), int?> whMap)
    {
        var card = WithService(s => s.GetDelivery(deliveryId));
        if (card == null || card.Lines.Count == 0) return OpResult.Success();
        var items = card.Lines
            .Where(l => l.ByProductId == null && l.QtyKg > 0.001)
            .Select(l =>
            {
                var key = (l.ProductId, l.LotId, l.CustomerId, l.PackagingTypeId);
                int? chosenWh = whMap.TryGetValue(key, out var wh) && wh is > 0 ? wh : (l.WarehouseId ?? DefaultWarehouseId);
                return new ProductionDeliveryItemDto
                {
                    OrderId = card.OrderId,
                    ProductId = l.ProductId,
                    LotId = l.LotId,
                    CustomerId = l.CustomerId,
                    PackagingTypeId = l.PackagingTypeId,
                    PackageCount = l.PackageCount,
                    WarehouseId = chosenWh,
                    QtyKg = l.QtyKg
                };
            }).ToList();
        if (items.Count == 0) return OpResult.Success();
        return WithService(s => s.UpdateDelivery(deliveryId, deliveryDate, items, notes ?? card.Notes));
    }

    private void FailLoud(string reason)
    {
        string msg = "⚠ لا يمكن الحفظ: " + reason;
        StatusLabel.Text = msg;
        if (_serviceResolver == null)
            AppContainer.Get<DialogService>().Error(msg);
    }

    private void CreateDraftOnly()
    {
        if (_currentOrder is { Recorded: true, ExecutionId: > 0, CanCreateDelivery: true } && _currentDelivery == null)
            Save_Click(null, null);
        else if (_currentOrder?.CanRecord == true)
            StatusLabel.Text = "سجّل الفعلي أولاً بالضغط على «💾 حفظ الفعلي واستلام الإنتاج وإرساله للفحص»؛ ستُنشأ مسودة التسليم تلقائياً.";
        else
            StatusLabel.Text = "لا توجد كمية فعلية متبقية غير مغطاة بأمر تسليم لهذا الأمر.";
    }

    private void EnableDraftEdit()
    {
        if (_currentDelivery?.Status == DocStatuses.Draft && CanProduction("Edit"))
        {
            WarehouseColumn.IsReadOnly = false;
            ItemsGrid.IsReadOnly = false;
            StatusLabel.Text = $"المسودة {_currentDelivery.DocumentNumber} مفعّلة لتعديل المخزن الوجهة لكل بند قبل التحرير.";
        }
        else
        {
            StatusLabel.Text = "التعديل متاح لمسودة أمر التسليم فقط قبل تحريرها للمخزن.";
        }
    }

    private void IssueDelivery_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDelivery == null || _currentDelivery.Id <= 0)
        {
            StatusLabel.Text = "احفظ الفعلي وأنشئ مسودة أمر التسليم أولاً قبل تحريرها للمخزن.";
            return;
        }
        if (_currentDelivery.Status != DocStatuses.Draft)
        {
            StatusLabel.Text = $"المستند {_currentDelivery.DocumentNumber} ليس مسودة ({_currentDelivery.StatusAr}).";
            return;
        }
        if (!CanProduction("Approve"))
        {
            FailLoud("صلاحية production/Approve مطلوبة لتحرير أمر التسليم للمخزن.");
            return;
        }

        ItemsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        ItemsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (CanProduction("Edit"))
        {
            var whMap = BuildWarehouseChoices();
            string delivDate = (DeliveryDateBox.SelectedDate ?? DateTime.Today).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            var upd = ApplyWarehouseChoicesToDraft(_currentDelivery.Id, delivDate, _currentDelivery.Notes, whMap);
            if (!upd.Ok)
            {
                FailLoud(upd.Message);
                return;
            }
        }

        if (_serviceResolver == null && !AppContainer.Get<DialogService>().Confirm(
            $"تحرير أمر التسليم {_currentDelivery.DocumentNumber} للمخزن بالمخازن الوجهة المحددة؟\nالتحرير لا يرحّل الأرصدة تلقائياً؛ يُنفَّذ الاستلام الفعلي من شاشة «أوامر استلام الإنتاج»."))
            return;

        try
        {
            var res = WithService(s => s.IssueDelivery(_currentDelivery.Id));
            if (!res.Ok)
            {
                FailLoud(res.Message);
                return;
            }
            int id = _currentDelivery.Id;
            int? orderId = _currentDelivery.OrderId ?? _currentOrder?.OrderId;
            LoadData(orderId, id);
            StatusLabel.Text = res.Message + " — جاهز الآن للاستلام المخزني وفحص الجودة.";
            _notifyInfo(res.Message);
        }
        catch (Exception ex)
        {
            FailLoud(ex.Message);
            ErrorLog.Write(ex, "ProductionDelivery.Issue");
        }
    }

    private void CancelDelivery()
    {
        if (_currentDelivery == null || _currentDelivery.Id <= 0)
        {
            StatusLabel.Text = "اختر مستند تسليم محفوظاً لإلغائه.";
            return;
        }
        if (!CanProduction("Cancel"))
        {
            FailLoud("صلاحية production/Cancel مطلوبة لإلغاء أمر التسليم.");
            return;
        }
        if (_serviceResolver == null && !AppContainer.Get<DialogService>().Confirm(
            $"هل تريد إلغاء أمر التسليم {_currentDelivery.DocumentNumber}؟"))
            return;
        try
        {
            var res = WithService(s => s.CancelDelivery(_currentDelivery.Id));
            if (!res.Ok) { FailLoud(res.Message); return; }
            LoadData(_currentDelivery.OrderId ?? _currentOrder?.OrderId, _currentDelivery.Id);
            StatusLabel.Text = res.Message;
        }
        catch (Exception ex)
        {
            FailLoud(ex.Message);
            ErrorLog.Write(ex, "ProductionDelivery.Cancel");
        }
    }

    private void OpenReceipt_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDelivery == null || _currentDelivery.Id <= 0)
        {
            StatusLabel.Text = "احفظ وحرّر أمر التسليم أولاً قبل فتح سند الاستلام المخزني.";
            return;
        }
        if (_currentDelivery.Status == DocStatuses.Draft)
        {
            StatusLabel.Text = "أمر التسليم ما زال مسودة — اضغط «📤 تحرير أمر التسليم للمخزن» أولاً.";
            return;
        }
        if (!CanFinishedGoods("View"))
        {
            FailLoud("صلاحية عرض استلام الإنتاج التام (finishedgoods) مطلوبة.");
            return;
        }
        FGReceiveView.PendingDeliveryId = _currentDelivery.Id;
        (Window.GetWindow(this) as MainWindow)?.OpenScreen("fgreceive");
    }

    private void OpenQuality_Click(object sender, RoutedEventArgs e)
    {
        if (!DatesErp.Desktop.Views.PermissionGate.Can("quality", "View"))
        {
            FailLoud("صلاحية عرض الجودة مطلوبة لفتح شاشة فحوصات الجودة.");
            return;
        }
        (Window.GetWindow(this) as MainWindow)?.OpenScreen("quality");
    }

    private void OpenOrders_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.OpenScreen("orders");

    private void Search_Click(object sender, RoutedEventArgs e) => OpenSearchWindow(SearchBox.Text);

    private void OpenSearchWindow(string initialTerm = null)
    {
        try
        {
            var idMap = new Dictionary<int, (int? orderId, int? deliveryId)>();
            var win = new DocSearchWindow("أوامر الإنتاج ومستندات التسليم",
                new List<SearchFieldDef> { new() { Key = "term", LabelAr = "رقم الأمر أو العميل أو الخطة أو التسليم", DefaultValue = initialTerm } }, filters =>
                {
                    idMap.Clear();
                    var term = ScreenSearch.Normalize(filters.GetValueOrDefault("term") ?? "");
                    var orders = WithService(s => s.GetActualDeliveryOrders(null, true)) ?? new List<ActualDeliveryOrderDto>();
                    var deliveries = WithService(s => s.GetDeliveries(null)) ?? new List<ProductionDeliveryCard>();
                    var resultRows = new List<(int id, object[] cells)>();
                    int key = 1;
                    foreach (var o in orders.Where(o => string.IsNullOrEmpty(term)
                        || ScreenSearch.Normalize(o.Label).Contains(term)
                        || ScreenSearch.Normalize(o.OrderNumber).Contains(term)
                        || ScreenSearch.Normalize(o.Customer).Contains(term)
                        || ScreenSearch.Normalize(o.PlanNumber).Contains(term)
                        || (o.Items ?? new List<ActualDeliveryItemDto>()).Any(i => ScreenSearch.Normalize(i.Product).Contains(term))))
                    {
                        idMap[key] = (o.OrderId, null);
                        resultRows.Add((key++, new object[] { "أمر إنتاج", o.OrderNumber, o.Customer, o.PlanNumber, o.ProductionDate,
                            o.Recorded ? "فعلي محفوظ" : o.CanRecord ? "جاهز لتسجيل الفعلي" : "غير متاح" }));
                    }
                    foreach (var d in deliveries.Where(d => string.IsNullOrEmpty(term)
                        || ScreenSearch.Normalize(d.DocumentNumber).Contains(term)
                        || ScreenSearch.Normalize(d.OrderNumber).Contains(term)
                        || ScreenSearch.Normalize(d.SourceNumber).Contains(term)
                        || ScreenSearch.Normalize(d.CustomerSummary).Contains(term)
                        || ScreenSearch.Normalize(d.StatusAr).Contains(term)))
                    {
                        idMap[key] = (null, d.Id);
                        resultRows.Add((key++, new object[] { "تسليم إنتاج", d.DocumentNumber, d.CustomerSummary, d.OrderNumber, d.SourceNumber, d.StatusAr }));
                    }
                    return new SearchResult
                    {
                        Columns = new List<string> { "النوع", "رقم المستند", "العميل", "الأمر/الخطة", "التاريخ/المصدر", "الحالة" },
                        Rows = resultRows
                    };
                }) { Owner = Window.GetWindow(this) };
            if (win.ShowDialog() == true && win.SelectedId is int selected && idMap.TryGetValue(selected, out var target))
            {
                SearchBox.Text = "";
                LoadData(target.orderId, target.deliveryId);
            }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "ProductionDelivery.Search"); }
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        LoadData(null, null);
    }

    private static bool Contains(string value, string term)
        => !string.IsNullOrWhiteSpace(value) && value.Contains(term, StringComparison.CurrentCultureIgnoreCase);

    private void Undo()
        => LoadData(_currentOrder?.OrderId, _currentDelivery?.Id);

    private void Print()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErp.Infrastructure.Persistence.DatesErpDbContext>();
            if (_currentDelivery is { Id: > 0 })
            {
                var model = Printing.StoredPrintModels.ProductionDelivery(db, _currentDelivery.Id);
                new PrintPreviewWindow(PhasePrint.Build(model), $"{model.DocTitle} {model.DocNo}")
                { Owner = Window.GetWindow(this) }.ShowDialog();
                return;
            }
            if (_currentOrder is { Recorded: true, OrderId: > 0 })
            {
                var execModel = Printing.StoredPrintModels.Execution(db, _currentOrder.OrderId);
                new PrintPreviewWindow(PhasePrint.Build(execModel), $"{execModel.DocTitle} {execModel.DocNo}")
                { Owner = Window.GetWindow(this) }.ShowDialog();
                return;
            }
            StatusLabel.Text = "احفظ الفعلي أو اختر مستند تسليم محفوظاً قبل الطباعة.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "تعذرت الطباعة: " + ex.Message;
            ErrorLog.Write(ex, "ProductionDelivery.Print");
        }
    }

    private static string ReceiptStatusAr(string state) => state switch
    {
        "Full" => "مستلم بالكامل",
        "Partial" => "مستلم جزئياً",
        _ => "لم يبدأ الاستلام"
    };

    private void AddSecondaryRow_Click(object sender, RoutedEventArgs e)
    {
        if (_currentOrder?.CanRecord != true || !CanProduction("Create") || !CanExecution("Edit"))
        {
            FailLoud("اختر أمر إنتاج قابلاً لتسجيل الفعلي قبل إضافة مخرج ثانوي.");
            return;
        }
        if (ByProductDefinitions.Count == 0)
        {
            FailLoud("لا توجد تعريفات مخرجات ثانوية نشطة. أضف التعريف من إعدادات الأصناف ثم حدّث الشاشة.");
            return;
        }
        var row = new ActualSecondaryRow();
        _secondary.Add(row);
        SecondaryGrid.SelectedItem = row;
        SecondaryGrid.ScrollIntoView(row);
        RefreshSecondarySummary();
    }

    private void Secondary_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (ActualSecondaryRow row in e.OldItems) row.PropertyChanged -= SecondaryRow_PropertyChanged;
        if (e.NewItems != null)
            foreach (ActualSecondaryRow row in e.NewItems) row.PropertyChanged += SecondaryRow_PropertyChanged;
        int secIndex = 1;
        foreach (var sec in _secondary) sec.LineNo = secIndex++;
        RefreshSecondarySummary();
    }

    private void SecondaryRow_PropertyChanged(object sender, PropertyChangedEventArgs e)
        => RefreshSecondarySummary();

    private void RefreshSecondarySummary()
    {
        if (SecondaryTotalLabel == null) return;
        double total = _secondary
            .Where(r => ActualProductionRow.TryNonnegative(r.Quantity, out var quantity) && quantity > 0)
            .Sum(r => ActualProductionRow.TryNonnegative(r.Quantity, out var quantity) ? quantity : 0);
        SecondaryTotalLabel.Text = $"إجمالي الجلسة: {total:N1} كجم";

        var previous = _currentOrder?.ByProducts?.Where(b => b.QtyKg > 0).Select(b =>
        {
            var def = ByProductDefinitions.FirstOrDefault(d => d.Id == b.ByProductId);
            return $"{def?.Name ?? $"مخرج #{b.ByProductId}"}: {b.QtyKg:N1} كجم";
        }).ToList() ?? new List<string>();
        SecondaryHistoryLabel.Text = ByProductDefinitions.Count == 0 && _currentOrder?.CanRecord == true
            ? "لا توجد تعريفات مخرجات ثانوية نشطة — أضفها من إعدادات الأصناف"
            : previous.Count > 0
                ? "المسجل في الجلسات السابقة: " + string.Join("، ", previous)
                : (_currentOrder?.Recorded == true ? "لا توجد مخرجات ثانوية سابقة مسجلة" : "");
        AddSecondaryButton.IsEnabled = _currentOrder?.CanRecord == true
            && CanProduction("Create") && CanExecution("Edit") && ByProductDefinitions.Count > 0;
        SecondaryGrid.IsReadOnly = _currentOrder?.CanRecord != true
            || !CanProduction("Create") || !CanExecution("Edit");
    }

    private void Secondary_LoadingRow(object sender, DataGridRowEventArgs e)
    {
        if (e.Row.Item is not ActualSecondaryRow)
        {
            e.Row.Background = new SolidColorBrush(Color.FromRgb(0xF8, 0xF7, 0xF1));
            e.Row.Opacity = 0.82;
            e.Row.ToolTip = "سطر فارغ للإضافة — اختر المخرج الثانوي وأدخل كميته عند وجود مخلفات";
        }
        else
        {
            e.Row.ClearValue(Control.BackgroundProperty);
            e.Row.Opacity = 1.0;
            e.Row.ToolTip = "اضغط Delete لحذف السطر المحدد";
        }
    }

    private void Secondary_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is DataGridCell { IsReadOnly: false, IsEditing: false } cell && !SecondaryGrid.IsReadOnly)
            SecondaryGrid.BeginEdit(e);
    }

    private void Secondary_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || SecondaryGrid.IsReadOnly) return;
        if (SecondaryGrid.SelectedItem is ActualSecondaryRow row)
        {
            SecondaryGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            SecondaryGrid.CommitEdit(DataGridEditingUnit.Row, true);
            _secondary.Remove(row);
            e.Handled = true;
        }
    }

    private void Grid_SingleClickEdit(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || grid.IsReadOnly) return;
        var dep = e.OriginalSource as DependencyObject;
        while (dep is not null and not DataGridCell) dep = VisualTreeHelper.GetParent(dep);
        if (dep is DataGridCell { IsReadOnly: false, IsEditing: false } cell)
        {
            if (e.OriginalSource is DependencyObject orig)
            {
                var comboCheck = orig;
                while (comboCheck != null && comboCheck != cell)
                {
                    if (comboCheck is ComboBox) return;
                    comboCheck = VisualTreeHelper.GetParent(comboCheck);
                }
            }
            grid.CommitEdit(DataGridEditingUnit.Row, true);
            grid.CurrentCell = new DataGridCellInfo(cell);
            grid.BeginEdit();
        }
    }
}
