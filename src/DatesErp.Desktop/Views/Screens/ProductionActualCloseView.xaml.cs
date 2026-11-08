using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows;
using System.Windows.Controls;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Core.Domain.Entities;
using DatesErp.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// شاشة الإنتاج الوحيدة لتسجيل الكميات الفعلية وإقفال جلسات التنفيذ.
/// لا تنشئ أو تحرر مستند تسليم أو استلاماً مخزنياً ولا حركة مخزون.
/// </summary>
public partial class ProductionActualCloseView : UserControl
{
    public static int? PendingOrderId { get; set; }
    public ObservableCollection<ActualByProductDefinitionDto> ByProductDefinitions { get; } = new();
    public ObservableCollection<ActualByProductSourceDto> ByProductSources { get; } = new();

    private readonly ObservableCollection<ActualProductionRow> _items = new();
    private readonly ObservableCollection<ActualSecondaryRow> _secondary = new();
    private List<ActualByProductDefinitionDto> _activeDefinitions = new();
    private readonly Func<IProductionDeliveryService> _serviceResolver;
    private readonly Action<string> _showSavedMessage;
    private ActualDeliveryOrderDto _currentOrder;
    private bool _saving;
    private bool _orderCanBeSaved;
    private bool _searchPerformed;
    private string _lastSearchText = "";

    private static bool CanProduction(string action)
        => DatesErp.Desktop.Views.PermissionGate.Can("production", action);

    public ProductionActualCloseView() : this(null, null) { }

    /// <summary>اختيار مزود الخدمة ورسالة النجاح يسمح بفحص سلوك الواجهة على قاعدة اختبار معزولة.</summary>
    public ProductionActualCloseView(Func<IProductionDeliveryService> serviceResolver, Action<string> showSavedMessage)
    {
        _serviceResolver = serviceResolver;
        _showSavedMessage = showSavedMessage ?? (message => MessageBox.Show(message,
            "تم إقفال الإنتاج وتسجيل الفعلي", MessageBoxButton.OK, MessageBoxImage.Information));
        InitializeComponent();
        WarehouseRouteBox.ItemsSource = new List<string>
        {
            "إرسال مباشر إلى مخزن التام (تجاوز الفحص)",
            "إرسال إلى الفحص أولاً (ثم المخزن بعد الاعتماد)"
        };
        WarehouseRouteBox.SelectedIndex = -1;
        ItemsGrid.ItemsSource = _items;
        SecondaryGrid.ItemsSource = _secondary;
        _secondary.CollectionChanged += Secondary_CollectionChanged;
        _items.CollectionChanged += Items_CollectionChanged;
        ResetSessionForm();
        Loaded += (_, _) => LoadOrders(PendingOrderId);
    }

    public void AttachChrome(Views.ErpChrome chrome)
    {
        chrome.SetModule("إقفال الإنتاج وتسجيل الفعلي");
        chrome.SetScreenCode("MRPMPS1022");
        chrome.SetPermissionModule("production");
        chrome.SetToolbar(new Views.ErpToolbar()
            .WithNew((_, _) => NewSession(), "جلسة تسجيل فعلية جديدة")
            .WithSearch((_, _) => OpenOrdersSearch(OrderSearchBox.Text), "بحث في أوامر الإنتاج والتنفيذات المحفوظة")
            .WithSaveForCreateOrEdit((_, _) => Save_Click(null, null), "حفظ الفعلي وإقفال الجلسة")
            .WithUndo((_, _) => ReloadCurrent(), "تراجع عن الإدخال غير المحفوظ وإعادة تحميل السجل")
            .WithRefresh((_, _) => ReloadCurrent())
            .WithPrint((_, _) => Print(), "طباعة ملخص التنفيذ المحفوظ")
            .WithExit((_, _) => (Window.GetWindow(this) as MainWindow)?.OpenPreviousScreen()));
        chrome.SetBody(this);
        chrome.CloseRequested += (_, _) => (Window.GetWindow(this) as MainWindow)?.OpenPreviousScreen();
    }

    public static void OpenForOrder(int orderId, Window source)
    {
        PendingOrderId = orderId;
        var main = System.Windows.Application.Current.MainWindow as MainWindow;
        if (source != null && source != main) source.Close();
        main?.OpenScreen("actualclose");
    }

    private T WithService<T>(Func<IProductionDeliveryService, T> action)
    {
        if (_serviceResolver != null) return action(_serviceResolver());
        using var scope = AppContainer.NewScope();
        return action(scope.ServiceProvider.GetRequiredService<IProductionDeliveryService>());
    }

    private void LoadOrders(int? selected, bool includeCompleted = false, string searchText = null,
        bool loadActiveOrders = false, bool selectFirst = true)
    {
        try
        {
            _activeDefinitions = WithService(s => s.GetActualByProducts());
            ByProductDefinitions.Clear();
            foreach (var definition in _activeDefinitions) ByProductDefinitions.Add(definition);

            if (!selected.HasValue && !includeCompleted && !loadActiveOrders && !_searchPerformed)
            {
                OrderBox.ItemsSource = Array.Empty<ActualDeliveryOrderDto>();
                OrderBox.SelectedItem = null;
                StatusLabel.Text = "ابدأ بالبحث عن أمر إنتاج؛ لا تُعرض الأوامر قبل طلب البحث.";
                return;
            }

            var orders = WithService(s => s.GetActualDeliveryOrders(selected, includeCompleted))
                .Where(o => o.CanRecord || (includeCompleted && o.Recorded) || o.OrderId == selected)
                .Where(o => string.IsNullOrWhiteSpace(searchText)
                    || (o.Label ?? "").Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
                    || (o.OrderNumber ?? "").Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
                    || (o.Customer ?? "").Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
                    || (o.PlanNumber ?? "").Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
                    || o.Items.Any(i => (i.Product ?? "").Contains(searchText, StringComparison.CurrentCultureIgnoreCase)))
                .ToList();
            OrderBox.ItemsSource = orders;
            OrderBox.SelectedItem = selectFirst
                ? orders.FirstOrDefault(o => o.OrderId == selected)
                    ?? orders.FirstOrDefault(o => o.CanRecord)
                    ?? orders.FirstOrDefault()
                : null;
            PendingOrderId = null;
            if (orders.Count == 0)
                StatusLabel.Text = includeCompleted
                    ? "لم يعثر البحث على أمر أو تنفيذ محفوظ مطابق. غيّر عبارة البحث أو امسحها."
                    : "لا توجد أوامر متاحة. استخدم البحث أو تحقق من اعتماد الأمر وتاريخه.";
            else if (!selectFirst)
                StatusLabel.Text = includeCompleted
                    ? $"نتائج البحث: {orders.Count} أمرًا/تنفيذًا. افتح قائمة الأمر واختر السجل المطلوب؛ لن يُفتح آخر أمر تلقائيًا."
                    : $"الأوامر القابلة لتسجيل الفعلي: {orders.Count}. افتح قائمة الأمر واختر الأمر المطلوب.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "تعذر تحميل أوامر الإنتاج: " + ex.Message;
            AppContainer.Get<DialogService>().HandleException(ex, "ActualClose.Load");
        }
    }

    private void NewSession()
    {
        if (OrderBox == null) return;
        _searchPerformed = false;
        _lastSearchText = "";
        OrderSearchBox.Text = "";
        ResetSessionForm();
        LoadOrders(null, includeCompleted: false, loadActiveOrders: true, selectFirst: false);
    }

    private void ResetSessionForm()
    {
        if (OrderBox != null) OrderBox.SelectedItem = null;
        _items.Clear();
        _secondary.Clear();
        ByProductSources.Clear();
        _currentOrder = null;
        RawBox.Text = "يحسب تلقائيًا بعد إدخال الفعلي"; DowntimeBox.Text = "0"; ReasonBox.Text = ""; NotesBox.Text = "";
        StatusLabel.Text = "جلسة جديدة: اختر أمرًا من القائمة. تُقدّر كمية الخام آليًا.";
        ItemsGrid.IsReadOnly = true;
        MeasurementsPanel.IsEnabled = false;
        ByProductsPanel.IsEnabled = false;
        SaveActualButton.IsEnabled = false;
        _orderCanBeSaved = false;
        WarehouseRouteBox.SelectedIndex = -1;
        WarehouseRouteBox.IsEnabled = false;
        RefreshSummaries();
    }

    private void WarehouseRoute_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SaveActualButton != null && !_saving)
            SaveActualButton.IsEnabled = _orderCanBeSaved && (WarehouseRouteBox.SelectedIndex is 0 or 1);
    }

    private void SearchOrders_Click(object sender, RoutedEventArgs e) => OpenOrdersSearch(OrderSearchBox.Text);

    private void OpenOrdersSearch(string initialTerm = null)
    {
        try
        {
            var win = new DocSearchWindow("أوامر الإنتاج والتنفيذات المحفوظة",
                new List<SearchFieldDef> { new() { Key = "term", LabelAr = "الأمر أو العميل أو الخطة أو الصنف", DefaultValue = initialTerm } }, filters =>
                {
                    var all = WithService(s => s.GetActualDeliveryOrders(null, true)) ?? new List<ActualDeliveryOrderDto>();
                    var term = ScreenSearch.Normalize(filters.GetValueOrDefault("term") ?? "");
                    var rows = all.Where(o => o.CanRecord || o.Recorded)
                        .Where(o => string.IsNullOrEmpty(term)
                            || ScreenSearch.Normalize(o.Label).Contains(term)
                            || ScreenSearch.Normalize(o.OrderNumber).Contains(term)
                            || ScreenSearch.Normalize(o.Customer).Contains(term)
                            || ScreenSearch.Normalize(o.PlanNumber).Contains(term)
                            || ScreenSearch.Normalize(o.ProductionDate).Contains(term)
                            || (o.Items ?? new List<ActualDeliveryItemDto>()).Any(i => ScreenSearch.Normalize(i.Product).Contains(term)))
                        .ToList();
                    return new SearchResult
                    {
                        Columns = new List<string> { "رقم الأمر", "العميل", "الخطة", "تاريخ الإنتاج", "الأصناف", "الحالة" },
                        Rows = rows.Select(o => (o.OrderId, new object[] { o.OrderNumber, o.Customer, o.PlanNumber, o.ProductionDate,
                            string.Join("، ", (o.Items ?? new List<ActualDeliveryItemDto>()).Select(i => i.Product).Distinct()),
                            o.Recorded ? "تنفيذ محفوظ" : o.CanRecord ? "متاح لتسجيل الفعلي" : "غير متاح" })).ToList()
                    };
                }) { Owner = Window.GetWindow(this) };
            if (win.ShowDialog() == true && win.SelectedId is int id)
            {
                _searchPerformed = true;
                LoadOrders(id, includeCompleted: true, selectFirst: true);
            }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "ActualClose.Search"); }
    }

    private void OrderSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SearchOrders_Click(sender, e);
    }

    private void ReloadCurrent()
    {
        if (OrderBox.SelectedItem is ActualDeliveryOrderDto selected)
            LoadOrders(selected.OrderId, includeCompleted: true);
        else if (_searchPerformed)
            LoadOrders(null, includeCompleted: true, searchText: _lastSearchText, selectFirst: false);
        else
            LoadOrders(null, includeCompleted: false, loadActiveOrders: true, selectFirst: false);
    }

    private void Order_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ItemsGrid == null) return;
        var order = OrderBox.SelectedItem as ActualDeliveryOrderDto;
        _currentOrder = order;
        WarehouseRouteBox.SelectedIndex = -1;
        _items.Clear();
        _secondary.Clear();
        ByProductSources.Clear();
        foreach (var source in order?.ByProductSources ?? new List<ActualByProductSourceDto>()) ByProductSources.Add(source);
        RawBox.Text = "يحسبه النظام آليًا";
        DowntimeBox.Text = "0";
        ReasonBox.Text = "";
        NotesBox.Text = "";
        ByProductDefinitions.Clear();
        foreach (var definition in _activeDefinitions) ByProductDefinitions.Add(definition);

        bool editable = order?.CanRecord == true && CanProduction("Create") && PermissionGate.Can("execution", "Edit");
        _orderCanBeSaved = editable;
        ItemsGrid.IsReadOnly = !editable;
        MeasurementsPanel.IsEnabled = editable;
        ByProductsPanel.IsEnabled = editable;
        SaveActualButton.IsEnabled = editable && (WarehouseRouteBox.SelectedIndex is 0 or 1);
        WarehouseRouteBox.IsEnabled = editable;
        if (!editable) WarehouseRouteBox.SelectedIndex = -1;

        CustChip.Text = $"👤 العميل: {order?.Customer ?? "—"}";
        PlanChip.Text = $"📋 الخطة: {order?.PlanNumber ?? "—"}";
        ShiftChip.Text = $"⏰ الوردية: {order?.Shift ?? "—"}";
        DateChip.Text = $"📅 التاريخ: {GetProductionDate(order) ?? "—"}";
        StatusLabel.Text = order?.Status ?? "اختر أمراً من الأعلى.";

        if (order == null) return;
        bool continuation = order.Recorded && order.CanRecord;
        bool historical = order.Recorded && !order.CanRecord;
        ItemsGrid.Columns[4].Header = continuation ? "فعلي الجلسة — اكتب في الأصفر"
            : historical ? "الإنتاج الفعلي التراكمي" : "الفعلي — اكتب في الأصفر";
        foreach (var line in order.Items)
        {
            var row = new ActualProductionRow(line, historical, continuation);
            row.PropertyChanged += ActualRow_PropertyChanged;
            _items.Add(row);
        }

        if (order.Recorded && !continuation)
        {
            RawBox.Text = order.ConsumedRawKg.ToString(CultureInfo.CurrentCulture);
            DowntimeBox.Text = order.DowntimeHours.ToString(CultureInfo.CurrentCulture);
            ReasonBox.Text = order.DowntimeReason ?? "";
            NotesBox.Text = order.Notes ?? "";
            foreach (var byProduct in order.ByProducts)
            {
                _secondary.Add(new ActualSecondaryRow
                {
                    Definition = ByProductDefinitions.FirstOrDefault(x => x.Id == byProduct.ByProductId)
                        ?? new ActualByProductDefinitionDto { Id = byProduct.ByProductId, Name = $"مخرج محفوظ #{byProduct.ByProductId}", Unit = "كجم" },
                    Source = ByProductSources.FirstOrDefault(x => x.OrderItemId == byProduct.SourceOrderItemId
                        && x.RawProductId == byProduct.RawProductId && x.RawLotId == byProduct.RawLotId && x.CustomerId == byProduct.CustomerId),
                    Quantity = byProduct.QtyKg.ToString(CultureInfo.CurrentCulture)
                });
            }
            StatusLabel.Text += $" — آخر فحص: {order.QualityNumber ?? "بانتظار الفحص"}";
        }
        else
        {
            AddSecondaryRow();
            if (continuation)
            {
                RawBox.Text = "سيحسبه النظام آليًا";
                DowntimeBox.Text = "0";
                ReasonBox.Text = "";
                NotesBox.Text = "";
                double remainingKg = Math.Max(0, order.PlannedQtyKg - order.ActualQtyKg);
                StatusLabel.Text += $" — سبق تسجيل {order.ActualQtyKg:N1} كجم؛ المتبقي المخطط {remainingKg:N1} كجم. أدخل كميات هذه الجلسة فقط.";
            }
            else if (!editable)
                StatusLabel.Text += " — التسجيل غير متاح للصلاحية الحالية أو لحالة الأمر.";
        }
    }

    private static string GetProductionDate(ActualDeliveryOrderDto order)
        => order?.ProductionDate;

    private void AddByProduct_Click(object sender, RoutedEventArgs e)
        => AddSecondaryRow();

    private void AddSecondaryRow()
    {
        _secondary.Add(new ActualSecondaryRow());
    }

    private void RemoveByProduct_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ActualSecondaryRow row)
            _secondary.Remove(row);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_saving) return;
            if (OrderBox.SelectedItem is not ActualDeliveryOrderDto order)
            {
                StatusLabel.Text = "اختر أمر الإنتاج أولاً.";
                return;
            }
            if (!order.CanRecord)
            {
                StatusLabel.Text = "لا يمكن تسجيل الفعلي: " + (order.Status ?? "حالة الأمر لا تسمح بالإقفال.");
                return;
            }
            if (!CanProduction("Create") || !PermissionGate.Can("execution", "Edit"))
            {
                StatusLabel.Text = "لا يمكن الحفظ: يلزم صلاحية إنشاء الإنتاج وتعديل التنفيذ.";
                return;
            }

            ItemsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            ItemsGrid.CommitEdit(DataGridEditingUnit.Row, true);

            if (_items.Count == 0 || _items.Any(i => !i.TryQuantity(out _)))
                throw new ArgumentException("أدخل الفعلي لكل بند، والصفر يُدخل صراحةً.");
            if (!ActualProductionRow.TryNonnegative(DowntimeBox.Text, out var hours))
                throw new ArgumentException("أدخل ساعات توقف صحيحة غير سالبة.");
            if (WarehouseRouteBox.SelectedIndex is not (0 or 1))
                throw new ArgumentException("اختر مسار الإقفال قبل الحفظ: استلام مباشر في مخزن التام أو إرسال إلى الفحص أولاً.");

            var input = new ActualProductionDto
            {
                OrderId = order.OrderId,
                WarehouseRoute = WarehouseRouteBox.SelectedIndex == 0 ? ProductionWarehouseRoutes.Direct : ProductionWarehouseRoutes.Quality,
                ConsumedRawKg = 0, // يترك حساب الخام الفعلي لخدمة الإقفال بناءً على التخصيصات ومخرجات الجلسة.
                DowntimeHours = hours,
                DowntimeReason = ReasonBox.Text,
                Notes = NotesBox.Text
            };
            foreach (var row in _items)
            {
                row.TryQuantity(out var quantity);
                input.Items.Add(new ActualProductionItemDto { OrderItemId = row.Source.OrderItemId, ActualCartons = quantity });
            }
            foreach (var row in _secondary)
            {
                if (row.Definition == null && string.IsNullOrWhiteSpace(row.Quantity)) continue;
                if (row.Definition == null || !ActualProductionRow.TryNonnegative(row.Quantity, out var quantity) || quantity <= 0)
                    throw new ArgumentException("اختر المخرج الثانوي وأدخل كميته الموجبة، أو احذف السطر غير المستخدم.");
                if (row.Source == null)
                    throw new ArgumentException("اختر مصدر الخام والعميل والدفعة لكل مخرج ثانوي.");
                input.ByProducts.Add(new ByProductQtyDto
                {
                    ByProductId = row.Definition.Id, QtyKg = quantity,
                    SourceOrderItemId = row.Source.OrderItemId, RawProductId = row.Source.RawProductId,
                    RawLotId = row.Source.RawLotId, CustomerId = row.Source.CustomerId
                });
            }

            _saving = true;
            var result = WithService(s => s.SaveActualProduction(input));
            if (!result.Ok)
            {
                StatusLabel.Text = result.Message;
                return;
            }
            string successMessage = result.Message + "\n\nإذا بقي إنتاج مخطط، ابحث عن الأمر نفسه لاستكمال جلسة جديدة. لا تنفذ هذه الشاشة أي حركة مخزنية؛ أمين مخزن التام هو من يحفظ الاستلام ويرحّله.";
            _showSavedMessage(successMessage);
            _searchPerformed = false;
            _lastSearchText = "";
            OrderSearchBox.Text = "";
            LoadOrders(null, includeCompleted: false);
            NewSession();
            StatusLabel.Text = successMessage;
        }
        catch (ArgumentException ex)
        {
            StatusLabel.Text = ex.Message;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "ActualClose.Save: " + ex.Message;
            AppContainer.Get<DialogService>().HandleException(ex, "ActualClose.Save");
        }
        finally
        {
            _saving = false;
        }
    }

    private void Print()
    {
        try
        {
            if (OrderBox.SelectedItem is not ActualDeliveryOrderDto { Recorded: true, OrderId: > 0 } order)
            {
                StatusLabel.Text = "اختر تنفيذًا محفوظًا قبل الطباعة.";
                return;
            }
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErp.Infrastructure.Persistence.DatesErpDbContext>();
            var model = Printing.StoredPrintModels.Execution(db, order.OrderId);
            new PrintPreviewWindow(PhasePrint.Build(model), $"{model.DocTitle} {model.DocNo}")
                { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "تعذرت الطباعة: " + ex.Message;
            AppContainer.Get<DialogService>().HandleException(ex, "ActualClose.Print");
        }
    }

    private void Secondary_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (ActualSecondaryRow row in e.OldItems) row.PropertyChanged -= SecondaryRow_PropertyChanged;
        if (e.NewItems != null) foreach (ActualSecondaryRow row in e.NewItems) row.PropertyChanged += SecondaryRow_PropertyChanged;
        RefreshSummaries();
    }

    private void Items_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e) => RefreshSummaries();
    private void ActualRow_PropertyChanged(object sender, PropertyChangedEventArgs e) => RefreshSummaries();
    private void SecondaryRow_PropertyChanged(object sender, PropertyChangedEventArgs e) => RefreshSummaries();

    private static bool IsKilogramUnit(string unit)
        => (unit ?? "").Trim().ToLowerInvariant() is "كجم" or "كغ" or "kg" or "كيلوجرام";

    private void RefreshSummaries()
    {
        if (SecondaryTotalsLabel == null) return;
        double secondaryTotal = _secondary
            .Where(r => r.Definition != null && IsKilogramUnit(r.Definition.Unit))
            .Select(r => ActualProductionRow.TryNonnegative(r.Quantity, out var q) ? q : 0).Sum();
        SecondaryTotalsLabel.Text = $"إجمالي المخرجات الثانوية: {secondaryTotal:N2} كجم";
        if (ActualSummaryLabel != null)
        {
            int entered = _items.Count(r => r.TryQuantity(out _));
            int cartons = _items.Where(r => r.TryQuantity(out _)).Sum(r => { r.TryQuantity(out var q); return q; });
            int planned = _items.Sum(r => r.RemainingBefore + r.PreviouslyProduced);
            ActualSummaryLabel.Text = $"بنود مكتملة الإدخال: {entered}/{_items.Count} — الفعلي المُدخل: {cartons:N0} / {planned:N0} كرتون";
        }

        if (_currentOrder == null)
        {
            RawBox.Text = "يحسب تلقائيًا بعد إدخال الفعلي";
            RawEstimateHint.Text = "لا يلزم إدخال الخام يدويًا.";
            return;
        }
        if (_currentOrder.Recorded && !_currentOrder.CanRecord)
        {
            RawEstimateHint.Text = "كمية الخام الفعلية المحفوظة لهذه التنفيذات.";
            return;
        }

        double plannedTotalKg = _items.Sum(r => Math.Max(0, r.Source.PlannedQtyKg));
        double rawEstimateKg = 0;
        foreach (var row in _items)
        {
            if (!row.TryQuantity(out var cartons) || row.Source.PlannedCartons <= 0 || row.Source.PlannedQtyKg <= 0) continue;
            double linePlannedKg = row.Source.PlannedQtyKg;
            double actualKg = cartons * (linePlannedKg / row.Source.PlannedCartons);
            double byProductShare = plannedTotalKg > 0.001
                ? secondaryTotal * (linePlannedKg / plannedTotalKg) : 0;
            double rawBasis = row.RawMaterialBasisKg > 0 ? row.RawMaterialBasisKg : linePlannedKg;
            rawEstimateKg += rawBasis * Math.Min(1.0, Math.Max(0, (actualKg + byProductShare) / linePlannedKg));
        }
        RawBox.Text = rawEstimateKg.ToString("N2", CultureInfo.CurrentCulture);
        bool allEntered = _items.Count > 0 && _items.All(r => r.TryQuantity(out _));
        RawEstimateHint.Text = allEntered
            ? "تقدير تلقائي مكتمل من تخصيص الخام وكميات الإنتاج والمخرجات."
            : "تقدير مبدئي؛ يكتمل تلقائيًا بعد إدخال الفعلي لكل بند.";
    }
}
