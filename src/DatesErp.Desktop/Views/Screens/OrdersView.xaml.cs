using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DatesErp.Core.Common;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>§v1.50.36 — شاشة واحدة: بنود اليوم + مستند الأمر أسفلها. لا تبديل شاشات.</summary>
public partial class OrdersView : UserControl
{
    private TodayProductionDto _sheet;
    private readonly Func<TodayProductionDto> _load;
    private readonly Func<OpResult> _issue;
    private readonly bool _isolated;
    private OrderDocumentPanel _panel;
    private int? _shownOrderId;
    private TodayPendingGroupDto _selectedGroup;
    private bool _hasUnsavedPlanPreview;
    private bool _syncing;
    private Views.ErpToolbar _toolbar;
    private Button _activeToolbarButton;
    private object _activeOriginalContent;
    private System.Windows.Media.Brush _activeOriginalBorder;
    private Thickness _activeOriginalBorderThickness;

    public OrdersView() : this(null, null) { }

    public OrdersView(Func<TodayProductionDto> load, Func<OpResult> issue)
    {
        InitializeComponent();
        _isolated = load != null;
        // شاشة أوامر الإنتاج تعرض كل الخطط المعتمدة المجدولة؛ إصدار «اليوم» له زر مستقل وحارس مستقل.
        _load = load ?? (() => { using var scope = AppContainer.NewScope(); return scope.ServiceProvider.GetRequiredService<IProductionOrderService>().GetScheduledProduction(); });
        _issue = issue ?? (() => { using var scope = AppContainer.NewScope(); return scope.ServiceProvider.GetRequiredService<IProductionOrderService>().IssueTodayOrders(); });
        Loaded += (_, _) =>
        {
            ResetWorkspace();
            if (!_isolated && MainWindow.PendingOrderIdToOpen is int id)
            {
                MainWindow.PendingOrderIdToOpen = null;
                ShowOrderInPlace(id);
            }
        };
        Unloaded += (_, _) => { };
    }

    public void AttachChrome(Views.ErpChrome chrome)
    {
        chrome.SetModule("أمر الإنتاج — شاشة واحدة: الخطط المجدولة + المستند");
        // عنوان الشاشة للعرض فقط؛ بوابة الأزرار والصلاحيات تستخدم كود الوحدة الثابت.
        chrome.SetPermissionModule("production");
        chrome.SetScreenCode("MRPMPS1007");
        _toolbar = new Views.ErpToolbar()
            .WithNew((_, _) => AddFromPlan_Click(null, null), "➕ إضافة أمر")
            .WithEdit((_, _) => Edit_Click(null, null))
            .WithSaveForCreateOrEdit((_, _) => Save_Click(null, null), "💾 حفظ الأمر")
            .WithSearch((_, _) => Search_Click(null, null), "بحث عن أمر إنتاج محفوظ")
            .WithDelete((_, _) => Delete_Click(null, null))
            .WithRefresh((_, _) => RefreshClicked())
            .WithPrint((_, _) => Print_Click(null, null))
            .WithExit((_, _) => (Window.GetWindow(this) as MainWindow)?.OpenPreviousScreen());
        chrome.SetToolbar(_toolbar);
        UpdateToolbarState();
        chrome.SetBody(this);
        chrome.CloseRequested += (_, _) => (Window.GetWindow(this) as MainWindow)?.OpenPreviousScreen();
    }

    private void ResetWorkspace()
    {
        _sheet = null;
        _selectedGroup = null;
        _hasUnsavedPlanPreview = false;
        _shownOrderId = null;
        TodayGrid.ItemsSource = null;
        GridEmptyHint.Visibility = Visibility.Visible;
        DayLabel.Text = $"اليوم: {DateTime.Today:dd/MM/yyyy} — تاريخ العمل من الخادم";
        DayHint.Text = "ابدأ بزر «إضافة أمر»؛ ستظهر الخطط المعتمدة غير الصادرة فقط.";
        MarkToolbarAction(null, "ابدأ بإضافة أمر من خطة معتمدة.");
        DayChip.Text = $"📅 اليوم: {DateTime.Today:dd/MM/yyyy}";
        ItemsChip.Text = "📦 بنود المجموعة: 0";
        IssuedChip.Text = "🗂 الأمر: غير محفوظ";
        PendingChip.Text = "⏳ الخطوة: اختيار خطة";
        DayClosedChip.Text = "✔ إصدار واحد لكل مجموعة";
        TotCartonsBox.Text = "الكراتين: 0";
        TotQtyBox.Text = "الوزن: 0 كجم";
        TotCustsBox.Text = "عملاء: 0";
        TotLinesBox.Text = "خطوط: 0";
        ClearDetails();
        DetailsHint.Text = "لا يوجد أمر مفتوح. اضغط «إضافة أمر» لاختيار خطة، ثم راجع تفاصيلها وعملاءها واحفظ الأمر.";
        UpdateToolbarState();
    }

    private void RefreshClicked()
    {
        if (_panel?.IsEditing == true && !_isolated
            && !AppContainer.Get<DialogService>().Confirm("توجد تعديلات غير محفوظة على الأمر الحالي. هل تريد تحديث البيانات والتخلّي عنها؟")) return;
        MarkToolbarAction(_toolbar?.RefreshBtn, "تحديث بيانات المجموعة أو المستند الحالي.");
        RefreshToday();
    }

    private void RefreshToday()
    {
        if (_selectedGroup == null)
        {
            if (_panel != null) _panel.InvokeRefresh();
            else ResetWorkspace();
            return;
        }

        try
        {
            var next = _load();
            var rows = next.Rows.Where(r => r.PlanId == _selectedGroup.PlanId
                && r.ScheduledDate == _selectedGroup.ScheduledDate
                && r.ShiftId == _selectedGroup.ShiftId && r.LineId == _selectedGroup.LineId).ToList();
            _sheet = new TodayProductionDto
            {
                Day = next.Day, Message = next.Message, Rows = rows,
                CanIssue = rows.Any(r => r.IsPending)
            };
            _syncing = true;
            TodayGrid.ItemsSource = rows;
            TodayGrid.SelectedItem = _hasUnsavedPlanPreview ? rows.FirstOrDefault() : null;
            _syncing = false;
            GridEmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            GridEmptyHint.Text = rows.Count == 0
                ? "لم تعد للمجموعة بنود متاحة. حدّث القائمة أو اختر مجموعة أخرى."
                : _hasUnsavedPlanPreview
                    ? "هذه معاينة فقط — راجع العملاء والكميات، ثم اضغط «حفظ الأمر» لإصداره."
                    : "المجموعة صادرة — يظهر رقم الأمر وحالته هنا؛ لن تظهر مجدداً ضمن الخطط المتاحة للإصدار.";
            DayHint.Text = _hasUnsavedPlanPreview
                ? $"معاينة غير محفوظة: الخطة {_selectedGroup.PlanNumber} — {_selectedGroup.Customers}. لم يُنشأ أمر بعد."
                : $"تم حفظ أمر المجموعة للخطة {_selectedGroup.PlanNumber}؛ البنود صدرت بكميات الخطة الأصلية.";
            WorkflowStateText.Text = _hasUnsavedPlanPreview
                ? "الخطوة الحالية: راجع تفاصيل المجموعة ثم اضغط «حفظ الأمر»."
                : "الخطوة الحالية: استعراض الأمر المحفوظ — تم منع إعادة إصدار المجموعة نفسها.";
            DayChip.Text = $"📅 التاريخ المجدول: {_selectedGroup.ScheduledDate}";
            ItemsChip.Text = $"📦 البنود: {rows.Count}";
            IssuedChip.Text = $"🗂 الأمر: {(rows.Any(r => r.OrderId != null) ? rows.First(r => r.OrderId != null).OrderNumber : "معاينة غير محفوظة")}";
            PendingChip.Text = _hasUnsavedPlanPreview ? "⏳ بانتظار الحفظ" : "✔ صُدرت المجموعة";
            TotCartonsBox.Text = $"الكراتين: {rows.Sum(r => r.PlannedCartons):N0}";
            TotQtyBox.Text = $"الوزن: {rows.Sum(r => r.PlannedKg):N1} كجم";
            TotCustsBox.Text = $"عملاء: {rows.Select(r => r.CustomerId).Distinct().Count()}";
            TotLinesBox.Text = $"الخطوط: {rows.Select(r => r.LineId).Distinct().Count()}";
            if (_hasUnsavedPlanPreview && TodayGrid.SelectedItem is TodayProductionRowDto previewRow)
                ShowPendingRowDetails(previewRow);
            if (!_hasUnsavedPlanPreview && rows.FirstOrDefault(r => r.OrderId != null)?.OrderId is int id)
                ShowOrderInPlace(id);
            UpdateToolbarState();
        }
        catch (Exception ex)
        {
            _sheet = null;
            TodayGrid.ItemsSource = null;
            GridEmptyHint.Visibility = Visibility.Visible;
            GridEmptyHint.Text = "تعذر تحديث المجموعة المحددة؛ لم يُصدر أي أمر. حاول التحديث أو أعد اختيار الخطة.";
            WorkflowStateText.Text = "تعذر تحميل البيانات — راجع الاتصال ثم حدّث الشاشة.";
            if (!_isolated) AppContainer.Get<DialogService>().HandleException(ex, "Orders.RefreshSelectedGroup");
            else throw;
        }
    }

    private void PreviewSelectedGroup(TodayPendingGroupDto group)
    {
        if (group == null) return;
        _panel = null;
        _shownOrderId = null;
        DetailsHost.Content = null;
        DetailsHint.Text = $"معاينة المجموعة: الخطة {group.PlanNumber} — {group.PlanTitle} | العملاء: {group.Customers} | "
            + $"التاريخ {group.ScheduledDate} | {group.ItemsCount} بنداً | {group.Cartons:N0} كرتون / {group.PlannedKg:N1} كجم. "
            + "راجع البنود أعلاه ثم اضغط «حفظ الأمر».";
        DetailsHint.Visibility = Visibility.Visible;
        _selectedGroup = group;
        _hasUnsavedPlanPreview = true;
        MarkToolbarAction(_toolbar?.NewBtn, $"اخترت الخطة {group.PlanNumber}؛ راجع معاينتها ثم احفظ الأمر.");
        RefreshToday();
    }

    private void SaveSelectedGroup()
    {
        if (!_hasUnsavedPlanPreview || _selectedGroup == null) return;
        if (!_isolated && !AppContainer.Get<DialogService>().Confirm(
            $"حفظ/إصدار أمر إنتاج من الخطة {_selectedGroup.PlanNumber}؟\n"
            + $"التاريخ: {_selectedGroup.ScheduledDate}\nالعملاء: {_selectedGroup.Customers}\n"
            + $"البنود: {_selectedGroup.ItemsCount} — {_selectedGroup.Cartons:N0} كرتون / {_selectedGroup.PlannedKg:N1} كجم\n"
            + "لن تظهر هذه المجموعة مرة أخرى بعد الإصدار.")) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var result = scope.ServiceProvider.GetRequiredService<IProductionOrderService>()
                .IssuePlanMultiCustomerGroup(_selectedGroup.PlanId, _selectedGroup.ScheduledDate,
                    _selectedGroup.ShiftId, _selectedGroup.LineId);
            if (!result.Ok)
            {
                if (!_isolated) AppContainer.Get<DialogService>().Error(result.Message);
                WorkflowStateText.Text = "لم يُحفظ الأمر: " + result.Message;
                return;
            }
            _hasUnsavedPlanPreview = false;
            MarkToolbarAction(_toolbar?.SaveBtn, $"تم حفظ الأمر {result.DocumentNumber}; المجموعة لن تُصدر مرة أخرى.");
            if (!_isolated) AppContainer.Get<DialogService>().Info(result.Message);
            RefreshToday();
            ShowOrderInPlace(result.Id);
        }
        catch (Exception ex)
        {
            if (!_isolated) AppContainer.Get<DialogService>().HandleException(ex, "Orders.SaveFromPlan");
            else throw;
        }
    }

    private void MarkToolbarAction(Button button, string stateText)
    {
        if (_activeToolbarButton != null)
        {
            _activeToolbarButton.Content = _activeOriginalContent;
            _activeToolbarButton.BorderBrush = _activeOriginalBorder;
            _activeToolbarButton.BorderThickness = _activeOriginalBorderThickness;
        }
        _activeToolbarButton = null;
        if (button != null)
        {
            _activeToolbarButton = button;
            _activeOriginalContent = button.Content;
            _activeOriginalBorder = button.BorderBrush;
            _activeOriginalBorderThickness = button.BorderThickness;
            button.Content = "▶ " + button.Content;
            button.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xB0, 0x25));
            button.BorderThickness = new Thickness(2.2);
        }
        if (WorkflowStateText != null) WorkflowStateText.Text = "الخطوة الحالية: " + stateText;
    }

    private void UpdateToolbarState()
    {
        if (_toolbar == null) return;
        bool canCreate = _isolated || DatesErp.Desktop.Views.PermissionGate.Can("production", "Create");
        bool canEdit = _isolated || DatesErp.Desktop.Views.PermissionGate.Can("production", "Edit");
        bool canDelete = _isolated || DatesErp.Desktop.Views.PermissionGate.Can("production", "Delete");
        _toolbar.NewBtn.IsEnabled = canCreate;
        _toolbar.SaveBtn.IsEnabled = canCreate && _hasUnsavedPlanPreview
            || (_panel?.IsEditing == true && canEdit);
        _toolbar.EditBtn.IsEnabled = canEdit && _panel?.CanEdit == true;
        _toolbar.DeleteBtn.IsEnabled = canDelete && _panel?.CanDelete == true;
        _toolbar.PrintBtn.IsEnabled = _panel != null;
        _toolbar.SearchBtn.IsEnabled = true;
        _toolbar.RefreshBtn.IsEnabled = true;
    }

    private void IssueToday_Click(object sender, RoutedEventArgs e)
    {
        if (_sheet?.CanIssue != true) return;
        if (!_isolated && !AppContainer.Get<DialogService>().Confirm("إصدار أوامر جميع بنود اليوم المعتمدة كما هي؟\nلا تغيير للأصناف أو الكميات، ولا صرف مخزون بمجرد الإصدار.")) return;
        IssueTodayBtn.IsEnabled = false;
        try
        {
            var result = _issue();
            if (!_isolated)
            {
                if (result.Ok) AppContainer.Get<DialogService>().Info(result.Message);
                else AppContainer.Get<DialogService>().Error(result.Message);
            }
            RefreshToday();
            if (!result.Ok) DayHint.Text = result.Message;
        }
        catch (Exception ex)
        {
            RefreshToday();
            if (!_isolated) AppContainer.Get<DialogService>().HandleException(ex, "Orders.IssueToday");
            else throw;
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_sheet?.Rows == null) return;
        bool allSelected = _sheet.Rows.Where(r => r.IsPending).All(r => r.IsSelected);
        foreach (var r in _sheet.Rows.Where(r => r.IsPending)) r.IsSelected = !allSelected;
    }

    private void IssueSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_sheet?.Rows == null) return;
        var sel = _sheet.Rows.Where(r => r.IsPending && r.IsSelected).ToList();
        // السماح بالنقر على الصف ثم الإصدار مباشرة، مع إبقاء مربع ✓ للتحديد المتعدد.
        if (sel.Count == 0 && TodayGrid.SelectedItem is TodayProductionRowDto current && current.IsPending)
            sel.Add(current);
        if (sel.Count == 0) { AppContainer.Get<DialogService>().Info("حدد صفاً معلقاً أو ضع علامة ✓ على بند واحد على الأقل."); return; }
        // المجموعة التشغيلية الواحدة قد تحتوي عملاء متعددين؛ العميل محفوظ في كل بند،
        // ولا نقسم الأمر آلياً حسب CustomerId.
        var groups = sel.GroupBy(r => new { r.PlanId, r.ScheduledDate, r.ShiftId, r.LineId }).ToList();
        if (!_isolated && !AppContainer.Get<DialogService>().Confirm($"إصدار {groups.Count} أمر إنتاج متعدد العملاء من البنود المحددة بكميات الخطة الأصلية؟")) return;
        try
        {
            int issued = 0;
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
            foreach (var g in groups)
            {
                var res = svc.IssuePlanMultiCustomerGroup(g.Key.PlanId, g.Key.ScheduledDate, g.Key.ShiftId, g.Key.LineId);
                if (!res.Ok) { AppContainer.Get<DialogService>().Error(res.Message); return; }
                issued++;
            }
            AppContainer.Get<DialogService>().Info($"تم إصدار {issued} أمر إنتاج متعدد العملاء بكميات الخطة الأصلية.");
            RefreshToday();
        }
        catch (Exception ex)
        {
            if (!_isolated) AppContainer.Get<DialogService>().HandleException(ex, "Orders.IssueSelected");
            else throw;
        }
    }

    private void ApproveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_sheet?.Rows == null) return;
        var selected = _sheet.Rows
            .Where(r => r.OrderId != null && r.Status == DocStatuses.ToArabic(DocStatuses.Draft) && r.IsSelected)
            .ToList();
        if (selected.Count == 0 && TodayGrid.SelectedItem is TodayProductionRowDto current
            && current.OrderId != null && current.Status == DocStatuses.ToArabic(DocStatuses.Draft))
            selected.Add(current);
        var orderIds = selected.Where(r => r.OrderId != null).Select(r => r.OrderId!.Value).Distinct().ToList();
        if (orderIds.Count == 0)
        {
            AppContainer.Get<DialogService>().Info("حدد أمراً في حالة مسودة أولاً، ثم اضغط «اعتماد الأمر».");
            return;
        }
        if (!_isolated && !AppContainer.Get<DialogService>().Confirm($"اعتماد {orderIds.Count} أمر إنتاج؟ سيُعاد فحص الخطة والطاقة والعبوة والدفعات قبل الانتقال للمرحلة التالية.")) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
            foreach (var id in orderIds)
            {
                var result = svc.ApproveOrder(id);
                if (!result.Ok) { AppContainer.Get<DialogService>().Error(result.Message); return; }
            }
            AppContainer.Get<DialogService>().Info($"تم اعتماد {orderIds.Count} أمر إنتاج والانتقال إلى حالة الجدولة.");
            RefreshToday();
        }
        catch (Exception ex)
        {
            if (!_isolated) AppContainer.Get<DialogService>().HandleException(ex, "Orders.ApproveSelected");
            else throw;
        }
    }

    /// <summary>§v1.50.36 — يعرض المستند أسفل الجدول دون إخفاء قائمة اليوم.</summary>
    private void ShowOrderInPlace(int id)
    {
        if (_isolated) return;
        if (_shownOrderId == id && _panel != null) return;
        DetailsHost.Content = _panel = new OrderDocumentPanel(id)
        {
            Navigator = ShowOrderInPlace,
            Changed = RefreshToday,
            ReturnToList = ResetWorkspace
        };
        _shownOrderId = id;
        DetailsHint.Visibility = Visibility.Collapsed;
        WorkflowStateText.Text = _selectedGroup == null
            ? "الخطوة الحالية: استعراض أمر محفوظ من البحث."
            : "الخطوة الحالية: الأمر محفوظ؛ بيانات الخطة والعملاء محفوظة معه.";
        UpdateToolbarState();
    }

    private void ClearDetails()
    {
        _panel = null; _shownOrderId = null;
        DetailsHost.Content = null;
        DetailsHint.Visibility = Visibility.Visible;
        UpdateToolbarState();
    }

    private void AddFromPlan_Click(object sender, RoutedEventArgs e)
    {
        if (_panel?.IsEditing == true && !_isolated
            && !AppContainer.Get<DialogService>().Confirm("توجد تعديلات غير محفوظة على الأمر الحالي. هل تريد إلغاءها والبدء بأمر جديد؟")) return;
        if (_hasUnsavedPlanPreview && !_isolated
            && !AppContainer.Get<DialogService>().Confirm("توجد معاينة أمر غير محفوظة. هل تريد استبدالها واختيار خطة أخرى؟")) return;
        MarkToolbarAction(_toolbar?.NewBtn, "اختر مجموعة خطة معتمدة لم تُصدر بعد.");
        OrderDocumentPanel.PromptAddFromPlan(Window.GetWindow(this), PreviewSelectedGroup);
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        if (_panel?.IsEditing == true && !_isolated
            && !AppContainer.Get<DialogService>().Confirm("توجد تعديلات غير محفوظة على الأمر الحالي. هل تريد إلغاءها والمتابعة إلى البحث؟")) return;
        if (_hasUnsavedPlanPreview && !_isolated
            && !AppContainer.Get<DialogService>().Confirm("ستُلغى معاينة الخطة غير المحفوظة عند فتح البحث. هل تتابع؟")) return;
        _selectedGroup = null;
        _hasUnsavedPlanPreview = false;
        TodayGrid.ItemsSource = null;
        GridEmptyHint.Visibility = Visibility.Visible;
        GridEmptyHint.Text = "اختر أمراً محفوظاً من نافذة البحث لعرض تفاصيله.";
        ClearDetails();
        MarkToolbarAction(_toolbar?.SearchBtn, "ابحث عن أمر محفوظ وافتحه للاستعراض أو التعديل المسموح.");
        UpdateToolbarState();
        OrderDocumentPanel.PromptSearch(Window.GetWindow(this), ShowOrderInPlace);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_hasUnsavedPlanPreview) { SaveSelectedGroup(); return; }
        if (_panel == null) { AppContainer.Get<DialogService>().Info("اضغط «إضافة أمر» واختر خطة، أو ابحث عن أمر محفوظ أولاً."); return; }
        MarkToolbarAction(_toolbar?.SaveBtn, "احفظ التعديل المفعّل على الأمر الحالي.");
        _panel.InvokeSave();
        UpdateToolbarState();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_panel == null) { AppContainer.Get<DialogService>().Info("اختر أمراً محفوظاً من البحث أولاً."); return; }
        MarkToolbarAction(_toolbar?.EditBtn, "وضع التعديل مفعّل للأمر الحالي.");
        _panel.InvokeEdit();
        UpdateToolbarState();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_panel == null) { AppContainer.Get<DialogService>().Info("اختر مسودة أمر محفوظة أولاً."); return; }
        if (_panel.IsEditing) { AppContainer.Get<DialogService>().Info("احفظ التعديلات أو ألغِها قبل حذف الأمر."); return; }
        MarkToolbarAction(_toolbar?.DeleteBtn, "حذف مسودة الأمر المحددة.");
        _panel.InvokeDelete();
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        if (_panel == null) { AppContainer.Get<DialogService>().Info("اختر أمراً محفوظاً أولاً."); return; }
        MarkToolbarAction(_toolbar?.PrintBtn, "طباعة الأمر المفتوح.");
        _panel.InvokePrint();
    }

    private void FollowExecution_Click(object sender, RoutedEventArgs e)
    {
        // شاشة أمر الإنتاج للاستعراض فقط بعد الإصدار؛ لا اعتماد تنفيذي أو تسليم/استلام مخزني هنا.
        if (TodayGrid.SelectedItem is TodayProductionRowDto { OrderId: int orderId })
            ShowOrderInPlace(orderId);
        else
            AppContainer.Get<DialogService>().Info("حدد أمراً محفوظاً لعرض مستنده هنا. إجراءات التنفيذ والتسليم ليست ضمن هذه الشاشة.");
    }

    private void TodayGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_isolated) { TodayGrid_SelectionChanged(sender, null); return; }
        if ((TodayGrid.SelectedItem as TodayProductionRowDto)?.OrderId != null)
            FollowExecution_Click(sender, e);
        else
            TodayGrid_SelectionChanged(sender, null);
    }
    private void TodayGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if (TodayGrid.SelectedItem is not TodayProductionRowDto row) return;
        if (row.OrderId is int id) ShowOrderInPlace(id);
        else ShowPendingRowDetails(row);
    }

    private void ShowPendingRowDetails(TodayProductionRowDto row)
    {
        if (row == null) return;
        DetailsHost.Content = null;
        DetailsHint.Visibility = Visibility.Visible;
        DetailsHint.Text = $"تفاصيل البند المحدد — الخطة {row.PlanNumber} ({row.PlanTitle}) | العميل: {row.CustomerName} | "
            + $"الخامة: {row.RawName} | الدفعة: {row.LotCode} | الشحنة: {row.ShipmentNo} | المخزن: {row.WarehouseName} | "
            + $"المنتج: {row.ProductName} | العبوة: {row.PackName} | {row.PlannedCartons:N0} كرتون / {row.PlannedKg:N1} كجم | "
            + $"التاريخ: {row.ScheduledDate} | الوردية: {row.ShiftName} | الخط: {row.LineName}. "
            + "هذه معاينة فقط؛ لا يصدر الأمر عند تحديد الصف. اضغط «حفظ الأمر» بعد مراجعة المجموعة.";
    }
}
