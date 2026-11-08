using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using DatesErp.Application.Services;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Connection;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// §إصلاح: يُبلّغ عن التغيير — Renumber() يُعيد ترقيم الصفوف بعد كل تعديل في المجموعة،
/// وبلا إشعار كانت خلية «م» تعرض أرقاماً قديمة وتصبح وسوم أزرار الحذف خاطئة.
/// </summary>
/// <summary>§B58: خيار قائمة (وردية/خط/عبوة) لخلايا الجدولEditable.</summary>
public class OptUi { public int Id { get; set; } public string Name { get; set; } }

/// <summary>
/// شاشة إعداد واعتماد خطط الإنتاج (MPS) — مطابقة للنموذج المعتمد:
/// مسار المعاملة (إعداد ← اعتماد المدير العام ← أوامر التشغيل ← الإقفال)،
/// نطاق التخطيط (عدة عملاء/عميل محدد)، أزرار الإدراج، شريط طاقة الوردية،
/// خطة اليوم وحالة الأيام وتقدم العملاء المستقل.
/// </summary>
public partial class PlanningView : UserControl
{
    private List<Views.PlanSearchWindow.PlanSearchItem> _plans_all = new();
    private readonly ObservableCollection<PlanRowUi> _rows = new();
    private List<(int? id, string name)> _planCustomers = new();
    private List<int> _planIds = new();
    // §إصلاح: معرّفات الورديات/الخطوط الفعلية المعروضة — بدل افتراض SelectedIndex+1
    private List<int> _shiftIds = new();
    private List<int> _lineIds = new();
    private int SelectedShiftId() => ShiftBox.SelectedIndex >= 0 && ShiftBox.SelectedIndex < _shiftIds.Count ? _shiftIds[ShiftBox.SelectedIndex] : 1;
    private int SelectedLineId() => LineBox.SelectedIndex >= 0 && LineBox.SelectedIndex < _lineIds.Count ? _lineIds[LineBox.SelectedIndex] : 1;
    /// <summary>§v1.50.35 — خيار الصنف التام يحمل مواصفات البطاقة (وزن/قوالب/طاقة) لا عبوة عامة.</summary>
    internal static ProductOption ToProductOpt(DatesErp.Core.Domain.Entities.Product p,
        IReadOnlyDictionary<int, double> packageWeights = null)
    {
        double? defaultPackageWeight = null;
        if (p.DefaultPackagingTypeId is int packageId && packageWeights != null
            && packageWeights.TryGetValue(packageId, out var configuredWeight))
            defaultPackageWeight = configuredWeight;

        return new ProductOption
        {
            Id = p.Id,
            Name = $"{p.ProductNameAr} ({p.ProductCode})",
            // نفس UnitsPolicy.CartonWeight مع أوزان العبوات المحملة دفعة واحدة، لتفادي
            // استعلام منفصل لكل صنف/دفعة في نافذة الاختيار الكبيرة.
            DefaultCartonWeightKg = UnitsPolicy.CartonWeight(p, defaultPackageWeight),
            CartonWeightKg = p.CartonWeightKg,
            MoldsCount = p.MoldsCount,
            MoldWeightKg = p.MoldWeightKg,
            HourlyRate = p.HourlyProductionRate,
            DefaultPackagingTypeId = p.DefaultPackagingTypeId
        };
    }

    private static string UnitWithCartonWeight(string unit, double weightKg)
    {
        var label = string.IsNullOrWhiteSpace(unit) ? "كرتون" : unit;
        return weightKg > 0 ? $"{label} ({weightKg:N1} كجم)" : label;
    }
    private int? _currentPlanId; // §1.50.67 FIX: nullable to fix CS0472 and Value usage
    private string _savedPlanFingerprint = ""; // §1.50.71 P1: بصمة آخر حالة محفوظة/محملة (رأس + بنود صالحة) — تكشف تعديلات غير محفوظة قبل الاعتماد
    private bool _locked;
    private bool _programmaticScope; // حارس: تغييرات النطاق البرمجية لا تفتح النوافذ تلقائياً
    private Views.ErpToolbar _toolbar;
    // §1.50.60 — تحسينات عامة 7-ب/7-ج/7-هـ: حفظ تلقائي + تكرار صف + تنقل لوحة مفاتيح
    private System.Windows.Threading.DispatcherTimer _autoSaveTimer;
    private DateTime _lastAutoSave = DateTime.MinValue;
    private string AutoSavePath
    {
        get
        {
            var fileName = $"PlanningDraft_{(AppContainer.Provider?.GetService(typeof(ICurrentSession)) is ICurrentSession cs ? cs.UserId : 0)}.json";
            var dataRoot = IsolatedTestRun.IsEnabled || StandaloneInstall.IsEnabled
                ? AppConfig.ConfigDirectory
                : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DateERP");
            return System.IO.Path.Combine(dataRoot, "drafts", fileName);
        }
    }

    // §B58: قوائم الخلاياEditable (وردية/خط/عبوة) — تُقرأ من قاعدة البيانات في Load
    public List<OptUi> ShiftOptions { get; } = new();
    public List<OptUi> LineOptions { get; } = new();
    public List<OptUi> PackOptions { get; } = new();

    public PlanningView()
    {
        InitializeComponent();
        RowsGrid.ItemsSource = _rows;
        _rows.CollectionChanged += (_, e) =>
        {
            Renumber(); UpdateCapacityBar(); UpdateTotals();
            if (e.NewItems != null)
                foreach (PlanRowUi row in e.NewItems)
                {
                    row.PropertyChanged += RowUi_Changed;
                    row.QuantityGuard = q => CheckRowQuantity(row, q);
                }
            if (e.OldItems != null)
                foreach (PlanRowUi row in e.OldItems) { row.PropertyChanged -= RowUi_Changed; row.QuantityGuard = null; }
        };
        // §1.50.67 FIX: إلغاء الحفظ التلقائي — كان يحفظ خطط وهمية (بناءً على طلب المستخدم)
        // _autoSaveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        // _autoSaveTimer.Tick += (_, _) => AutoSaveDraft();
        _autoSaveTimer = null;
        // §1.50.60 7-هـ: تنقل لوحة مفاتيح مثل Excel
        RowsGrid.PreviewKeyDown += RowsGrid_PreviewKeyDown;
        Loaded += (_, _) =>
        {
            Services.ComboBoxAutoShowHelper.Apply(this);
            Load();
            // §إصلاح: قائمة الخطط المحفوظة تُحمّل فور فتح الشاشة لتظهر مباشرة في شبكة السجل
            RefreshPlansList();
            // لا تُقرأ أو تُستعاد المسودات القديمة؛ حذفها محصور بالتشغيل العادي فقط.
            RemoveLegacyPlanningDrafts();
            // §فتح خطة محددة طُلبت من شاشة أخرى (لوحة التحكم) ثم تصفير الطلب
            if (MainWindow.PendingPlanIdToOpen is int pid)
            {
                MainWindow.PendingPlanIdToOpen = null;
                OpenPlan(pid);
            }
        };
        Unloaded += (_, _) => RemoveLegacyPlanningDrafts();
    }

    public void AttachChrome(Views.ErpChrome chrome)
    {
        chrome.SetModule("خطة الإنتاج — التخطيط والجدولة (MPS)");
        // وحدة الصلاحيات ثابتة بالكود، وليست عنوان الشاشة المعروض للمستخدم.
        chrome.SetPermissionModule("planning");
        chrome.SetScreenCode("MRPMPS1001");
        _toolbar = new Views.ErpToolbar()
            .WithNew((_, _) => NewPlan(), "خطة إنتاج جديدة (F2)")
            .WithSave((_, _) => Save_Click(null, null), "حفظ الخطة (F10)")
            // §إصلاح: الاعتماد كان مكتوباً في Approve() لكنه غير موصول بأي زر — فلم يكن ممكناً
            // اعتماد خطة من شاشتها إطلاقاً، وكان المسار الوحيد عبر لوحة التحكم وللمدير فقط.
            .WithApprove((_, _) => Approve(), "اعتماد الخطة ونقلها لأوامر التشغيل")
            .WithDelete((_, _) => DeletePlan())
            .WithUndo((_, _) => UndoInput(), "تراجع / مسح التعديلات والبدء من جديد")
            .WithSearch((_, _) => OpenPlansSearch(), "بحث واختيار من الخطط المحفوظة (F9)")
            .WithPrint((_, _) => Print())
            .WithExcel((_, _) => Export())
            .WithUnapprove((_, _) => Unapprove(), "إلغاء الاعتماد وإعادة الفتح")
            .WithNavigation((_, _) => Nav(0), (_, _) => Nav(-1), (_, _) => Nav(1), (_, _) => Nav(int.MaxValue))
            .WithCustom("📋 الخطط السابقة (F9)", "ErpButton", (_, _) => OpenPlansSearch())
            .WithExit((_, _) => (Window.GetWindow(this) as MainWindow)?.OpenPreviousScreen());
        if (_toolbar.UnapproveBtn != null) _toolbar.UnapproveBtn.Visibility = Visibility.Collapsed;
        if (_toolbar.ApproveBtn != null) _toolbar.ApproveBtn.Visibility = Visibility.Collapsed;
        chrome.SetToolbar(_toolbar);
        chrome.SetBody(this);
        chrome.CloseRequested += (_, _) => (Window.GetWindow(this) as MainWindow)?.OpenPreviousScreen();
    }

    private void Load()
    {
        // كل قسم يُحمَّل مستقلاً: فشل قسم (كالورديات) لا يجوز أن يمنع ظهور قائمة العملاء
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var shiftRows = db.Shifts.Where(s => s.IsActive).OrderBy(s => s.Id).ToList();
            _shiftIds = shiftRows.Select(s => s.Id).ToList();
            // §B58: خيارات خلايا الجدول (وردية/خط/عبوة) من قاعدة البيانات
            ShiftOptions.Clear(); ShiftOptions.AddRange(shiftRows.Select(x => new OptUi { Id = x.Id, Name = x.ShiftNameAr }));
            LineOptions.Clear(); LineOptions.AddRange(db.ProductionLines.AsNoTracking().OrderBy(x => x.Id).Select(x => new OptUi { Id = x.Id, Name = x.LineNameAr }));
            PackOptions.Clear(); PackOptions.Add(new OptUi { Id = 0, Name = "عام (أي عبوة)" });
            PackOptions.AddRange(db.PackagingTypes.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Id).Select(x => new OptUi { Id = x.Id, Name = x.PackageNameAr }));
            ShiftBox.ItemsSource = shiftRows
                .Select(s => $"{s.ShiftNameAr} (ساعات فعلية: {s.EffectiveProductiveHours} س)").ToList();
            var lineRows = db.ProductionLines.Where(l => l.IsActive).OrderBy(l => l.Id).ToList();
            _lineIds = lineRows.Select(l => l.Id).ToList();
            LineBox.ItemsSource = lineRows
                .Select(l => $"{l.LineNameAr} (طاقة: {l.CapacityPerShift} كجم)").ToList();
            if (ShiftBox.Items.Count > 0) ShiftBox.SelectedIndex = 0;
            if (LineBox.Items.Count > 0) LineBox.SelectedIndex = 0;
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.Load.ShiftsLines"); }

        RefreshCustomerList();

        try
        {
            using var _numScope = AppContainer.NewScope();
            var _num = _numScope.ServiceProvider.GetRequiredService<INumberingService>().Peek("PLAN");
            CodeBox.Text = _num;
        }
        catch { CodeBox.Text = "PLN-تلقائي"; }
        if (PlanMetaBox != null) PlanMetaBox.Text = "خطة جديدة — لم تُحفظ بعد · أنشأها: — · اعتمدها: —";
        // §2 — الشاشة تفتح فارغة في وضع «خطة جديدة» — الخطط المحفوظة تظهر عبر زر «قائمة الخطط / بحث»
        UpdateCapacityBar();
    }

    /// <summary>تحميل/إعادة تحميل قائمة العملاء لخطة العميل المحدد — مع إظهار سبب الفشل الحقيقي إن فشل.</summary>
    private void RefreshCustomerList()
    {
        try
        {
            if (SingleCustBox == null) return;
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var customers = db.Customers.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.CustomerName).ToList();
            SingleCustBox.ItemsSource = customers;
            if (customers.Count == 0)
                AppContainer.Get<DialogService>().Error("لا يوجد عملاء نشطون في البيانات الأساسية — أضف العملاء أولاً من شاشة بيانات العملاء.");
            else if (SingleCustBox.SelectedIndex < 0)
                SingleCustBox.Text = "-- اختر العميل --";
        }
        catch (Exception ex)
        {
            AppContainer.Get<DialogService>().HandleException(ex, "Planning.Load.Customers");
        }
    }

    // ══════════ نطاق التخطيط ونوع الخطة ══════════

    private void Scope_Changed(object sender, RoutedEventArgs e)
    {
        if (SingleCustPanel == null) return;
        bool single = SingleRadio.IsChecked == true;
        SingleCustPanel.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
        // الخطة لعميل واحد: العميل يُحفظ في رأس النموذج فلا يظهر عموده بجوار البنود
        // §عميل واحد: لا يُعرض عمود العميل — كل البنود له، فالعمود تكرار بلا معلومة.
        if (CustomerColumn != null)
            CustomerColumn.Visibility = single ? Visibility.Collapsed : Visibility.Visible;
        if (ScopeChip != null)
            ScopeChip.Text = single ? "النطاق: 👤 عميل محدد" : "النطاق: 👥 عدة عملاء (مجمع)";
        // في كل مرة يختار المستخدم «خطة لعميل محدد» تُعاد قراءة قائمة العملاء لضمان ألا تكون فارغة
        if (single) RefreshCustomerList();
    }

    /// <summary>نوع الخطة — يومية = تاريخ واحد فقط، الباقي فترة من-إلى. أزرار المدد السريعة أزيلت (1.50.57) بناءً على طلب المستخدم: الاكتفاء بتحديد التاريخ.</summary>
    private void TypeBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TypeBox == null || EndBox == null) return;
        bool isDaily = TypeBox.SelectedIndex == 0;
        bool isPeriod = TypeBox.SelectedIndex == 3;
        // يومية: تاريخ واحد فقط
        if (StartLabel != null) StartLabel.Text = isDaily ? "تاريخ الخطة *:" : "من تاريخ *:";
        if (EndPanel != null) EndPanel.Visibility = isDaily ? Visibility.Collapsed : Visibility.Visible;
        if (DateColumn != null) DateColumn.Visibility = isDaily ? Visibility.Collapsed : Visibility.Visible;
        if (isDaily)
        {
            if (StartBox?.SelectedDate != null)
            {
                EndBox.SelectedDate = StartBox.SelectedDate;
                // يومية: كل البنود بنفس تاريخ الخطة
                foreach (var r in _rows) r.DateValue = StartBox.SelectedDate;
                RowsGrid.Items.Refresh();
            }
            EndBox.IsEnabled = false;
        }
        else
        {
            EndBox.IsEnabled = isPeriod ? !_locked : true;
            if (!isPeriod && StartBox?.SelectedDate != null)
                EndBox.SelectedDate = PlanningService.PeriodEndDate(PlanTypeKey(), StartBox.SelectedDate.Value);
        }
        UpdateDateChips();
    }

    private void PlanDates_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TypeBox == null || StartBox == null || EndBox == null) return;
        bool isDaily = TypeBox.SelectedIndex == 0;
        if (isDaily && StartBox.SelectedDate != null)
        {
            EndBox.SelectedDate = StartBox.SelectedDate;
            // يومية: حدث كل البنود لتاريخ الخطة الواحد
            foreach (var r in _rows) r.DateValue = StartBox.SelectedDate;
            RowsGrid.Items.Refresh();
        }
        else if (TypeBox.SelectedIndex != 3 && StartBox.SelectedDate != null)
        {
            EndBox.SelectedDate = PlanningService.PeriodEndDate(PlanTypeKey(), StartBox.SelectedDate.Value);
        }
        UpdateDateChips();
        UpdateCapacityBar();
    }

    /// <summary>
    /// §التخطيط الأفقي: شرائح السياق أعلى الشاشة — «تاريخ الإنتاج المحدد» والفترة.
    /// تاريخ الإنتاج المحدد هو اليوم المُعيَّن للتنفيذ (تاريخ البداية)؛ وإن كانت الفترة أطول من يوم
    /// وُسِم بأنه بداية الفترة لأن لكل بند تاريخه المستقل في الجدول.
    /// </summary>
    private void UpdateDateChips()
    {
        try
        {
            var from = StartBox?.SelectedDate;
            var to = EndBox?.SelectedDate;
            string d(DateTime? v) => DatesErp.Core.Common.UiFormat.D(v);
            if (PeriodChip != null)
                PeriodChip.Text = from != null && to != null ? $"الفترة: {d(from)} – {d(to)}" : "الفترة: —";
            if (ScheduledDateChip != null)
            {
                if (from == null) ScheduledDateChip.Text = "📅 تاريخ الإنتاج المحدد: —";
                else if (to != null && to.Value.Date != from.Value.Date)
                    ScheduledDateChip.Text = $"📅 تاريخ الإنتاج المحدد: {d(from)} (بداية الفترة — لكل بند تاريخه في الجدول)";
                else ScheduledDateChip.Text = $"📅 تاريخ الإنتاج المحدد: {d(from)}";
            }
        }
        catch (Exception ex) { ErrorLog.Write(ex, "Planning.DateChips"); }
    }

    /// <summary>مطابق لـ v1.59 onSinglePlanCustomerChanged: اختيار العميل في الخطة الفردية يفتح نافذة دفعاته فوراً.</summary>
    private void SingleCust_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_programmaticScope || SingleCustBox == null || SingleCustBox.SelectedItem == null) return;
        if (SingleRadio.IsChecked != true) return;
        try
        {
            var cust = SingleCustBox.SelectedItem;
            var id = (int)cust.GetType().GetProperty("Id")!.GetValue(cust)!;
            var name = (string)cust.GetType().GetProperty("CustomerName")!.GetValue(cust)!;
            OpenLotsEditor(id, name);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.SingleCustomer"); }
    }

    // ══════════ الإدراج: أصناف العميل / العملاء ══════════

    /// <summary>👤 أصناف العميل (F4): عميل محدد ← نافذة شحناته ودفعاته ← اختيار الدفعة ← تفاصيل البند.</summary>
    private void PickLot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            int? custId = null; string custName = null;
            if (SingleRadio.IsChecked == true)
            {
                if (SingleCustBox.SelectedItem == null)
                { AppContainer.Get<DialogService>().Error("اختر العميل المحدد أولاً."); return; }
                custId = (int)SingleCustBox.SelectedItem.GetType().GetProperty("Id").GetValue(SingleCustBox.SelectedItem);
                custName = (string)SingleCustBox.SelectedItem.GetType().GetProperty("CustomerName").GetValue(SingleCustBox.SelectedItem);
                AddRowForCustomer(custId.Value, custName);
            }
            else
            {
                OpenLotsEditor(null, null);
            }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.PickLot"); }
    }

    /// <summary>👥 أصناف العملاء (F6): النافذة المجمعة لشحنات ودفعات كافة العملاء (مطابقة لـ v1.59).</summary>
    private void MultiCustomers_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // §B76: في وضع «عميل محدد» لا تُفتح قائمة كل العملاء — دفعات عميله فقط
            if (SingleRadio.IsChecked == true)
            {
                if (SingleCustBox.SelectedItem == null)
                { AppContainer.Get<DialogService>().Error("اختر العميل المحدد أولاً."); return; }
                var cust = SingleCustBox.SelectedItem as DatesErp.Core.Domain.Entities.Customer;
                OpenLotsEditor(cust.Id, cust.CustomerName);
                return;
            }
            OpenLotsEditor(null, null);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.MultiCustomers"); }
    }

    /// <summary>👤 أصناف العميل (F4): فتح النافذة المنبثقة لدفعات العميل المحدد فقط.</summary>
    private void AddRowForCustomer(int custId, string custName)
    {
        OpenLotsEditor(custId, custName);
    }

    /// <summary>
    /// النافذة المنبثقة لاختيار الأصناف — مطابقة لنموذج v1.59:
    /// كل دفعة صف قابل للتحرير (الصنف التام ← العبوة ← الكراتين ← الخام يُحسب) مع تحديد متعدد
    /// وإدراج فردي وإنزال جماعي. إن مرَّ عميل محدد تُعرض دفعاته فقط.
    /// </summary>
    private void OpenLotsEditor(int? custId, string custName)
    {
        if (_locked) { AppContainer.Get<DialogService>().Error("الخطة معتمدة ومقفلة — فك الاعتماد أولاً (بصلاحية، وبعد إلغاء الحركات اللاحقة)."); return; }
        using var scope = AppContainer.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        int shiftId = SelectedShiftId();
        string rowDate = (StartBox.SelectedDate ?? DateTime.Today).ToString("dd/MM/yyyy");

        var svc = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        // فلتر مطابق لـ v1.59: أصناف المجموعة 002 أو بلا مجموعة — كي تظهر أصناف المصنع مهما كان تعبئة المجموعة
        var products = svc.GetFinishedProducts();
        var packageDefinitions = db.PackagingTypes.AsNoTracking().ToList();
        var packs = packageDefinitions.Where(p => p.IsActive).ToList();
        var packageWeights = packs.ToDictionary(p => p.Id, p => p.UnitWeightKg);
        if (packs.Count == 0 && products.Count > 0)
        {
            AppContainer.Get<DialogService>().Error("لا توجد عبوات معرفّة — أضف العبوات (الكراتين والقوالب) من شاشة العبوات أولاً.");
            return;
        }

        // §B67: مصدر واحد مُفلتر: عميل محدد ← دفعاته فقط (حتى الموروثة من السند)؛ عدة عملاء ← الكل
        var lotDtos = svc.GetAvailableLots(custId);
        var today = DateTime.Today;
        var rawByLot = db.Lots.AsNoTracking().ToDictionary(l => l.Id, l => l.ProductId);
        int? exclPlan = _currentPlanId.HasValue && _currentPlanId.Value > 0 ? _currentPlanId : (int?)null;
        DateTime? planRefDate = StartBox.SelectedDate;
        var editorRows = lotDtos.Select(l =>
        {
            int daysSinceArrival = l.ArrivalDate != null
                ? (today - l.ArrivalDate.Value.Date).Days : 0;
            int nonnegativeDaysSinceArrival = daysSinceArrival > 0 ? daysSinceArrival : 0;
            var row = new LotEditorRow
            {
                LotId = l.LotId,
                ShipmentId = l.ShipmentId,
                ShipmentNo = l.ShipmentNo ?? "—",
                WarehouseId = l.WarehouseId,
                WarehouseName = l.WarehouseName ?? (l.WarehouseId != null ? $"مخزن {l.WarehouseId}" : "غير محدد"),
                LotCode = l.LotCode,
                CustomerId = l.CustomerId, // §B87/M6: null = «بدون عميل» — يُحفَظ NULL لا صفراً
                CustomerName = l.CustomerName ?? (custName ?? "—"),
                RawName = l.ProductName ?? "—",
                OnHandKg = l.InitialQtyKg,
                ReservedKg = l.ReservedQtyKg,
                Available = l.RemainingKg,
                // §المعالجة: حالتها على الدفعة (حتى يُعرض القيد باللون الأحمر ويُمنع الإنتاج قبل التاريخ)
                TreatmentRequired = l.RequiresTreatment,
                TreatmentUntilDate = l.TreatmentUntilDate,
                TreatmentReadyDate = l.TreatmentReadyDate,
                UnderTreatmentKg = l.UnderTreatmentKg,
                // §تتبع سحب الخام بوحدة الاستلام وبحسب المخزن الفعلي
                Ctx = svc.GetShipmentQuantityContext(l.LotId, l.WarehouseId, exclPlan, planRefDate),
                DaysInStockText = l.ArrivalDate != null ? $"{nonnegativeDaysSinceArrival} يوماً" : "",
                // القائمة تُحصر بالأصناف المرتبطة رسمياً بخام الدفعة؛ ويبقى الرصيد تفصيلاً مستقلاً.
                AllProducts = svc.GetPlannableProducts(l.LotId).Select(p => ToProductOpt(p, packageWeights)).ToList(),
                AllPacks = packs.Select(p => new PackOption { Id = p.Id, Name = p.PackageNameAr, UnitWeightKg = p.UnitWeightKg, MoldsCount = p.MoldsCount, MoldWeightKg = p.MoldWeightKg }).ToList(),
                // §B80: وحدة كل صنف تام كما في بطاقته — تظهر فور اختيار الصنف
                ProductUnits = products.ToDictionary(x => x.Id, x => string.IsNullOrWhiteSpace(x.UnitOfMeasure) ? "—" : x.UnitOfMeasure)
            };
            foreach (var p2 in products)
                row.PerProductAvailable[p2.Id] = svc.GetProductLotRemaining(l.LotId, p2.Id, exclPlan);
            ConfigureRawSelector(row, rawByLot.TryGetValue(l.LotId, out var raw) ? raw : null, db, svc, packageWeights);
            row.SetContext(); // §يبني خيارات طريقة السحب من وحدة الاستلام الفعلية (يثبّت أيضاً حالة المعالجة)
            return row;
        }).Where(r => r.Available > 0 || r.UnderTreatmentKg > 0).ToList(); // §تُعرض الكميات تحت المعالجة أيضاً
        var allLots = lotDtos;

        if (editorRows.Count == 0)
        {
            // §B56: رسالة تشخيصية صادقة تفرز السببين: لا دفعات أصلاً، أم دفعات محجوزة/مستهلكة بالكامل
            if (allLots.Count == 0)
            {
                int anyLots = db.Lots.Count();
                AppContainer.Get<DialogService>().Error(custId != null
                    ? $"لا توجد دفعات خام في المخزن باسم العميل «{custName}» (إجمالي الدفعات بالنظام: {anyLots}).\n" +
                      "التخطيط يستهلك الخام من دفعات الاستلام — استلم خاماً لهذا العميل من شاشة الاستلام ثم عد للتخطيط."
                    : "لا توجد شحنات خام في المخزن حالياً — التخطيط يبني بنوده على دفعات الاستلام.\n" +
                      "استلم الخام أولاً من شاشة «الاستلام وسندات الاستلام» (يعتمد الاستلام فتتكوّن الدفعات)، ثم عد إلى الخطط.");
            }
            else
            {
                double totalReserved = allLots.Sum(l => l.ReservedQtyKg); // lotDtos
                AppContainer.Get<DialogService>().Error(
                    $"توجد {allLots.Count} دفعة بالمخزن لكن المتاح منها للتخطيط صفر — كل الكميات محجوزة لخطط/أوامر قائمة أو مستهلكة.\n" +
                    $"إجمالي المحجوز: {totalReserved:N1} كجم. أقفل أو احذف الخطط المنتهية لتحرير الحجوزات، أو استلم خاماً إضافياً.");
            }
            return;
        }

        string title = custId != null
            ? $"👤 أصناف وشحنات العميل: {custName} — اختر الصنف التام والكمية ثم إنزال"
            : "👥 أصناف وشحنات كافة العملاء المتاحة بالمستودع — دليل الاختيار المجمع";
        // §B80: فترة الخطة تُمرر للنافذة — تاريخ كل بند إلزامي داخلها
        DateTime? planFrom = StartBox.SelectedDate ?? DateTime.Today;
        DateTime? planTo = EndBox.SelectedDate ?? planFrom;
        // §B92: الاختيار اليدوي للوردية — الورديات النشطة + وردية الشاشة افتراضياً
        var shiftsForManual = db.Shifts.Where(s => s.IsActive).OrderBy(s => s.Id).ToList();
        var win = new LotsEditorWindow(editorRows, title, custId != null, planFrom, planTo, shiftsForManual, shiftId,
            additions => EvaluateDraft(_rows.Where(r => (r.LotId != null || r.ProductId != 0) && r.Cartons > 0).Select(CapacityItem).Concat(additions).ToList()), SelectedLineId()) { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() != true || win.Inserted.Count == 0) return;

        // تحويل الصفوف المدرجة إلى بنود الخطة (صنف كامل أو جزء — حسب ما أدخله المستخدم)
        InsertEditorRows(win.Inserted, products, packs, rowDate, shiftId,
            LineBox.SelectedIndex >= 0 ? SelectedLineId() : 1, db);
    }

    /// <summary>
    /// §B91 — 🔍 فحص الخطة: يوزّع بنود الخطة المحفوظة (عملاء/أصناف) على أيام الفترة (من–إلى)
    /// بنفس عمل محرك التوزيع، ويعرض الحكم (قابلة للتنفيذ/عجز) + الأيام + العملاء + الأصناف + التحذيرات.
    /// </summary>
    private void CheckPlan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_currentPlanId == null || _currentPlanId.Value == 0) { AppContainer.Get<DialogService>().Error("احفظ الخطة أولاً أو اختر خطة من السجل لفحصها."); return; }
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            var result = svc.CheckPlan(_currentPlanId.Value);
            var win = new PlanCheckWindow(result) { Owner = Window.GetWindow(this) };
            win.ShowDialog();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Plan.Check"); }
    }

    /// <summary>
    /// ⚖ اقتراح توزيع عادل — معالج آلي بفترة حرة (أسبوع/20 يوماً/شهر...) ووردية إلزامية:
    /// يبني البنود من الأرصدة المتاحة بالتناوب العادل (الأقل إنجازاً أولاً + أقدم الحاويات FIFO)،
    /// يعرض نصيب كل عميل وأيام إنتاجه، ثم يتيح تنزيلها كاملة أو جزئياً بعد التعديل.
    /// </summary>
    private void Fair_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool amendmentMode = _locked && _currentPlanId is > 0;
            if (_locked && !amendmentMode) { AppContainer.Get<DialogService>().Error("لا يمكن إعادة التوزيع إلا لخطة معتمدة مفتوحة ولجلسات لم تبدأ."); return; }
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            var pendingSessions = new List<ProductionPlanSession>();
            ProductionPlan amendmentPlan = null;
            if (amendmentMode)
            {
                amendmentPlan = db.ProductionPlans.AsNoTracking().FirstOrDefault(p => p.Id == _currentPlanId.Value);
                if (amendmentPlan == null || !amendmentPlan.IsApproved || amendmentPlan.IsClosed)
                { AppContainer.Get<DialogService>().Error("إعادة التوزيع متاحة لخطة معتمدة ومفتوحة فقط."); return; }
                pendingSessions = db.ProductionPlanSessions.AsNoTracking()
                    .Where(s => s.PlanId == amendmentPlan.Id && s.Status == "Pending")
                    .OrderBy(s => s.SessionDate).ThenBy(s => s.SequenceNo).ToList();
                if (pendingSessions.Count == 0)
                { AppContainer.Get<DialogService>().Error("لا توجد جلسات مستقبلية غير منفذة لإعادة توزيعها."); return; }
            }
            var allowedShiftIds = amendmentMode ? pendingSessions.Select(s => s.ShiftId).Distinct().ToHashSet() : new HashSet<int>();
            var shifts = db.Shifts.Where(s => s.IsActive && (!amendmentMode || allowedShiftIds.Contains(s.Id))).ToList();
            var lines = db.ProductionLines.Where(l => l.IsActive && (!amendmentMode || l.Id == amendmentPlan.LineId)).ToList();
            var products = svc.GetFinishedProducts();

            var start = amendmentMode ? pendingSessions.Min(s => s.SessionDate).Date : StartBox.SelectedDate ?? DateTime.Today;
            var end = amendmentMode ? pendingSessions.Max(s => s.SessionDate).Date : EndBox.SelectedDate ?? start.AddDays(6);
            var defaultShift = amendmentMode ? pendingSessions.First().ShiftId : ShiftBox.SelectedIndex >= 0 ? SelectedShiftId() : 1;
            var defaultLine = amendmentMode ? amendmentPlan.LineId ?? 1 : LineBox.SelectedIndex >= 0 ? SelectedLineId() : 1;
            var wiz = new FairDistributionWizardWindow(shifts, lines, products, start, end, defaultShift, defaultLine)
            { Owner = Window.GetWindow(this) };
            if (wiz.ShowDialog() != true) return;

            var proposal = svc.SuggestFairDistribution(wiz.FromDate, wiz.ToDate, wiz.ShiftId, wiz.LineId, wiz.TargetProductId, wiz.DailyKg, wiz.ExcludeFriday, wiz.UseCumulative, wiz.CapPerCustomerPerDay, wiz.UseFullDay,
                _currentPlanId is > 0 ? _currentPlanId : null);
            if (!proposal.Ok || proposal.Rows.Count == 0)
            { AppContainer.Get<DialogService>().Error(proposal.Message ?? "لا توجد نتائج للتوزيع."); return; }

            List<int> replaceSessionIds = new();
            Dictionary<(DateTime Day, int ShiftId, int LineId), int> amendmentSessionMap = new();
            if (amendmentMode)
            {
                Core.Common.UiFormat.TryParseDate(wiz.FromDate, out var amendFrom);
                Core.Common.UiFormat.TryParseDate(wiz.ToDate, out var amendTo);
                var chosenShiftIds = wiz.UseFullDay ? shifts.Select(s => s.Id).ToHashSet() : new HashSet<int> { wiz.ShiftId };
                var selectedSessions = pendingSessions.Where(s => s.SessionDate.Date >= amendFrom.Date && s.SessionDate.Date <= amendTo.Date
                    && chosenShiftIds.Contains(s.ShiftId) && s.LineId == wiz.LineId).ToList();
                if (selectedSessions.Count == 0)
                { AppContainer.Get<DialogService>().Error("النطاق المختار لا يحتوي جلسات مستقبلية معلّقة في هذه الخطة."); return; }
                replaceSessionIds = selectedSessions.Select(s => s.Id).ToList();
                amendmentSessionMap = selectedSessions.ToDictionary(s => (s.SessionDate.Date, s.ShiftId, s.LineId), s => s.Id);
                proposal.Rows = proposal.Rows.Where(r => Core.Common.UiFormat.TryParseDate(r.Date, out var day)
                    && amendmentSessionMap.ContainsKey((day.Date, r.ShiftId, wiz.LineId))).ToList();
                if (proposal.Rows.Count == 0)
                { AppContainer.Get<DialogService>().Error("لا توجد كميات قابلة للتوزيع في الجلسات المستقبلية المحددة؛ لم يتغير الإصدار السابق."); return; }
                proposal.Customers = proposal.Customers.Select(c =>
                {
                    var rowsForCustomer = proposal.Rows.Where(r => r.CustomerId == c.CustomerId).ToList();
                    c.AllocatedKg = rowsForCustomer.Sum(r => r.PlannedQtyKg);
                    c.ProposedFinishedKg = c.AllocatedKg;
                    c.AllocatedCartons = rowsForCustomer.Sum(r => r.PlannedCartons);
                    c.ProductionDays = rowsForCustomer.Select(r => r.Date).Distinct().ToList();
                    return c;
                }).Where(c => proposal.Rows.Any(r => r.CustomerId == c.CustomerId)).ToList();
                proposal.DaysUsed = proposal.Rows.Select(r => r.Date).Distinct().Count();
                proposal.Message = "إصدار إعادة توزيع للجلسات المستقبلية فقط — سيُحفظ الإصدار السابق كما هو.\\n" + proposal.Message;
            }

            var summary = new FairSummaryWindow(proposal) { Owner = Window.GetWindow(this) };
            if (summary.ShowDialog() != true) return;
            if (!amendmentMode)
            {
                TypeBox.SelectedIndex = 3;
                if (Core.Common.UiFormat.TryParseDate(wiz.FromDate, out var chosenFrom)) StartBox.SelectedDate = chosenFrom;
                if (Core.Common.UiFormat.TryParseDate(wiz.ToDate, out var chosenTo)) EndBox.SelectedDate = chosenTo;
                ShiftBox.SelectedIndex = _shiftIds.IndexOf(wiz.ShiftId);
                LineBox.SelectedIndex = _lineIds.IndexOf(wiz.LineId);
            }


            // تحويل الاقتراح إلى صفوف قابلة للتحرير في نافذة الاختيار (صنف كامل أو جزء = عدّل الكراتين)
            var packageDefinitions = db.PackagingTypes.AsNoTracking().ToList();
            var packs = packageDefinitions.Where(p => p.IsActive).ToList();
            var packageWeights = packs.ToDictionary(p => p.Id, p => p.UnitWeightKg);
            var editorRows = proposal.Rows.Select(r =>
            {
                var er = new LotEditorRow
                {
                    LotId = r.LotId, ShipmentId = r.ShipmentId, ShipmentNo = r.ShipmentNo,
                    WarehouseId = r.WarehouseId, WarehouseName = r.WarehouseName,
                    LotCode = r.LotCode, CustomerId = r.CustomerId, CustomerName = r.CustomerName,
                    RawName = r.RawName, Available = r.AvailableKg,
                    DaysInStockText = $"{r.DaysInStock} يوماً",
                    PresetDate = r.Date,
                    // §تتبع سحب الخام: سياق سطر الاستلام لدفعة ومخزن هذا البند.
                    Ctx = svc.GetShipmentQuantityContext(r.LotId, r.WarehouseId, _currentPlanId.HasValue && _currentPlanId.Value > 0 ? _currentPlanId : null),
                    ShiftId = r.ShiftId, ShiftName = r.ShiftName ?? "—",
                    ProductId = r.ProductId, PackId = r.PackagingTypeId,
                    CartonsText = r.PlannedCartons.ToString(),
                    AllProducts = products.Select(p => ToProductOpt(p, packageWeights)).ToList(),
                    AllPacks = packs.Select(p => new PackOption { Id = p.Id, Name = p.PackageNameAr, UnitWeightKg = p.UnitWeightKg, MoldsCount = p.MoldsCount, MoldWeightKg = p.MoldWeightKg }).ToList()
                };
                var selectedProduct = er.ProductId;
                ConfigureRawSelector(er, db.Lots.AsNoTracking().Where(l => l.Id == r.LotId).Select(l => (int?)l.ProductId).FirstOrDefault(), db, svc, packageWeights);
                if (selectedProduct != null && er.AllProducts.Any(p => p.Id == selectedProduct)) er.ProductId = selectedProduct;
                er.SetContext(); // §يبني خيارات طريقة السحب من وحدة الاستلام الفعلية
                return er;
            }).ToList();

            DatesErp.Core.Common.UiFormat.TryParseDate(wiz.FromDate, out var fairFrom);
            DatesErp.Core.Common.UiFormat.TryParseDate(wiz.ToDate, out var fairTo);
            var fairUnits = products.ToDictionary(x => x.Id, x => string.IsNullOrWhiteSpace(x.UnitOfMeasure) ? "كرتون" : x.UnitOfMeasure);
            var fairCartonWeights = products.ToDictionary(x => x.Id, x => x.CartonWeightKg);
            foreach (var er2 in editorRows) { er2.ProductUnits = fairUnits; er2.ProductCartonWeights = fairCartonWeights; }
            LoadEditorRowTreatment(editorRows, db); // §يثبّت حالة المعالجة (أحمر/قيد) على بنود التوزيع
            var win = new LotsEditorWindow(editorRows,
                "⚖ بنود التوزيع العادل — راجع وعدّل (صنف كامل أو جزء، تاريخ ووردية) ثم أنزل للخطة", false, fairFrom, fairTo,
                shifts, wiz.ShiftId,
                additions => amendmentMode ? EvaluateDraft(additions) : EvaluateDraft(_rows.Where(r => (r.LotId != null || r.ProductId != 0) && r.Cartons > 0).Select(CapacityItem).Concat(additions).ToList()), wiz.LineId)
            { Owner = Window.GetWindow(this) };
            if (win.ShowDialog() != true || win.Inserted.Count == 0) return;

            // أي فرق عن أرقام المقترح يُسجّل معه السبب والمقارنة قبل/بعد في ملاحظات الخطة؛
            // تاريخ المستخدم/الحفظ يُسجّل من سجل تدقيق الخطة عند الحفظ.
            static (int Cartons, double FinishedKg, double RawKg) SumProposal(IEnumerable<(int Cartons, double FinishedKg, double RawKg)> rows)
                => (rows.Sum(x => x.Cartons), rows.Sum(x => x.FinishedKg), rows.Sum(x => x.RawKg));
            var before = SumProposal(proposal.Rows.Select(r => (r.PlannedCartons, r.PlannedQtyKg, r.RawRequiredQtyKg)));
            var after = SumProposal(win.Inserted.Select(r =>
            {
                int.TryParse(r.CartonsText, out var cartons);
                return (cartons, r.ComputedKg, r.RawRequiredKg);
            }));
            var beforeAllocation = proposal.Rows.GroupBy(r => $"{r.CustomerId}|{r.LotId}|{r.ProductId}|{r.PackagingTypeId}|{r.Date}|{r.ShiftId}")
                .ToDictionary(g => g.Key, g => g.Sum(r => r.PlannedCartons));
            var afterAllocation = win.Inserted.GroupBy(r => $"{r.CustomerId}|{r.LotId}|{r.ProductId}|{r.PackId}|{r.DateValue:dd/MM/yyyy}|{r.ShiftId}")
                .ToDictionary(g => g.Key, g => g.Sum(r => int.TryParse(r.CartonsText, out var q) ? q : 0));
            bool allocationChanged = beforeAllocation.Count != afterAllocation.Count
                || beforeAllocation.Any(x => !afterAllocation.TryGetValue(x.Key, out var cartons) || cartons != x.Value);
            bool manuallyChanged = allocationChanged || before.Cartons != after.Cartons
                || Math.Abs(before.FinishedKg - after.FinishedKg) > 0.01
                || Math.Abs(before.RawKg - after.RawKg) > 0.01
                || win.Inserted.Any(r => r.ProductId == null || r.PackId == null || r.DateValue == null || r.ShiftId == null);
            FairManualAdjustmentReasonWindow reason = null;
            string impact = $"المقترح: {before.Cartons:N0} كرتون، {before.FinishedKg:N1} كجم تام، {before.RawKg:N1} كجم خام.\nبعد التعديل: {after.Cartons:N0} كرتون، {after.FinishedKg:N1} كجم تام، {after.RawKg:N1} كجم خام.";
            if (manuallyChanged || amendmentMode)
            {
                reason = new FairManualAdjustmentReasonWindow(impact, amendmentMode) { Owner = Window.GetWindow(this) };
                if (reason.ShowDialog() != true) return;
                if (!amendmentMode)
                {
                    var stamp = $"[تعديل يدوي للتوزيع العادل | {DateTime.Now:dd/MM/yyyy HH:mm} | المستخدم: {AppContainer.Get<ICurrentSession>().UserName} | السبب: {reason.Reason} | الأثر: {impact.Replace("\n", " ")}]";
                    NotesBox.Text = string.IsNullOrWhiteSpace(NotesBox.Text) ? stamp : NotesBox.Text.TrimEnd() + "\n" + stamp;
                }
            }
            if (amendmentMode)
            {
                var amendedItems = new List<PlanItemDto>();
                foreach (var row in win.Inserted)
                {
                    if (row.ProductId is not int productId || row.PackId is not int packId || row.LotId is not int lotId
                        || row.WarehouseId is not int warehouseId || row.DateValue is not DateTime scheduled || row.ShiftId is not int shiftId
                        || !amendmentSessionMap.TryGetValue((scheduled.Date, shiftId, wiz.LineId), out var sessionId))
                    { AppContainer.Get<DialogService>().Error("كل بند تعديل يجب أن يبقى ضمن جلسة مستقبلية معلّقة مع مخزن ودفعة محددين."); return; }
                    if (!int.TryParse(row.CartonsText, out var cartons) || cartons <= 0 || row.ComputedKg <= 0 || row.RawRequiredKg <= 0)
                    { AppContainer.Get<DialogService>().Error("كمية المنتج التام واحتياج الخام يجب أن يكونا موجبين لكل بند تعديل."); return; }
                    int rawId = row.RawProductId ?? db.Lots.AsNoTracking().Where(l => l.Id == lotId).Select(l => l.ProductId).First();
                    amendedItems.Add(new PlanItemDto
                    {
                        SessionId = sessionId, SourceType = "FromReceiving", LotId = lotId,
                        ShipmentId = row.ShipmentId, SelectedRawProductId = rawId, CustomerId = row.CustomerId,
                        ProductId = productId, PackagingTypeId = packId, PlannedCartons = cartons,
                        PlannedQtyKg = row.ComputedKg, RawRequiredQtyKg = row.RawRequiredKg,
                        ScheduledDate = scheduled.ToString("dd/MM/yyyy"), SuggestedShiftId = shiftId,
                        SuggestedLineId = wiz.LineId, PriorityNo = amendedItems.Count + 1,
                        SourceUnit = row.SourceUnit, SourceQtyInUnit = row.SourceQtyInUnit,
                        SourceUnitWeightKg = row.SourceUnitWeightKg, SourceQtyKg = row.RawRequiredKg,
                        Allocations = new List<PlanItemAllocationDto>
                        {
                            new() { WarehouseId = warehouseId, LotId = lotId, CustomerId = row.CustomerId, AllocatedQtyKg = row.RawRequiredKg }
                        }
                    });
                }
                var revised = svc.AmendFuturePlan(_currentPlanId.Value, reason.Reason, amendedItems, replaceSessionIds);
                if (!revised.Ok) { AppContainer.Get<DialogService>().Error(revised.Message); return; }
                AppContainer.Get<DialogService>().Info(revised.Message);
                OpenPlan(_currentPlanId.Value);
                return;
            }
            InsertEditorRows(win.Inserted, products, packs, wiz.FromDate, wiz.ShiftId, wiz.LineId, db);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.Fair"); }
    }

    /// <summary>إنزال الصفوف المدرجة من نافذة الاختيار إلى جدول بنود الخطة (مشترك بين اليدوي والتوزيع العادل).</summary>
    private void InsertEditorRows(List<LotEditorRow> inserted, List<DatesErp.Core.Domain.Entities.Product> products,
        List<DatesErp.Core.Domain.Entities.PackagingType> packs, string fallbackDate, int shiftId, int lineId, DatesErpDbContext db)
    {
        var capacity = EvaluateDraft(_rows.Where(r => (r.LotId != null || r.ProductId != 0) && r.Cartons > 0).Select(CapacityItem).Concat(inserted.Select(r => LotsEditorWindow.CapacityItem(r, lineId))).ToList());
        if (!capacity.IsValid) { AppContainer.Get<DialogService>().Error(capacity.Error); return; }
        foreach (var row in inserted)
        {
            var pack = packs.FirstOrDefault(p => p.Id == row.PackId);
            var product = row.ProductId is int selectedProductId
                ? products.FirstOrDefault(p => p.Id == selectedProductId) : null;
            double cartonWeight = row.ProductId is int productId
                ? UnitsPolicy.CartonWeight(db, productId, row.PackId) : 0;
            var uiRow = new PlanRowUi
            {
                CustomerId = row.CustomerId,
                CustomerName = row.CustomerName,
                ShipmentId = row.ShipmentId,
                ShipmentNo = row.ShipmentNo,
                WarehouseId = row.WarehouseId,
                WarehouseName = row.WarehouseName,
                LotId = row.LotId,
                OnHandKg = row.OnHandKg,
                ReservedKg = row.ReservedKg,
                AvailableKg = row.AvailableKg,
                AllocationSelectedQtyKg = row.SelectedQtyKg,
                RawKgPerFinishedKg = row.RawKgPerFinishedKg,
                RawProductId = row.RawProductId,
                LotCode = row.LotCode,
                RawName = row.RawName,
                ProductId = row.ProductId ?? 0,
                ProductName = product?.ProductNameAr ?? "-",
                PackId = row.PackId,
                RawFactorPackagingTypeId = row.PackId,
                PackName = pack?.PackageNameAr ?? "-",
                UnitDisplay = UnitWithCartonWeight(product?.UnitOfMeasure ?? "كرتون", cartonWeight),
                CartonWeight = cartonWeight,
                QtyKg = row.ComputedKg,
                Cartons = int.TryParse(row.CartonsText, out var c) ? c : 0,
                // §B80: التاريخ من عمود التاريخ في النافذة (إلزامي) ثم السقط المسبق ثم بداية الفترة
                DateValue = row.DateValue
                    ?? (DatesErp.Core.Common.UiFormat.TryParseDate(row.PresetDate, out var pdv) ? pdv : (DateTime?)null)
                    ?? (DatesErp.Core.Common.UiFormat.TryParseDate(fallbackDate, out var fdv) ? fdv : DateTime.Today),
                ShiftId = row.ShiftId ?? shiftId, // §B87: وردية البند من المحرك، أو وردية الشاشة لليدوي
                ShiftName = db.Shifts.AsNoTracking().Where(x => x.Id == (row.ShiftId ?? shiftId)).Select(x => x.ShiftNameAr).FirstOrDefault() ?? "-",
                LineId = lineId,
                LineName = db.ProductionLines.AsNoTracking().Where(x => x.Id == lineId).Select(x => x.LineNameAr).FirstOrDefault() ?? "-",
                // §1.50.78 — تتبع سحب الخام يُنقل من النافذة المنبثقة إلى البند المنزَّل مع وحدة الاستلام الفعلية وتخصيص المخزن
                SourceUnit = row.SourceUnit ?? row.Ctx?.ReceiptUnit ?? "كجم",
                SourceQtyInUnit = row.SourceQtyInUnit,
                SourceUnitWeightKg = row.SourceUnitWeightKg > 0 ? row.SourceUnitWeightKg : (row.Ctx?.UnitWeightKg ?? 1.0),
                SourceQtyKg = row.SourceQtyKg,
                PreservedAllocations = row.LotId != null && row.WarehouseId != null && row.SelectedQtyKg > 0
                    ? new List<PlanItemAllocationDto>
                    {
                        new() { WarehouseId = row.WarehouseId, LotId = row.LotId.Value, CustomerId = row.CustomerId, AllocatedQtyKg = row.SelectedQtyKg }
                    }
                    : new List<PlanItemAllocationDto>()
            };
            uiRow.ApplySavedRawRequirement(row.RawRequiredKg, row.WastePercent, row.CustomRawRequiredKg);
            uiRow.ConfigureReceiptUnitContext(row.ReceiptUnitText, row.UnitWeight, row.SourceMode ?? row.SourceUnit);
            // إزالة أي صفوف فارغة قديمة إن وجدت قبل الإنزال
            for (int ri = _rows.Count - 1; ri >= 0; ri--)
                if (_rows[ri].LotId == null && _rows[ri].ProductId == 0) _rows.RemoveAt(ri);
            _rows.Add(uiRow);
            if (!_planCustomers.Any(x => x.id == row.CustomerId))
                _planCustomers.Add((row.CustomerId, row.CustomerName));
        }
        Renumber();
        UpdateCapacityBar();
        UpdateTotals();
        RowsGrid.Items.Refresh();
        AppContainer.Get<DialogService>().Info($"تم إنزال ({inserted.Count}) بند إلى الخطة بنجاح.");
    }

    /// <summary>✍️ إضافة يدوي (بدون دفعة).</summary>
    private void Manual_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_locked) { AppContainer.Get<DialogService>().Error("الخطة معتمدة ومقفلة."); return; }
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            var customers = db.Customers.Where(c => c.IsActive).ToList();
            var fixedCustomer = SingleRadio.IsChecked == true ? SingleCustBox.SelectedItem as DatesErp.Core.Domain.Entities.Customer : null;
            if (SingleRadio.IsChecked == true && fixedCustomer == null) { AppContainer.Get<DialogService>().Error("اختر العميل أولاً."); return; }
            if (fixedCustomer != null) customers = new() { fixedCustomer };
            string Label(DatesErp.Core.Domain.Entities.Customer c) => $"{c.CustomerName} ({c.CustomerCode})";
            var customerDialog = new Views.EntityFormDialog("عميل البند اليدوي", new List<Views.FieldDef>
            { new() { Key = "customer", LabelAr = "العميل", Kind = "combo", Options = customers.Select(Label).ToArray() } }) { Owner = Window.GetWindow(this) };
            if (customerDialog.ShowDialog() != true) return;
            var customer = customers.FirstOrDefault(c => Label(c) == customerDialog.Values["customer"]?.ToString());
            if (customer == null) return;
            var packageDefinitions = db.PackagingTypes.AsNoTracking().ToList();
            var packs = packageDefinitions.Where(p => p.IsActive).ToList();
            var packageWeights = packs.ToDictionary(p => p.Id, p => p.UnitWeightKg);
            var products = svc.GetFinishedProducts();
            var row = new LotEditorRow { CustomerId = customer.Id, CustomerName = customer.CustomerName,
                LotCode = "يدوي — دون دفعة", AllPacks = packs.Select(p => new PackOption { Id = p.Id, Name = p.PackageNameAr,
                    UnitWeightKg = p.UnitWeightKg, MoldsCount = p.MoldsCount, MoldWeightKg = p.MoldWeightKg }).ToList(),
                ProductUnits = products.ToDictionary(p => p.Id, p => p.UnitOfMeasure ?? "كرتون"),
                ProductCartonWeights = products.ToDictionary(p => p.Id, p => p.CartonWeightKg) };
            ConfigureRawSelector(row, null, db, svc, packageWeights);
            var window = new LotsEditorWindow(new() { row }, "اختر الخام ثم الصنف التام المرتبط والكمية", true,
                StartBox.SelectedDate ?? DateTime.Today, EndBox.SelectedDate ?? StartBox.SelectedDate ?? DateTime.Today,
                db.Shifts.Where(s => s.IsActive).ToList(), SelectedShiftId(),
                additions => EvaluateDraft(_rows.Where(r => (r.LotId != null || r.ProductId != 0) && r.Cartons > 0).Select(CapacityItem).Concat(additions).ToList()), SelectedLineId()) { Owner = Window.GetWindow(this) };
            if (window.ShowDialog() == true && window.Inserted.Count > 0)
                InsertEditorRows(window.Inserted, products, packs, (StartBox.SelectedDate ?? DateTime.Today).ToString("dd/MM/yyyy"), SelectedShiftId(), SelectedLineId(), db);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.Manual"); }
    }

    /// <summary>§يحمّل حالة المعالجة لصفوف الاختيار (من الدفعة/سطر الاستلام/الدورات الجارية) — يثبّت القيد الأحمر.</summary>
    private static void LoadEditorRowTreatment(List<LotEditorRow> rows, DatesErpDbContext db)
    {
        var lotIds = rows.Where(r => r.LotId != null).Select(r => r.LotId.Value).Distinct().ToList();
        if (lotIds.Count == 0) return;
        var lots = db.Lots.AsNoTracking().Where(l => lotIds.Contains(l.Id))
            .Select(l => new { l.Id, l.ShipmentItemId, l.ProductId, l.UnderTreatmentQtyKg }).ToList();
        var siIds = lots.Select(l => l.ShipmentItemId).Where(s => s != null).Distinct().ToList();
        var shipItems = db.ShipmentItems.AsNoTracking().Where(i => siIds.Contains(i.Id))
            .Select(i => new { i.Id, i.TreatmentRequired, i.TreatmentUntilDate }).ToList();
        var readyDates = db.RawTreatments.AsNoTracking()
            .Where(t => lotIds.Contains(t.LotId) && t.Status == DatesErp.Core.Domain.Entities.TreatmentStatuses.InProgress)
            .GroupBy(t => t.LotId)
            .Select(g => new { LotId = g.Key, Date = g.Max(x => x.ExpectedReadyAt) })
            .ToDictionary(x => x.LotId, x => x.Date);
        foreach (var r in rows)
        {
            var lot = lots.FirstOrDefault(l => l.Id == r.LotId);
            if (lot == null) continue;
            var si = shipItems.FirstOrDefault(i => i.Id == lot.ShipmentItemId);
            r.TreatmentRequired = (si?.TreatmentRequired
                ?? db.Products.AsNoTracking().Where(p => p.Id == lot.ProductId).Select(p => (bool?)p.RequiresTreatment).FirstOrDefault())
                ?? false;
            r.TreatmentUntilDate = si?.TreatmentUntilDate;
            r.TreatmentReadyDate = readyDates.TryGetValue(lot.Id, out var d) ? d : null;
            r.UnderTreatmentKg = lot.UnderTreatmentQtyKg;
            r.Recalc();
        }
    }

    private static void ConfigureRawSelector(LotEditorRow row, int? rawId, DatesErpDbContext db,
        IPlanningService svc, IReadOnlyDictionary<int, double> packageWeights)
    {
        row.RawOptions = db.Products.AsNoTracking().Where(p => p.IsActive && p.ItemType == "Raw")
            .OrderBy(p => p.ProductNameAr).ToList().Select(p => ToProductOpt(p, packageWeights)).ToList();
        row.LoadFinishedProducts = sourceRawId => svc.GetFinishedProductsForRaw(sourceRawId)
            .Select(p => ToProductOpt(p, packageWeights)).ToList();
        var factorCache = new Dictionary<(int FinishedId, int RawId, int? PackagingId), double>();
        row.ResolveRawFactor = (finishedId, selectedRawId, packagingId) =>
        {
            if (selectedRawId is not int selected) return 0;
            var key = (finishedId, selected, packagingId);
            if (!factorCache.TryGetValue(key, out var factor))
            {
                var result = RawRequirementEngine.Calculate(db, finishedId, 1, selected, packagingId);
                factor = result.IsConfigured ? (double)result.RawKgPerFinishedKg : 0;
                factorCache[key] = factor;
            }
            return factor;
        };
        row.RawProductId = rawId;
        if (rawId == null) row.ReloadFinishedProducts();
    }

    private static void RefreshDisplayedRawFactor(PlanRowUi row, DatesErpDbContext db)
    {
        if (row == null) return;
        int? rawId = row.RawProductId;
        if (rawId == null && row.LotId is int lotId)
            rawId = db.Lots.AsNoTracking().Where(l => l.Id == lotId).Select(l => (int?)l.ProductId).FirstOrDefault();
        var result = row.ProductId > 0
            ? RawRequirementEngine.Calculate(db, row.ProductId, 1, rawId, row.PackId)
            : null;
        row.RawKgPerFinishedKg = result?.IsConfigured == true ? (double)result.RawKgPerFinishedKg : 0;
        row.RawFactorPackagingTypeId = row.PackId;
    }

    private void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        if (_locked) { AppContainer.Get<DialogService>().Error("الخطة معتمدة ومقفلة — لا حذف بنود بعد الاعتماد."); return; }
        if (sender is Button b && b.Tag is PlanRowUi r1) _rows.Remove(r1);
        else if (sender is Button b2 && b2.DataContext is PlanRowUi r2) _rows.Remove(r2);
        EnsureEmptyRow();
    }

    // ═══════════════════ إلغاء صفوف الإضافة المباشرة أسفل الخطة (1.50.78) — الإنزال حصراً من المنبثق ═══════════════════
    private void AddEmptyRow_Click(object sender, RoutedEventArgs e)
    {
        if (_locked) return;
        PickLot_Click(sender, e);
    }

    private void AddEmptyRow()
    {
        // §1.50.78: أُلغيت حقول إضافة الأصناف أسفل شاشة الخطط؛ لا تُنشأ أي صفوف فارغة، والاختيار حصراً عبر شاشة اختيار الأصناف المنبثقة.
        Renumber();
        UpdateTotals();
    }

    private void EnsureEmptyRow()
    {
        // §1.50.78: تنظيف أي صف فارغ متبقٍ بدلاً من إنشاء صف فارغ جديد.
        for (int i = _rows.Count - 1; i >= 0; i--)
        {
            if (_rows[i].LotId == null && _rows[i].ProductId == 0)
                _rows.RemoveAt(i);
        }
        Renumber();
        UpdateTotals();
    }

    private void CustomerName_Click(object sender, RoutedEventArgs e)
    {
        if (_locked) return;
        if (sender is Button b && b.Tag is PlanRowUi row) OpenLotPicker(row);
    }

    private void LotCode_Click(object sender, RoutedEventArgs e)
    {
        if (_locked) return;
        if (sender is Button b && b.Tag is PlanRowUi row) OpenLotPicker(row);
    }

    private void ProductName_Click(object sender, RoutedEventArgs e)
    {
        if (_locked) return;
        if (sender is Button b && b.Tag is PlanRowUi row)
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            int? rawId = row.RawProductId;
            if (rawId == null && row.LotId is int lotId)
                rawId = db.Lots.AsNoTracking().Where(l => l.Id == lotId).Select(l => (int?)l.ProductId).FirstOrDefault();
            if (rawId is not int linkedRawId)
            {
                AppContainer.Get<DialogService>().Error("حدد الصنف الخام أو الدفعة أولاً؛ قائمة الصنف التام تعرض فقط الأصناف المرتبطة بهذا الخام في إعدادات الأصناف.");
                return;
            }
            var linkedProducts = scope.ServiceProvider.GetRequiredService<IPlanningService>().GetFinishedProductsForRaw(linkedRawId);
            if (linkedProducts.Count == 0)
            {
                AppContainer.Get<DialogService>().Error("لا يوجد صنف تام نشط مرتبط بهذا الخام. راجع مصادر الخام في إعدادات الصنف.");
                return;
            }
            // يتغير نطاق الاختيار حسب الخام الرسمي؛ الربط لا يتغير باختلاف عبوة التام.
            var dlg = new Views.ProductPickerWindow("", "Finished", linkedProducts.Select(p => p.Id)) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true && dlg.SelectedProduct != null)
            {
                var p = dlg.SelectedProduct;
                row.ProductId = p.Id;
                row.ProductName = p.ProductNameAr;
                row.PackId = p.DefaultPackagingTypeId;
                row.CartonWeight = UnitsPolicy.CartonWeight(db, p.Id, row.PackId);
                row.UnitDisplay = UnitWithCartonWeight(p.UnitOfMeasure, row.CartonWeight);
                if (row.CartonWeight <= 0) { AppContainer.Get<DialogService>().Error($"وزن الكرتون غير معرف للصنف «{p.ProductNameAr}» — عرّفه في بطاقة الصنف أو العبوة الافتراضية قبل الإضافة."); return; }
                if (row.Cartons > 0) row.QtyKg = Math.Round(row.Cartons * row.CartonWeight, 1);
                RefreshDisplayedRawFactor(row, db);
                RowsGrid.Items.Refresh();
                EnsureEmptyRow();
            }
        }
    }

    private void OpenLotPicker(PlanRowUi row)
    {
        try
        {
            var currentIds = _rows.Where(r => r.LotId != null).Select(r => r.LotId.Value).ToList();
            var dlg = new Views.LotPickerWindow(currentIds) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true && dlg.SelectedLot != null)
            {
                var lot = dlg.SelectedLot;
                row.LotId = lot.LotId;
                row.LotCode = lot.LotCode;
                row.CustomerId = lot.CustomerId;
                row.CustomerName = lot.CustomerName ?? "—";
                row.ShipmentId = lot.ShipmentId;
                row.ShipmentNo = lot.ShipmentNo;
                row.WarehouseId = lot.WarehouseId;
                row.WarehouseName = lot.WarehouseName ?? (lot.WarehouseId != null ? $"مخزن {lot.WarehouseId}" : "غير محدد");
                row.RawProductId = lot.ProductId;
                row.RawName = lot.ProductName;
                row.SourceQtyKg = lot.RemainingKg;
                row.SourceUnit = lot.ReceiptUnit ?? "كجم";
                // إذا لم يكن الصنف التام محدداً، اقترح نفس الخام أو أول تام مرتبط
                if (row.ProductId == 0)
                {
                    row.ProductId = lot.ProductId;
                    row.ProductName = lot.ProductName ?? "—";
                    row.UnitDisplay = lot.ReceiptUnit ?? "كجم";
                }
                using (var scope = AppContainer.NewScope())
                    RefreshDisplayedRawFactor(row, scope.ServiceProvider.GetRequiredService<DatesErpDbContext>());
                row.QtyKg = lot.AvailableForDateKg > 0 ? lot.AvailableForDateKg : lot.RemainingKg;
                if (row.CartonWeight > 0) row.Cartons = (int)Math.Ceiling(row.QtyKg / row.CartonWeight);
                RowsGrid.Items.Refresh();
                UpdateTotals();
                EnsureEmptyRow();
            }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.PickLotInline"); }
    }

    private void RowsGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
        // لا حاجة حالياً — لكن يبقي البنية لتعيين قوائم الورديات/الخطوط مستقبلاً
    }


    private void Renumber()
    {
        int n = 1;
        foreach (var r in _rows) { r.No = n; r.Priority = n; n++; }
    }

    // §1.50.57 — أزرار المدد السريعة أزيلت بناءً على طلب المستخدم (الاكتفاء بتحديد التاريخ). تبقى الدالة للتوافق لكن لا تُستدعى.
    private void Duration_Click(object sender, RoutedEventArgs e) { }

    // ══════════ شريط طاقة الوردية ══════════

    private void CapacityInputs_Changed(object sender, EventArgs e) => UpdateCapacityBar();

    /// <summary>
    /// §إصلاح شامل لشريط الطاقة — كان:
    ///  • يأخذ معدل «صنف أول بند» فقط (والخطة متعددة الأصناف بمعدلات مختلفة)
    ///  • يتجاهل العبوة رغم أن الـ Backend يحسب الطاقة لكل عبوة
    ///  • يجمع كراتين كل الأيام ويقارنها بطاقة يوم واحد
    ///  • يتجاهل وردية كل بند
    ///  • يفبرك rate=500 عند غياب التعريف
    ///  • يبتلع الأخطاء بـ catch { }
    /// الآن: يُحسب لكل (يوم × وردية) بمعدل كل صنف وعبوته — نفس منطق EnsureSlotCapacity في الـ Backend.
    /// </summary>
    // ══════════ الحفظ وسير الاعتماد ══════════

    /// <summary>§B58: تحرير خلية (كراتين/تاريخ/وردية/خط) يُحدّث الطاقة والعدادات فوراً.</summary>
    private void RowUi_Changed(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlanRowUi.CartonsText) or nameof(PlanRowUi.Date) or nameof(PlanRowUi.DateValue)
            or nameof(PlanRowUi.ShiftId) or nameof(PlanRowUi.LineId) or nameof(PlanRowUi.PackId) or nameof(PlanRowUi.QtyKg)
            or nameof(PlanRowUi.QuantityError) or nameof(PlanRowUi.Cartons)
            or nameof(PlanRowUi.SourceMode) or nameof(PlanRowUi.SourceQtyKg) or nameof(PlanRowUi.SourceDisplay))
        { UpdateCapacityBar(); UpdateTotals(); }
    }

    /// <summary>§B58: عدادات الخطة (بنود/كراتين/وزن/عملاء) + تلميح الجدول الفارغ — كمخطط المرجع.</summary>
    private void UpdateTotals()
    {
        if (TotItemsBox == null) return;
        var valid = _rows.Where(r => r.LotId != null || r.ProductId != 0).ToList();
        TotItemsBox.Text = $"البنود: {valid.Count}";
        TotCartonsBox.Text = $"كراتين: {valid.Sum(r => r.Cartons):N0}";
        TotQtyBox.Text = $"وزن الإنتاج التام: {valid.Sum(r => r.QtyKg):N1} كجم";
        TotCustsBox.Text = $"عملاء: {valid.Select(r => r.CustomerId).Distinct().Count()}";
        GridHintPlan.Visibility = valid.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>§B58: «من استلام مباشر» — فتح نافذة الدفعات المتاحة بلا عميل محدد.</summary>
    private void DirectReceipt_Click(object sender, RoutedEventArgs e)
    { if (_locked) { AppContainer.Get<DialogService>().Error("الخطة معتمدة ومقفلة."); return; } OpenLotsEditor(null, null); }

    // §1.50.62 — قوالب ونسخ سندات (8)
    private void SaveAsTemplate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_currentPlanId == null) { AppContainer.Get<DialogService>().Error("احفظ الخطة أولاً قبل حفظها كقالب."); return; }
            var dlg = new Views.InputDialog("حفظ كقالب","اسم القالب:", $"قالب - {TitleBox.Text}") { Owner = System.Windows.Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<DatesErp.Core.Interfaces.Services.IPlanningService>();
            // Use PlanningService directly for template methods
            var ps = scope.ServiceProvider.GetRequiredService<DatesErp.Application.Services.PlanningService>();
            var r = ps.SaveAsTemplate(_currentPlanId.Value, dlg.Value);
            if (!r.Ok) AppContainer.Get<DialogService>().Error(r.Message);
            else AppContainer.Get<DialogService>().Info(r.Message);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.SaveAsTemplate"); }
    }
    private void LoadTemplate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var ps = scope.ServiceProvider.GetRequiredService<DatesErp.Application.Services.PlanningService>();
            var templates = ps.GetTemplates();
            if (templates.Count == 0) { AppContainer.Get<DialogService>().Info("لا توجد قوالب محفوظة بعد — احفظ خطة كقالب أولاً."); return; }
            var picker = new Views.TemplatePickerWindow(templates) { Owner = System.Windows.Window.GetWindow(this) };
            if (picker.ShowDialog() != true || picker.SelectedTemplateId == null) return;
            // إذا كانت خطة حالية مفتوحة، نسأل هل نستبدل البنود أم ننشئ جديدة
            if (AppContainer.Get<DialogService>().Confirm("هل تريد إنشاء خطة جديدة من القالب؟\nنعم = خطة جديدة، لا = استبدال بنود الخطة الحالية"))
            {
                var r = ps.CreateFromTemplate(picker.SelectedTemplateId.Value, $"من قالب {picker.SelectedTemplateName}");
                if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
                AppContainer.Get<DialogService>().Info(r.Message);
                // افتح الخطة الجديدة
                OpenPlan(r.Id);
            }
            else
            {
                // استبدال بنود الخطة الحالية ببنود القالب
                var tpl = templates.First(t => t.Id == picker.SelectedTemplateId.Value);
                // تحميل بنود القالب إلى الجدول الحالي
                RowsGrid.ItemsSource = null;
                _rows.Clear();
                // نحتاج إلى تحويل بنود القالب إلى PlanRowUi — نستخدم نفس منطق تحميل الخطة
                using var scope2 = AppContainer.NewScope();
                var db = scope2.ServiceProvider.GetRequiredService<DatesErp.Infrastructure.Persistence.DatesErpDbContext>();
                var tplFull = db.ProductionPlans.Include(p => p.Items).First(p => p.Id == tpl.Id);
                foreach (var it in tplFull.Items)
                {
                    // نستخدم نفس آلية إضافة صف يدوي مع تعبئة من المنتج/الدفعة
                    var prod = db.Products.AsNoTracking().FirstOrDefault(p => p.Id == it.ProductId);
                    if (prod == null) continue;
                    int? rawId = it.SelectedRawProductId;
                    if (rawId == null && it.LotId is int templateLotId)
                        rawId = db.Lots.AsNoTracking().Where(l => l.Id == templateLotId).Select(l => (int?)l.ProductId).FirstOrDefault();
                    rawId ??= prod.SourceProductId;
                    var formula = RawRequirementEngine.Calculate(db, prod.Id, 1, rawId, it.PackagingTypeId);
                    var row = new PlanRowUi
                    {
                        No = _rows.Count + 1,
                        ProductId = prod.Id,
                        ProductName = prod.ProductNameAr,
                        CartonsText = it.PlannedCartons.ToString(),
                        QtyKg = it.PlannedQtyKg,
                        LotId = it.LotId,
                        RawProductId = rawId,
                        RawKgPerFinishedKg = formula.IsConfigured ? (double)formula.RawKgPerFinishedKg : 0,
                        PackId = it.PackagingTypeId,
                        RawFactorPackagingTypeId = it.PackagingTypeId,
                        CustomerId = it.CustomerId
                    };
                    _rows.Add(row);
                }
                RowsGrid.ItemsSource = _rows;
                AppContainer.Get<DialogService>().Info($"تم تحميل {tplFull.Items.Count} بنداً من القالب {tpl.PlanTitle} إلى الخطة الحالية — احفظ الخطة.");
            }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.LoadTemplate"); }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_locked) { AppContainer.Get<DialogService>().Error("الخطة معتمدة ومقفلة."); return; }
            RowsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            RowsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            UpdateCapacityBar();
            if (string.IsNullOrWhiteSpace(TitleBox.Text)) { AppContainer.Get<DialogService>().Error("أدخل عنوان الخطة."); return; }
            // §1.50.67 FIX2: عند الضغط على صنف جديد يتشفر الحفظ — تجاهل الصفوف غير المكتملة (كراتين 0) عند الحفظ
            // مثل UpdateCapacityBar: فقط البنود المكتملة (هوية + كراتين>0) تُحفظ، الصف الجديد الفارغ لا يعطل الحفظ
            var allValid = _rows.Where(r => r.LotId != null || r.ProductId != 0).ToList();
            var validRows = allValid.Where(r => r.Cartons > 0 && int.TryParse(r.CartonsText, out var nn) && nn > 0).ToList();
            if (validRows.Count == 0)
            {
                if (allValid.Count > 0) { AppContainer.Get<DialogService>().Error("أكمل كمية الكراتين للبند الجديد (يجب أن تكون >0) أو احذفه — الصفوف الفارغة لا تُحفظ."); return; }
                AppContainer.Get<DialogService>().Error("أضف بنداً واحداً على الأقل — اختر دفعة من الجدول مباشرة."); return;
            }
            // فحص الطاقة لا يعطل الزر؛ بعد اكتمال البيانات يُعرض سببه هنا عند الضغط على حفظ.
            if (!_capacityValid) { AppContainer.Get<DialogService>().Error(RemainingBadge.Text); return; }
            // §B80: فرض تاريخ كل إنتاج — كل بند بتاريخ صالح داخل فترة الخطة (قبل الخلفية أيضاً)
            var perStart = (StartBox.SelectedDate ?? DateTime.Today).Date;
            var perEnd = (EndBox.SelectedDate ?? DateTime.Today).Date;
            foreach (var rowD in validRows)
            {
                if (!Core.Common.UiFormat.TryParseDate(rowD.Date, out var rdD))
                { AppContainer.Get<DialogService>().Error($"البند ({rowD.No}) «{rowD.ProductName}» بلا تاريخ إنتاج — حدّد تاريخ كل بند في عمود «تاريخ الإنتاج»."); return; }
                if (rdD.Date < perStart || rdD.Date > perEnd)
                { AppContainer.Get<DialogService>().Error($"تاريخ البند ({rowD.No}) «{rowD.ProductName}» ({rowD.Date}) خارج فترة الخطة ({perStart:dd/MM/yyyy} ← {perEnd:dd/MM/yyyy})."); return; }
            }

            string ptype = TypeBox.SelectedIndex switch { 0 => "Daily", 1 => "Weekly", 2 => "Monthly", _ => "Period" };
            // §B75: النطاق والعميل المحدد يُحفظان في رأس الخطة
            string scopeMode = SingleRadio.IsChecked == true ? "Single" : "Multi";
            int? singleCustId = scopeMode == "Single"
                ? (SingleCustBox.SelectedItem as DatesErp.Core.Domain.Entities.Customer)?.Id
                : null;
            using var scope = AppContainer.NewScope();
            var svc = (IPlanningService)scope.ServiceProvider.GetService(typeof(IPlanningService));
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var rawNeedByRow = validRows.ToDictionary(row => row, row =>
            {
                double rowRaw = row.RawRequiredKg > 0.001 ? row.RawRequiredKg : row.QtyKg;
                int? rawId = row.RawProductId;
                if (rawId == null && row.LotId is int sourceLotId)
                    rawId = db.Lots.AsNoTracking().Where(l => l.Id == sourceLotId)
                        .Select(l => (int?)l.ProductId).FirstOrDefault();
                var rawResult = RawRequirementEngine.Calculate(db, row.ProductId, row.QtyKg, rawId, row.PackId, rowRaw);
                return rawResult.IsConfigured ? (double)rawResult.RawRequiredQtyKg : rowRaw;
            });
            // الصفوف المتعددة لنفس العميل/الصنف/اليوم تصبح بند خطة واحداً مع عدة
            // تخصيصات خام؛ هكذا لا يضيع تقسيم 10 طن من المخزن 1 و10 من المخزن 2.
            var itemsDto = validRows
                .GroupBy(row => new
                {
                    row.CustomerId, row.ProductId, row.PackId, row.Date, row.ShiftId, row.LineId,
                    row.RawProductId, SourceType = row.LotId != null ? "FromReceiving" : "Manual"
                })
                .Select(group =>
                {
                    var first = group.First();
                    bool oneSource = group.Select(x => x.LotId).Distinct().Count() == 1;
                    double groupRawNeed = Math.Round(group.Sum(x => rawNeedByRow[x]), 3);
                    string srcUnit = oneSource && first.LotId != null
                        ? (string.IsNullOrWhiteSpace(first.SourceUnit) ? first.SourceMode : first.SourceUnit)
                        : null;
                    double srcUw = srcUnit != null
                        ? (string.Equals(srcUnit, "كجم", StringComparison.OrdinalIgnoreCase)
                            ? 1.0
                            : (first.SourceUnitWeightKg > 0 ? first.SourceUnitWeightKg : (first.LotReceiptUnitWeightKg > 0 ? first.LotReceiptUnitWeightKg : 1.0)))
                        : 0;
                    double srcQtyKg = oneSource && first.LotId != null ? groupRawNeed : 0;
                    double srcQtyUnits = srcUnit == null
                        ? 0
                        : (string.Equals(srcUnit, "كجم", StringComparison.OrdinalIgnoreCase)
                            ? Math.Round(srcQtyKg, 2)
                            : (srcUw > 0 ? Math.Round(srcQtyKg / srcUw, 2) : Math.Round(first.SourceQtyInUnit, 2)));

                    var builtAllocations = new List<PlanItemAllocationDto>();
                    foreach (var x in group)
                    {
                        double needKg = rawNeedByRow[x];
                        if (needKg <= 0.001) continue;
                        if (x.WarehouseId == null && x.PreservedAllocations != null && x.PreservedAllocations.Count > 0)
                        {
                            double origSum = x.PreservedAllocations.Sum(a => a.AllocatedQtyKg);
                            double ratio = origSum > 0.001 ? needKg / origSum : 1.0;
                            double allocatedSoFar = 0;
                            for (int ai = 0; ai < x.PreservedAllocations.Count; ai++)
                            {
                                var pa = x.PreservedAllocations[ai];
                                double part = ai == x.PreservedAllocations.Count - 1
                                    ? Math.Max(0, Math.Round(needKg - allocatedSoFar, 3))
                                    : Math.Round(pa.AllocatedQtyKg * ratio, 3);
                                allocatedSoFar += part;
                                if (part > 0.001)
                                {
                                    builtAllocations.Add(new PlanItemAllocationDto
                                    {
                                        WarehouseId = pa.WarehouseId,
                                        LotId = pa.LotId,
                                        CustomerId = pa.CustomerId ?? x.CustomerId,
                                        AllocatedQtyKg = part
                                    });
                                }
                            }
                        }
                        else if (x.LotId != null)
                        {
                            builtAllocations.Add(new PlanItemAllocationDto
                            {
                                WarehouseId = x.WarehouseId,
                                LotId = x.LotId.Value,
                                CustomerId = x.CustomerId,
                                AllocatedQtyKg = needKg
                            });
                        }
                    }

                    return new PlanItemDto
                    {
                        SourceType = group.Key.SourceType,
                        LotId = first.LotId,
                        SelectedRawProductId = first.RawProductId,
                        ShipmentId = oneSource ? first.ShipmentId : null,
                        CustomerId = first.CustomerId,
                        ProductId = first.ProductId,
                        PackagingTypeId = first.PackId,
                        PlannedQtyKg = group.Sum(x => x.QtyKg),
                        PlannedCartons = group.Sum(x => x.Cartons),
                        RawRequiredQtyKg = groupRawNeed,
                        ScheduledDate = first.Date,
                        SuggestedShiftId = first.ShiftId,
                        SuggestedLineId = first.LineId,
                        PriorityNo = group.Min(x => x.Priority),
                        SourceUnit = srcUnit,
                        SourceQtyInUnit = srcQtyUnits,
                        SourceUnitWeightKg = srcUw,
                        SourceQtyKg = srcQtyKg,
                        // قرار مدير التخطيط محفوظ صراحةً لكل مخزن/دفعة/عميل/كمية.
                        Allocations = builtAllocations
                    };
                }).ToList();

            // §تعديل خطة قائمة (مسودة) بدل إنشاء نسخة مكررة — الحفظ يعمل كحفظ وتحديث معاً
            OpResult r = _currentPlanId.HasValue && _currentPlanId.Value > 0
                ? svc.UpdatePlan(_currentPlanId.Value, TitleBox.Text, ptype,
                    (StartBox.SelectedDate ?? DateTime.Today).ToString("dd/MM/yyyy"),
                    (EndBox.SelectedDate ?? DateTime.Today).ToString("dd/MM/yyyy"),
                    SelectedShiftId(), SelectedLineId(), itemsDto, NotesBox.Text, scopeMode, singleCustId)
                : svc.SavePlan(TitleBox.Text, ptype,
                    (StartBox.SelectedDate ?? DateTime.Today).ToString("dd/MM/yyyy"),
                    (EndBox.SelectedDate ?? DateTime.Today).ToString("dd/MM/yyyy"),
                    SelectedShiftId(), SelectedLineId(), itemsDto, NotesBox.Text, scopeMode, singleCustId);

            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            _currentPlanId = r.Id;
            CodeBox.Text = r.DocumentNumber;
            FillPlanMeta();
            string nextStep = r.Message.Contains("غياب معامل تحويل خام", StringComparison.Ordinal)
                ? "\nالخطة معلقة — لا ترسلها للاعتماد حتى تسجيل المعامل الرسمي ثم إعادة حفظها."
                : "\nأرسلها للاعتماد للمدير العام عند الجاهزية.";
            AppContainer.Get<DialogService>().Info(r.Message + "\nحُفظت البنود كما أدخلتها (عميل/شحنة/دفعة/صنف/عبوة)." + nextStep);
            SetStatusUI("Draft");
            RefreshPlansList();
            // §B108: إعادة تحميل البنود من القاعدة بعد الحفظ — الحفظ يعيد بناء البنود،
            // فمعرفاتها في الذاكرة تصبح قديمة، و«تعديل البند» يعتمد عليها.
            if (_currentPlanId.HasValue && _currentPlanId.Value > 0) OpenPlan(_currentPlanId.Value);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.Save"); }
    }

    private bool HasPendingRawRequirement() => false;

    private static string PendingRawActionMessage
        => "راجع كميات الإنتاج واحتياج الخام قبل الإرسال للاعتماد.";

    private void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (HasPendingRawRequirement())
        { AppContainer.Get<DialogService>().Error(PendingRawActionMessage); return; }
        if (_currentPlanId == null || _currentPlanId.Value == 0) { AppContainer.Get<DialogService>().Error("احفظ الخطة أولاً."); return; }
        using var scope = AppContainer.NewScope();
        var svc = (IPlanningService)scope.ServiceProvider.GetService(typeof(IPlanningService));
        var r = svc.SubmitPlan(_currentPlanId.Value);
        if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
        AppContainer.Get<DialogService>().Info(r.Message);
        SetStatusUI("UnderApproval");
        RefreshPlansList();
    }

    /// <summary>§إصلاح: زر «اعتماد الخطة» داخل الشبكة — يحفظ أولاً إن لزم ثم يعتمد.</summary>
    private void ApproveAction_Click(object sender, RoutedEventArgs e)
    {
        // §B58: «تعليق» = حفظ مسودة بلا اعتماد؛ «اعتماد» = حفظ ثم اعتماد/إرسال
        if (HoldRadio.IsChecked == true) { Save_Click(sender, e); return; }
        if (HasPendingRawRequirement())
        { AppContainer.Get<DialogService>().Error(PendingRawActionMessage); return; }
        ApproveAction_ClickCore(sender, e);
    }

    /// <summary>
    /// §1.50.71 P1: بصمة حالة الخطة كما ستُحفظ (رأس + البنود الصالحة فقط) — تكشف تعديلات
    /// غير محفوظة بعد آخر حفظ/تحميل قبل الاعتماد (حارس فقدان البيانات). الصفوف الفارغة
    /// (بلا هوية أو كراتين=0) مستثناة لأن Save_Click يتجاهلها أصلاً فلا تُعد «تعديلاً».
    /// </summary>
    private string PlanFingerprint()
    {
        var parts = new List<string>
        {
            TitleBox?.Text ?? "",
            (StartBox?.SelectedDate ?? DateTime.MinValue).ToString("yyyy-MM-dd"),
            (EndBox?.SelectedDate ?? DateTime.MinValue).ToString("yyyy-MM-dd"),
            TypeBox?.SelectedIndex.ToString() ?? "",
            SingleRadio?.IsChecked == true ? "Single" : "Multi",
            NotesBox?.Text ?? ""
        };
        foreach (var r in _rows)
        {
            if (r.LotId == null && r.ProductId == 0) continue;   // بلا هوية — لن يُحفظ
            if (!(r.Cartons > 0 && int.TryParse(r.CartonsText, out var nn) && nn > 0)) continue; // كراتين صفر — Save_Click يتخطاها
            string allocSig = r.PreservedAllocations != null && r.PreservedAllocations.Count > 0
                ? string.Join(",", r.PreservedAllocations.Select(a => $"{a.WarehouseId}:{a.LotId}:{a.AllocatedQtyKg:F2}"))
                : "";
            parts.Add($"R|{r.LotId}|{r.WarehouseId}|{r.RawProductId}|{r.ShipmentId}|{r.CustomerId}|{r.ProductId}|{r.PackId}|" +
                      $"{nn}|{r.QtyKg:F3}|{r.RawRequiredKg:F3}|{r.Date}|{r.ShiftId}|{r.LineId}|{r.Priority}|{r.SourceUnit}|" +
                      $"{r.SourceQtyInUnit:F3}|{r.SourceUnitWeightKg:F3}|{r.SourceQtyKg:F3}|{allocSig}");
        }
        return string.Join("‡", parts);
    }

    private void ApproveAction_ClickCore(object sender, RoutedEventArgs e)
    {
        if (_locked) { AppContainer.Get<DialogService>().Error("الخطة معتمدة ومقفلة."); return; }
        if (_currentPlanId == null || _currentPlanId.Value == 0)
        {
            if (!AppContainer.Get<DialogService>().Confirm("الخطة غير محفوظة بعد. هل تريد حفظها ثم اعتمادها؟")) return;
            Save_Click(sender, e);
            if (_currentPlanId == null || _currentPlanId.Value == 0) return;   // الحفظ فشل — رسالة الخطأ ظهرت بالفعل
        }
        Approve();
    }

    private void Approve()
    {
        try
        {
            if (_currentPlanId == null || _currentPlanId.Value == 0) { AppContainer.Get<DialogService>().Error("احفظ الخطة أولاً أو اختر خطة من السجل."); return; }
            // §1.50.71 P1: خطة محفوظة لكن فيها تعديلات بعد آخر حفظ (رأس/فترة/بنود) → احفظها قبل الاعتماد.
            // هنا (في Approve نفسه) لا في زر بعينه: يغلق مساري الاعتماد (زر الشاشة وشريط الأدوات).
            if (PlanFingerprint() != _savedPlanFingerprint)
            {
                if (!AppContainer.Get<DialogService>().Confirm("في الخطة تعديلات غير محفوظة منذ آخر حفظ. ستُحفظ قبل الاعتماد — متابعة؟")) return;
                Save_Click(null, null);
                if (PlanFingerprint() != _savedPlanFingerprint) return;   // الحفظ فشل (رسالة الخطأ ظهرت بالفعل)
            }
            if (!AppContainer.Get<DialogService>().Confirm("اعتماد الخطة رسمياً ونقلها لأوامر التشغيل؟")) return;
            using var scope = AppContainer.NewScope();
            var svc = (IPlanningService)scope.ServiceProvider.GetService(typeof(IPlanningService));
            var r = svc.ApprovePlan(_currentPlanId.Value);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            SetLocked(true);
            SetStatusUI("Approved");
            RefreshPlansList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.Approve"); }
    }

    private void ReturnForRevision_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlanId == null || _currentPlanId.Value == 0) return;
        var dlg = new Views.InputDialog("إعادة الخطة للمدير التنفيذي للتعديل", "سبب الإعادة / الملاحظات:") { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        using var scope = AppContainer.NewScope();
        var svc = (IPlanningService)scope.ServiceProvider.GetService(typeof(IPlanningService));
        var r = svc.ReturnPlan(_currentPlanId.Value, dlg.Value);
        if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
        AppContainer.Get<DialogService>().Info(r.Message);
        SetLocked(false);
        SetStatusUI("RevisionRequired");
        RefreshPlansList();
    }

    private void Unapprove()
    {
        if (_currentPlanId == null || _currentPlanId.Value == 0) return;
        if (!AppContainer.Get<DialogService>().Confirm("إلغاء الاعتماد وإعادة فتح الخطة للتعديل؟")) return;
        using var scope = AppContainer.NewScope();
        var svc = (IPlanningService)scope.ServiceProvider.GetService(typeof(IPlanningService));
        var r = svc.UnapprovePlan(_currentPlanId.Value);
        if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
        AppContainer.Get<DialogService>().Info(r.Message);
        SetLocked(false);
        SetStatusUI("Draft");
        RefreshPlansList();
    }

    private void DeletePlan()
    {
        if (_currentPlanId == null || _currentPlanId.Value == 0) { AppContainer.Get<DialogService>().Error("لا توجد خطة محددة."); return; }
        if (!AppContainer.Get<DialogService>().Confirm("حذف الخطة (المسودة)؟")) return;
        using var scope = AppContainer.NewScope();
        var svc = (IPlanningService)scope.ServiceProvider.GetService(typeof(IPlanningService));
        var r = svc.DeletePlan(_currentPlanId.Value);
        if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
        AppContainer.Get<DialogService>().Info(r.Message);
        NewPlan();
        RefreshPlansList();
    }

    /// <summary>
    /// §إصلاح — تراجع بوظيفتين حسب المعيار المعلن («جديد ← إفراغ؛ محفوظ ← إعادة آخر نسخة محفوظة — لا حذف أبداً»).
    /// كان يمسح البنود دائماً، فيفقد المستخدم خطة محفوظة من العرض بدل استعادتها.
    /// </summary>
    private void UndoInput()
    {
        if (_currentPlanId.HasValue && _currentPlanId.Value > 0)
        {
            OpenPlan(_currentPlanId.Value);   // استعادة آخر نسخة محفوظة
            AppContainer.Get<DialogService>().Info("أُعيدت آخر نسخة محفوظة من الخطة.");
            return;
        }
        _rows.Clear();
        NotesBox.Text = "";
        UpdateCapacityBar();
    }

    private void NewPlan()
    {
        _programmaticScope = true;
        try
        {
            _currentPlanId = 0;
            _rows.Clear();
            _planCustomers.Clear();
            try
            {
                using var _numScope = AppContainer.NewScope();
                var _num = _numScope.ServiceProvider.GetRequiredService<INumberingService>().Peek("PLAN");
                CodeBox.Text = _num;
            }
            catch { CodeBox.Text = "PLN-تلقائي"; }
            TitleBox.Text = "";
            NotesBox.Text = "";
            StartBox.SelectedDate = EndBox.SelectedDate = null;
            MultiRadio.IsChecked = true;
            SetLocked(false);
            SetStatusUI("Draft");
            TypeBox_Changed(null, null); // إعادة تطبيق قاعدة «اليومية = تاريخ واحد»
            UpdateCapacityBar();
            AddEmptyRow();
            _savedPlanFingerprint = PlanFingerprint();   // §1.50.71 P1: خط الأساس لخطة جديدة (فارغة)
        }
        finally { _programmaticScope = false; }
    }

    // ══════════ الحالة والقفل ══════════

    private void SetStatusUI(string status)
    {
        string text; System.Windows.Media.Color fg, bg, bd;
        switch (status)
        {
            case "Approved":
                text = "معتمدة ومجدولة 🟢 (مقفل 🔒)";
                fg = System.Windows.Media.Color.FromRgb(0x15, 0x80, 0x3D);
                bg = System.Windows.Media.Color.FromRgb(0xDC, 0xFC, 0xE7);
                bd = System.Windows.Media.Color.FromRgb(0x86, 0xEF, 0xAC);
                break;
            case "UnderApproval":
                text = "بانتظار اعتماد المدير العام ⏳";
                fg = System.Windows.Media.Color.FromRgb(0x92, 0x40, 0x0E);
                bg = System.Windows.Media.Color.FromRgb(0xFE, 0xF3, 0xC7);
                bd = System.Windows.Media.Color.FromRgb(0xFC, 0xD3, 0x4D);
                break;
            case "RevisionRequired":
                text = "معادة للتعديل من المدير العام ↩️";
                fg = System.Windows.Media.Color.FromRgb(0x99, 0x1B, 0x1B);
                bg = System.Windows.Media.Color.FromRgb(0xFE, 0xE2, 0xE2);
                bd = System.Windows.Media.Color.FromRgb(0xFC, 0xA5, 0xA5);
                break;
            default:
                text = "مسودة قيد الإعداد 📝";
                fg = System.Windows.Media.Color.FromRgb(0x03, 0x69, 0xA1);
                bg = System.Windows.Media.Color.FromRgb(0xE0, 0xF2, 0xFE);
                bd = System.Windows.Media.Color.FromRgb(0xBA, 0xE6, 0xFD);
                break;
        }
        StatusText.Text = text;
        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(fg);
        StatusBanner.Background = new System.Windows.Media.SolidColorBrush(bg);
        StatusBanner.BorderBrush = new System.Windows.Media.SolidColorBrush(bd);

        // مسار المعاملة
        SetStep(Step1, status is "Draft" or "RevisionRequired");
        SetStep(Step2, status == "UnderApproval" || status == "Approved");
        SetStep(Step3, status == "Approved");
        SetStep(Step4, false);

        SubmitBtn.Visibility = status is "Draft" or "RevisionRequired" ? Visibility.Visible : Visibility.Collapsed;
        ReturnBtn.Visibility = status == "UnderApproval" ? Visibility.Visible : Visibility.Collapsed;
        if (_toolbar != null && _toolbar.UnapproveBtn != null)
            _toolbar.UnapproveBtn.Visibility = status == "Approved" ? Visibility.Visible : Visibility.Collapsed;
        // §إصلاح: زر الاعتماد يظهر ما دامت الخطة غير معتمدة — كان مخفياً دائماً فلا اعتماد من الشاشة
        // §1.50.73: زر الاعتماد في شريط الأدوات يبقى مخفياً دائماً — الاعتماد فعل واحد
        // في مكان واحد: زر «اعتماد الخطة» في بطاقة الأزرار (يدير مساري اعتماد/تعليق
        // عبر الراديو + الحارس). شريط الأدوات كان يعرض نسخة ثانية منه = زران متكرران.

        if (_toolbar != null && _toolbar.DeleteBtn != null)
            _toolbar.DeleteBtn.Visibility = status == "Approved" ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void SetStep(Border step, bool active)
    {
        step.Background = new System.Windows.Media.SolidColorBrush(active
            ? System.Windows.Media.Color.FromRgb(0xDC, 0xFC, 0xE7)
            : System.Windows.Media.Color.FromRgb(0xE2, 0xE8, 0xF0));
        step.BorderBrush = new System.Windows.Media.SolidColorBrush(active
            ? System.Windows.Media.Color.FromRgb(0x86, 0xEF, 0xAC)
            : System.Windows.Media.Colors.Transparent);
        if (step.Child is TextBlock tb)
            tb.Foreground = new System.Windows.Media.SolidColorBrush(active
                ? System.Windows.Media.Color.FromRgb(0x15, 0x80, 0x3D)
                : System.Windows.Media.Color.FromRgb(0x64, 0x74, 0x8B));
    }

    private string PlanTypeKey() => TypeBox.SelectedIndex switch { 0 => "Daily", 1 => "Weekly", 2 => "Monthly", _ => "Period" };

    /// <summary>§B75: عرض منشئ الخطة ومعتمدها وتاريخيهما في الرأس — بيانات محفوظة تُقرأ حية.</summary>
    private void FillPlanMeta()
    {
        if (PlanMetaBox == null) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            if (!_currentPlanId.HasValue) { PlanMetaBox.Text = "خطة جديدة — لم تُحفظ بعد · أنشأها: — · اعتمدها: —"; return; }
            var plan = db.ProductionPlans.AsNoTracking().FirstOrDefault(pl => pl.Id == _currentPlanId.Value);
            if (plan == null) { PlanMetaBox.Text = "خطة جديدة — لم تُحفظ بعد · أنشأها: — · اعتمدها: —"; return; }
            string NameOf(int? uid) => uid == null ? "—"
                : db.Users.AsNoTracking().Where(u => u.Id == uid).Select(u => u.FullName).FirstOrDefault() ?? "—";
            PlanMetaBox.Text = $"أنشأها: {NameOf(plan.CreatedBy)} في {plan.CreatedDate:dd/MM/yyyy HH:mm}" +
                               (plan.IsApproved ? $" · اعتمدها: {NameOf(plan.ApprovedBy)} في {plan.ApprovedDate:dd/MM/yyyy HH:mm}" : " · لم تُعتمد بعد");
        }
        catch { }
    }

    private void SetLocked(bool locked)
    {
        _locked = locked;
        StartBox.IsEnabled = !locked;
        EndBox.IsEnabled = !locked;
        TypeBox.IsEnabled = !locked;
        TitleBox.IsEnabled = !locked;
        NotesBox.IsEnabled = !locked;
        ShiftBox.IsEnabled = !locked;
        LineBox.IsEnabled = !locked;
        MultiRadio.IsEnabled = !locked;
        SingleRadio.IsEnabled = !locked;
        SingleCustBox.IsEnabled = !locked;
        RowsGrid.IsReadOnly = locked;
        if (InsertActionsPanel != null)
        {
            InsertActionsPanel.IsEnabled = true;
            foreach (var actionButton in InsertActionsPanel.Children.OfType<Button>())
                actionButton.IsEnabled = !locked || ReferenceEquals(actionButton, FairDistributionBtn);
        }
        if (FairDistributionBtn != null) FairDistributionBtn.IsEnabled = !locked || _currentPlanId is > 0;
        if (EditRowBtn != null) EditRowBtn.IsEnabled = !locked;
        if (HoldRadio != null) HoldRadio.IsEnabled = !locked;
        if (ApproveRadio != null) ApproveRadio.IsEnabled = !locked;
        if (_toolbar != null)
        {
            // §FIX 1.50.73: القفل وحده يتحكم في تفعيل الحفظ؛ التحقق يتم عند الضغط.
            if (_toolbar.SaveBtn != null) _toolbar.SaveBtn.IsEnabled = !locked;
            if (_toolbar.NewBtn != null) _toolbar.NewBtn.IsEnabled = true; // خطة جديدة دائماً متاحة
            if (_toolbar.ApproveBtn != null) _toolbar.ApproveBtn.IsEnabled = !locked;
            if (_toolbar.DeleteBtn != null) _toolbar.DeleteBtn.IsEnabled = !locked;
        }
        // §FIX 1.50.73: نفس القاعدة لزر الحفظ الرئيسي في الشاشة.
        if (SaveActionBtn != null) SaveActionBtn.IsEnabled = !locked;
        if (ApproveActionBtn != null) ApproveActionBtn.IsEnabled = !locked;
        if (SubmitBtn != null) SubmitBtn.IsEnabled = !locked;
    }

    // ══════════ التنقل والسجل ══════════

    private void Nav(int dir)
    {
        if (_planIds.Count == 0) return;
        int idx = _currentPlanId.HasValue ? _planIds.IndexOf(_currentPlanId.Value) : -1;
        idx = dir switch { 0 => 0, int.MaxValue => _planIds.Count - 1, _ => Math.Clamp(idx + dir, 0, _planIds.Count - 1) };
        OpenPlan(_planIds[idx]);
    }

    private void OpenPlan(int id)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var plan = db.ProductionPlans.Include(p => p.Items).ThenInclude(i => i.Allocations).FirstOrDefault(p => p.Id == id);
            if (plan == null) return;
            _currentPlanId = plan.Id;
            CodeBox.Text = plan.DocumentNumber;
            TitleBox.Text = plan.PlanTitle;
            NotesBox.Text = plan.Notes;
            StartBox.SelectedDate = plan.StartDate;
            EndBox.SelectedDate = plan.EndDate;
            TypeBox.SelectedIndex = plan.PlanType switch { "Daily" => 0, "Weekly" => 1, "Monthly" => 2, _ => 3 };
            if (plan.ShiftId != null) { int si = _shiftIds.IndexOf(plan.ShiftId.Value); if (si >= 0) ShiftBox.SelectedIndex = si; }
            if (plan.LineId != null) { int li = _lineIds.IndexOf(plan.LineId.Value); if (li >= 0) LineBox.SelectedIndex = li; }
            // §B75: استعادة نطاق التخطيط والعميل المحدد من الرأس
            // §B106.2 — الحارس _programmaticScope كان يُفعَّل لاحقاً في هذه الدالة فقط،
            // بينما إسنادُ SingleCustBox.SelectedIndex هنا يُطلق SelectionChanged فوراً،
            // فيستدعي SingleCust_Changed -> OpenLotsEditor: تُفتح نافذة «أصناف وشحنات
            // العميل» فوق الخطة بمجرد النقر المزدوج عليها لعرضها. الفتح استعراضٌ لا
            // إدخال، فالحارس يلزم حول الإسناد نفسه.
            _programmaticScope = true;
            try
            {
                if (plan.ScopeMode == "Single") SingleRadio.IsChecked = true; else MultiRadio.IsChecked = true;
                if (plan.SingleCustomerId != null)
                    for (int ci = 0; ci < SingleCustBox.Items.Count; ci++)
                        if ((SingleCustBox.Items[ci] as DatesErp.Core.Domain.Entities.Customer)?.Id == plan.SingleCustomerId) { SingleCustBox.SelectedIndex = ci; break; }
            }
            finally { _programmaticScope = false; }
            FillPlanMeta();

            _rows.Clear();
            var planningSvc = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            foreach (var it in plan.Items.OrderBy(i => i.PriorityNo))
            {
                var allocations = it.Allocations?.OrderBy(a => a.WarehouseId).ToList() ?? new List<ProductionPlanItemAllocation>();
                int? singleWhId = allocations.Count == 1 ? allocations[0].WarehouseId : (int?)null;
                int? rawProductId = allocations.Count > 0
                    ? db.Lots.AsNoTracking().Where(l => l.Id == allocations[0].LotId).Select(l => (int?)l.ProductId).FirstOrDefault()
                    : null;
                if (rawProductId == null && it.LotId is int sourceLotId)
                    rawProductId = db.Lots.AsNoTracking().Where(l => l.Id == sourceLotId)
                        .Select(l => (int?)l.ProductId).FirstOrDefault();
                rawProductId ??= it.SelectedRawProductId;
                var rawFormula = RawRequirementEngine.Calculate(db, it.ProductId, 1, rawProductId, it.PackagingTypeId);
                var finishedProduct = db.Products.AsNoTracking().FirstOrDefault(p => p.Id == it.ProductId);
                double cartonWeight = UnitsPolicy.CartonWeight(db, it.ProductId, it.PackagingTypeId);
                var allocationSummary = string.Join("، ", allocations.Select(a =>
                {
                    var name = db.Warehouses.Where(w => w.Id == a.WarehouseId).Select(w => w.WarehouseNameAr).FirstOrDefault() ?? $"مخزن {a.WarehouseId}";
                    return allocations.Count == 1 ? name : $"{name}: {a.AllocatedQtyKg:N1} كجم";
                }));

                double onHandKg = 0, reservedKg = 0, availableKg = 0;
                ShipmentQuantityContext lotCtx = null;
                if (it.LotId is int lid)
                {
                    var engine = new RawPlanningAvailabilityEngine(db);
                    IEnumerable<int> targetWhIds = singleWhId != null
                        ? new[] { singleWhId.Value }
                        : UnitsPolicy.GetRawSourceWarehouseIds(db);
                    foreach (var wid in targetWhIds)
                    {
                        var snap = engine.ForLot(wid, lid, excludePlanId: plan.Id, asOfDate: it.ScheduledDate);
                        onHandKg += snap.ActualStockKg;
                        reservedKg += snap.PlannedReservationKg + snap.OtherCommitmentsKg;
                        availableKg += snap.AvailableForPlanningKg;
                    }
                    lotCtx = planningSvc.GetShipmentQuantityContext(lid, singleWhId, plan.Id, it.ScheduledDate);
                }

                var loadedRow = new PlanRowUi
                {
                    ItemId = it.Id,   // §B108: يتيح تعديل البند المحفوظ من الجدول الرئيسي
                    CustomerId = it.CustomerId,
                    CustomerName = db.Customers.Where(c => c.Id == it.CustomerId).Select(c => c.CustomerName).FirstOrDefault() ?? "—",
                    ShipmentId = it.ShipmentId,
                    ShipmentNo = db.Shipments.Where(s => s.Id == it.ShipmentId).Select(s => s.DocumentNumber).FirstOrDefault() ?? "—",
                    WarehouseId = singleWhId,
                    WarehouseName = allocations.Count > 0 ? allocationSummary : db.Shipments.Where(s => s.Id == it.ShipmentId)
                        .Join(db.Warehouses, s => s.ReceivingWarehouseId, w => (int?)w.Id, (s, w) => w.WarehouseNameAr).FirstOrDefault() ?? "غير محدد",
                    LotId = it.LotId,
                    RawProductId = rawProductId,
                    RawKgPerFinishedKg = rawFormula.IsConfigured ? (double)rawFormula.RawKgPerFinishedKg : 1.0,
                    SavedRawRequiredKg = (double)it.RawRequiredQtyKg,
                    OnHandKg = onHandKg,
                    ReservedKg = reservedKg,
                    AvailableKg = availableKg,
                    AllocationSelectedQtyKg = allocations.Sum(a => a.AllocatedQtyKg),
                    PreservedAllocations = allocations.Select(a => new PlanItemAllocationDto
                    {
                        WarehouseId = a.WarehouseId,
                        LotId = a.LotId,
                        CustomerId = a.CustomerId,
                        AllocatedQtyKg = a.AllocatedQtyKg
                    }).ToList(),
                    LotCode = db.Lots.Where(l => l.Id == it.LotId).Select(l => l.LotCode).FirstOrDefault() ?? "—",
                    RawName = db.Lots.Where(l => l.Id == it.LotId).Join(db.Products, l => l.ProductId, p => p.Id, (l, p) => p.ProductNameAr).FirstOrDefault() ?? "—",
                    ProductId = it.ProductId,
                    ProductName = finishedProduct?.ProductNameAr ?? "-",
                    PackId = it.PackagingTypeId,
                    RawFactorPackagingTypeId = it.PackagingTypeId,
                    PackName = db.PackagingTypes.Where(p => p.Id == it.PackagingTypeId).Select(p => p.PackageNameAr).FirstOrDefault() ?? "-",
                    UnitDisplay = UnitWithCartonWeight(finishedProduct?.UnitOfMeasure ?? "—", cartonWeight),
                    CartonWeight = cartonWeight,
                    QtyKg = it.PlannedQtyKg,
                    Cartons = it.PlannedCartons,
                    DateValue = it.ScheduledDate,
                    ShiftId = it.SuggestedShiftId ?? 1,
                    ShiftName = db.Shifts.Where(s => s.Id == (it.SuggestedShiftId ?? 1)).Select(s => s.ShiftNameAr).FirstOrDefault() ?? "-",
                    LineId = it.SuggestedLineId ?? 1,
                    LineName = db.ProductionLines.AsNoTracking().Where(x => x.Id == (it.SuggestedLineId ?? 1)).Select(x => x.LineNameAr).FirstOrDefault() ?? "-",
                    Priority = it.PriorityNo,
                    SourceUnit = it.SourceUnit ?? lotCtx?.ReceiptUnit ?? "كجم",
                    SourceQtyInUnit = it.SourceQtyInUnit,
                    SourceUnitWeightKg = it.SourceUnitWeightKg > 0 ? it.SourceUnitWeightKg : (lotCtx?.UnitWeightKg ?? 1.0),
                    SourceQtyKg = it.SourceQtyKg
                };
                loadedRow.ApplySavedRawRequirement((double)it.RawRequiredQtyKg);
                loadedRow.ConfigureReceiptUnitContext(
                    lotCtx?.ReceiptUnit ?? it.SourceUnit ?? "كجم",
                    lotCtx != null && lotCtx.UnitWeightKg > 0 ? lotCtx.UnitWeightKg : it.SourceUnitWeightKg,
                    it.SourceUnit);
                _rows.Add(loadedRow);
            }
            // نطاق الخطة: عميل واحد ← يُخفى عمود العميل (محفوظ في رأس النموذج) | عدة عملاء ← يظهر العمود
            // (الحارس يمنع الفتح التلقائي للنافذة أثناء الاسترجاع البرمجي)
            _programmaticScope = true;
            try
            {
                var custIds = plan.Items.Where(i => i.CustomerId != null).Select(i => i.CustomerId).Distinct().ToList();
                if (custIds.Count <= 1)
                {
                    SingleRadio.IsChecked = true;
                    if (custIds.Count == 1)
                    {
                        RefreshCustomerList(); // نضمن وجود القائمة قبل ضبط القيمة
                        SingleCustBox.SelectedValue = custIds[0];
                    }
                }
                else MultiRadio.IsChecked = true;
            }
            finally { _programmaticScope = false; }

            Renumber();
            RowsGrid.Items.Refresh();
            SetLocked(plan.IsApproved);
            SetStatusUI(plan.IsApproved ? "Approved" : plan.Status == "UnderApproval" ? "UnderApproval" : plan.Status == "RevisionRequired" ? "RevisionRequired" : "Draft");
            UpdateCapacityBar();
            if (!plan.IsApproved) EnsureEmptyRow();
            _savedPlanFingerprint = PlanFingerprint();   // §1.50.71 P1: خط الأساس بعد كل تحميل (ما في الشاشة = ما في القاعدة)
            // §توحيد الواجهات: عند فتح خطة من البحث، ابدأ العرض من أعلى النموذج ليكون متماسكاً غير منقسم
            // §لم يعد هناك تمرير رأسي — الشاشة كلها معروضة فلا حاجة للعودة للأعلى
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.Open"); }
    }

    private void RefreshPlansList()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            // §كفاءة (v1.50.20): تجميع واحد بدل 3N استعلامات — إحصاءات كل الخطط في
            // استعلام GROUP BY وحيد، ثم ربطها بالخطط في الذاكرة (قوائم الخطط تُحمَّل
            // في كل فتح للنافذة المنبثقة، وأصبح المقياس استعلامان مهما كبر عدد الخطط).
            var stats = db.ProductionPlanItems.AsNoTracking()
                .GroupBy(i => i.PlanId)
                .Select(g => new
                {
                    PlanId = g.Key,
                    Customers = g.Select(x => x.CustomerId).Distinct().Count(),
                    Items = g.Count(),
                    Qty = g.Sum(x => x.PlannedQtyKg)
                })
                .ToDictionary(x => x.PlanId);
            var list = db.ProductionPlans.AsNoTracking().OrderByDescending(p => p.Id).ToList();
            _planIds = list.Select(p => p.Id).ToList();
            _plans_all = list.Select(p =>
            {
                stats.TryGetValue(p.Id, out var s);
                return new Views.PlanSearchWindow.PlanSearchItem
                {
                    Id = p.Id,
                    DocNo = p.DocumentNumber,
                    Title = p.PlanTitle,
                    Period = $"{p.StartDate:dd/MM/yyyy} إلى {p.EndDate:dd/MM/yyyy}",
                    Customers = s?.Customers ?? 0,
                    Items = s?.Items ?? 0,
                    Qty = s?.Qty ?? 0,
                    StatusAr = p.IsApproved ? "معتمدة 🟢" : p.Status == "UnderApproval" ? "بانتظار الاعتماد ⏳" : p.Status == "RevisionRequired" ? "معادة للتعديل ↩️" : "مسودة 📝"
                };
            }).ToList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.List"); }
    }

    /// <summary>
    /// §شاشة الخطط الموحّدة: البحث عن الخطط المحفوظة في <b>نافذة منبثقة</b>
    /// (بدل المستطيل الدائم الذي كان يأكل ارتفاع الشاشة في كل لحظة).
    /// النقر المزدوج على خطة في المنبثق يُنزل بجميع بنودها إلى هذه الواجهة عبر <see cref="OpenPlan"/>.
    /// </summary>
    private void OpenPlansSearch()
    {
        try
        {
            RefreshPlansList();
            if (_plans_all.Count == 0)
            {
                AppContainer.Get<DialogService>().Info("لا توجد خطط محفوظة بعد — أنشئ خطة ثم احفظها.");
                return;
            }
            var win = new DocSearchWindow("خطط الإنتاج المحفوظة",
                new List<SearchFieldDef> { new() { Key = "term", LabelAr = "رقم الخطة أو عنوانها أو الحالة" } }, filters =>
                {
                    var term = ScreenSearch.Normalize(filters.GetValueOrDefault("term") ?? "");
                    var rows = _plans_all.Where(p => string.IsNullOrEmpty(term)
                        || ScreenSearch.Normalize(p.DocNo).Contains(term)
                        || ScreenSearch.Normalize(p.Title).Contains(term)
                        || ScreenSearch.Normalize(p.StatusAr).Contains(term)).ToList();
                    return new SearchResult
                    {
                        Columns = new List<string> { "رقم الخطة", "عنوان الخطة", "الفترة", "العملاء", "البنود", "الكمية (كجم)", "الحالة" },
                        Rows = rows.Select(p => (p.Id, new object[] { p.DocNo, p.Title, p.Period, p.Customers, p.Items, p.Qty, p.StatusAr })).ToList()
                    };
                }) { Owner = Window.GetWindow(this) };
            if (win.ShowDialog() == true && win.SelectedId is int id) OpenPlan(id);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.Search"); }
    }

    // ══════════ تعديل البند المحفوظ ══════════

    /// <summary>
    /// §B108 — تعديل بند خطة **محفوظ** (تاريخ/كمية/وردية/صنف/عبوة) مع إعادة فحص الطاقة في الخدمة.
    ///
    /// كان هذا الأمر معلّقاً على لوحة «بنود اليوم» المحذوفة، وهي وحدها التي كانت تحمل
    /// معرّف البند. صار الآن على **الجدول الرئيسي** مباشرةً عبر <c>PlanRowUi.ItemId</c>:
    /// نفس الوظيفة، في المكان الذي ينظر إليه الموظف أصلاً، وبنقرة مزدوجة على الصف.
    ///
    /// يعمل على الخطة المعتمدة أيضاً — وهذا مقصود: الجدول نفسه يُقفل بعد الاعتماد،
    /// و<c>UpdatePlanItem</c> هو المسار الوحيد الذي يفرض حراس التعديل بعد التنفيذ
    /// (صلاحية «إلغاء/استثناء» للبنود المنفَّذة، وحدود فترة الخطة، وفحص الطاقة).
    /// </summary>
    private void EditRow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_locked) { AppContainer.Get<DialogService>().Error("الخطة معتمدة ومقفلة — لا تعديل إلا بعد فك الاعتماد."); return; }
            if (RowsGrid.SelectedItem is not PlanRowUi row)
            { AppContainer.Get<DialogService>().Error("اختر بنداً من الجدول أولاً."); return; }

            // §الصف غير المحفوظ يُعدَّل في الجدول مباشرةً — لا معرف له في القاعدة بعد
            if (row.ItemId <= 0)
            {
                AppContainer.Get<DialogService>().Info(
                    "هذا البند لم يُحفظ بعد — عدّله مباشرةً في الجدول (الكراتين/التاريخ) ثم احفظ الخطة.");
                return;
            }

            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var products = scope.ServiceProvider.GetRequiredService<IPlanningService>().GetFinishedProducts();
            if (products.Count == 0) { AppContainer.Get<DialogService>().Error("لا توجد أصناف تامة نشطة للاختيار."); return; }
            var packs = db.PackagingTypes.Where(p => p.IsActive).ToList();
            var fields = new List<Views.FieldDef>
            {
                new() { Key = "date", LabelAr = "التاريخ الجديد (dd/MM/yyyy)", Default = row.Date },
                new() { Key = "qty", LabelAr = "الكمية الجديدة (كجم)", Default = row.QtyKg.ToString("0.###") },
                new() { Key = "shift", LabelAr = "رقم الوردية", Default = row.ShiftId.ToString() },
                new() { Key = "product", LabelAr = "الصنف التام", Kind = "combo", Options = products.Select(p => p.ProductNameAr).ToArray(), Default = row.ProductName },
                new() { Key = "pack", LabelAr = "العبوة", Kind = "combo", Options = packs.Select(p => p.PackageNameAr).ToArray(), Default = row.PackName }
            };
            var dlg = new Views.EntityFormDialog($"تعديل البند — {row.CustomerName} / {row.ProductName}", fields) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;

            double? qty = null;
            if (double.TryParse(dlg.Values["qty"]?.ToString(), out var q) && q > 0) qty = q;
            int? shift = null;
            if (int.TryParse(dlg.Values["shift"]?.ToString(), out var sh) && sh > 0) shift = sh;
            // تغيير الصنف/العبوة: يُرسل المعرف الجديد فقط إن غيّر المستخدم الاختيار فعلياً
            int? newProductId = null; int? newPackId = null;
            var prodSel = dlg.Values["product"]?.ToString();
            if (!string.IsNullOrEmpty(prodSel) && prodSel != row.ProductName)
                newProductId = products.FirstOrDefault(p => p.ProductNameAr == prodSel)?.Id;
            var packSel = dlg.Values["pack"]?.ToString();
            if (!string.IsNullOrEmpty(packSel) && packSel != row.PackName)
                newPackId = packs.FirstOrDefault(p => p.PackageNameAr == packSel)?.Id;

            var svc = (IPlanProgressService)scope.ServiceProvider.GetService(typeof(IPlanProgressService));
            var r = svc.UpdatePlanItem(row.ItemId, dlg.Values["date"]?.ToString(), qty, shift, null, newProductId, newPackId);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);

            // §إعادة تحميل الخطة من القاعدة: التعديل تم في الخدمة، والجدول يجب أن يعكس
            // ما حُفظ فعلاً (الكراتين المشتقة قد تختلف عمّا أدخله المستخدم) لا ما ظنه.
            if (_currentPlanId.HasValue && _currentPlanId.Value > 0) OpenPlan(_currentPlanId.Value);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.EditRow"); }
    }

    /// <summary>
    /// §B108 — نقر مزدوج على صف الجدول الرئيسي يفتح تعديل البند المحفوظ.
    ///
    /// يتجاهل النقر على خلية قابلة للتحرير (الكراتين/التاريخ) كي لا يسرق النافذةُ
    /// التحريرَ المباشر داخل الجدول، ويتجاهل الصف غير المحفوظ (ItemId = 0).
    /// </summary>
    private void RowsGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (RowsGrid.SelectedItem is not PlanRowUi row || row.ItemId <= 0) return;
        // خلية قيد التحرير ← النقر المزدوج للتحرير لا لفتح النافذة
        if (RowsGrid.CurrentColumn != null && !RowsGrid.CurrentColumn.IsReadOnly) return;
        EditRow_Click(sender, e);
    }

    // ══════════ الطباعة والتصدير ══════════

    /// <summary>§طباعة الخطة بنموذج نظامنا: A4 عمودي (§v1.50.25) — ترويسة الشركة + بطاقة الخطة +
    /// ملخص العملاء + بنود مرتبة بالتاريخ (فاصل لكل يوم) + الإجماليات + تذييل الاعتماد —
    /// معاينة إلزامية قبل الطباعة (تكبير/تصغير + تصدير PDF).</summary>
    private void Print()
    {
        try
        {
            if (_currentPlanId == null || _currentPlanId.Value == 0) { AppContainer.Get<DialogService>().Error("احفظ الخطة أولاً قبل الطباعة."); return; }
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var model = Views.PlanningPrintModel.Load(db, _currentPlanId.Value);
            if (model == null) { AppContainer.Get<DialogService>().Error("تعذر تحميل بيانات الخطة للطباعة."); return; }
            var doc = Views.PlanningPrintDocument.Build(model);
            var preview = new Views.PrintPreviewWindow(doc, $"خطة الإنتاج {model.PlanNumber} — {model.Title}")
            { Owner = Window.GetWindow(this) };
            preview.ShowDialog();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.Print"); }
    }

    private void Export()
    {
        var report = new ReportResult
        {
            TitleAr = $"خطة الإنتاج — {TitleBox.Text}",
            Columns = new List<string> { "م", "العميل", "الشحنة", "الدفعة", "الصنف", "العبوة", "الكراتين", "الوزن (كجم)", "التاريخ", "الوردية" },
            Rows = _rows.Select(r => new object[] { r.No, r.CustomerName, r.ShipmentNo, r.LotCode, r.ProductName, r.PackName, r.Cartons, r.QtyKg, r.Date, r.ShiftName }).ToList()
        };
        AppContainer.Get<ExportPrintService>().ExportExcel(report);
    }

    /// <summary>§بحث وفلترة لحظية على كل الأعمدة.</summary>
    // §حُذف PlansSearch_Changed مع حذف مربع البحث الدائم: البحث صار في نافذة منبثقة (OpenPlansSearch).

    // ══════════ 1.50.60 تحسينات عامة 7-ج/7-هـ/7-ب/7-د ══════════
    private void DuplicateRow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_locked) return;
            var src = RowsGrid.SelectedItem as PlanRowUi ?? _rows.LastOrDefault(r => r.LotId != null || r.ProductId != 0);
            if (src == null || (src.LotId == null && src.ProductId == 0)) { AppContainer.Get<DialogService>().Error("اختر بنداً منزَّلاً لتكراره أولاً."); return; }
            var dup = new PlanRowUi
            {
                CustomerId = src.CustomerId, CustomerName = src.CustomerName,
                ShipmentId = src.ShipmentId, ShipmentNo = src.ShipmentNo,
                WarehouseId = src.WarehouseId, WarehouseName = src.WarehouseName,
                LotId = src.LotId, LotCode = src.LotCode, RawProductId = src.RawProductId, RawName = src.RawName,
                OnHandKg = src.OnHandKg, ReservedKg = src.ReservedKg, AvailableKg = src.AvailableKg,
                ProductId = src.ProductId, ProductName = src.ProductName,
                PackId = src.PackId, PackName = src.PackName,
                RawKgPerFinishedKg = src.RawKgPerFinishedKg,
                RawFactorPackagingTypeId = src.RawFactorPackagingTypeId,
                UnitDisplay = src.UnitDisplay, CartonWeight = src.CartonWeight,
                QtyKg = src.QtyKg, Cartons = src.Cartons,
                DateValue = src.DateValue, ShiftId = src.ShiftId, ShiftName = src.ShiftName,
                LineId = src.LineId, LineName = src.LineName,
                SourceUnit = src.SourceUnit, SourceQtyInUnit = src.SourceQtyInUnit,
                SourceUnitWeightKg = src.SourceUnitWeightKg, SourceQtyKg = src.SourceQtyKg,
                PreservedAllocations = src.PreservedAllocations?.Select(a => new PlanItemAllocationDto
                {
                    WarehouseId = a.WarehouseId, LotId = a.LotId, CustomerId = a.CustomerId, AllocatedQtyKg = a.AllocatedQtyKg
                }).ToList() ?? new List<PlanItemAllocationDto>()
            };
            dup.ApplySavedRawRequirement(src.RawRequiredKg, src.WastePercent, src.CustomRawRequiredKg);
            dup.ConfigureReceiptUnitContext(src.LotReceiptUnit, src.LotReceiptUnitWeightKg, src.SourceMode);
            _rows.Add(dup);
            Renumber();
            UpdateTotals();
            RowsGrid.SelectedItem = dup;
            RowsGrid.ScrollIntoView(dup);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.DuplicateRow"); }
    }

    private void RowsGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        try
        {
            if (_locked) return;
            if (e.Key == System.Windows.Input.Key.F4) { PickLot_Click(null, null); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.F6) { MultiCustomers_Click(null, null); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.F10) { Save_Click(null, null); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Enter)
            {
                // Enter يمين مثل Excel بين الصفوف المنزَّلة فقط (دون إضافة صف فارغ)
                if (RowsGrid.CurrentColumn != null)
                {
                    int idx = RowsGrid.Columns.IndexOf(RowsGrid.CurrentColumn);
                    if (idx < RowsGrid.Columns.Count - 1)
                    {
                        RowsGrid.CurrentCell = new DataGridCellInfo(RowsGrid.SelectedItem, RowsGrid.Columns[idx + 1]);
                        RowsGrid.BeginEdit();
                        e.Handled = true;
                    }
                    else
                    {
                        int rIdx = _rows.IndexOf(RowsGrid.SelectedItem as PlanRowUi);
                        if (rIdx >= 0 && rIdx < _rows.Count - 1)
                        {
                            RowsGrid.SelectedIndex = rIdx + 1;
                            RowsGrid.CurrentCell = new DataGridCellInfo(_rows[rIdx + 1], RowsGrid.Columns[1]);
                            RowsGrid.BeginEdit();
                        }
                        e.Handled = true;
                    }
                }
            }
        }
        catch { }
    }

        private void AutoSaveDraft()
        {
            // §1.50.69 FIX: إلغاء الحفظ التلقائي نهائياً — حذف أي مسودات قديمة
            try { if (System.IO.File.Exists(AutoSavePath)) System.IO.File.Delete(AutoSavePath); } catch { }
            return;
        }

    private void TryRestoreAutoSave()
    {
        // §1.50.69 FIX: إلغاء استعادة مسودة تلقائية نهائياً + حذف الملف
        try { if (System.IO.File.Exists(AutoSavePath)) System.IO.File.Delete(AutoSavePath); } catch { }
        return;
    }

    private class AutoSaveRow
    {
        public int? CustomerId { get; set; } public string CustomerName { get; set; }
        public int? LotId { get; set; } public string LotCode { get; set; }
        public int ProductId { get; set; } public string ProductName { get; set; }
        public int? PackId { get; set; } public string PackName { get; set; }
        public double QtyKg { get; set; } public int Cartons { get; set; }
        public string Date { get; set; } public int? ShiftId { get; set; } public int? LineId { get; set; }
    }

    /// <summary>حذف توافق للمسودات القديمة في التشغيل العادي فقط؛ لا يلمس بيانات الاختبار أو OFFTT المنفصل.</summary>
    private static void RemoveLegacyPlanningDrafts()
    {
        if (IsolatedTestRun.IsEnabled || StandaloneInstall.IsEnabled) return;
        try
        {
            var draftsDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DateERP", "drafts");
            if (!System.IO.Directory.Exists(draftsDir)) return;
            foreach (var file in System.IO.Directory.GetFiles(draftsDir, "PlanningDraft_*.json"))
            {
                try { System.IO.File.Delete(file); } catch { }
            }
        }
        catch { }
    }

    private void ImportExcel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_locked) { AppContainer.Get<DialogService>().Error("الخطة معتمدة ومقفلة."); return; }
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Excel (*.xlsx;*.xls)|*.xlsx;*.xls|CSV (*.csv)|*.csv", Title = "اختر ملف بنود الخطة" };
            if (dlg.ShowDialog() != true) return;
            int added = 0;
            if (dlg.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                var lines = System.IO.File.ReadAllLines(dlg.FileName);
                foreach (var line in lines.Skip(1))
                {
                    var parts = line.Split(",");
                    if (parts.Length < 2) continue;
                    // توقع: LotCode, ProductCode, Cartons, Date
                    var lotCode = parts[0].Trim();
                    var prodCode = parts.Length > 1 ? parts[1].Trim() : "";
                    int cartons = parts.Length > 2 && int.TryParse(parts[2].Trim(), out var c) ? c : 0;
                    string dateStr = parts.Length > 3 ? parts[3].Trim() : "";
                    if (string.IsNullOrWhiteSpace(lotCode)) continue;
                    // ابحث عن الدفعة
                    using var scope = AppContainer.NewScope();
                    var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                    var lot = db.Lots.AsNoTracking().FirstOrDefault(l => l.LotCode == lotCode);
                    if (lot == null) continue;
                    var prod = !string.IsNullOrWhiteSpace(prodCode) ? db.Products.AsNoTracking().FirstOrDefault(p => p.ProductCode == prodCode) : null;
                    var row = new PlanRowUi
                    {
                        LotId = lot.Id, LotCode = lot.LotCode, RawProductId = lot.ProductId,
                        CustomerId = lot.CustomerId, CustomerName = db.Customers.Where(c => c.Id == lot.CustomerId).Select(c => c.CustomerName).FirstOrDefault() ?? "—",
                        ProductId = prod?.Id ?? 0, ProductName = prod?.ProductNameAr ?? "—",
                        Cartons = cartons > 0 ? cartons : 1,
                        QtyKg = prod != null && prod.CartonWeightKg > 0 ? cartons * prod.CartonWeightKg : 0,
                        DateValue = DatesErp.Core.Common.UiFormat.TryParseDate(dateStr, out var d) ? d : StartBox.SelectedDate
                    };
                    RefreshDisplayedRawFactor(row, db);
                    _rows.Add(row); added++;
                }
            }
            else
            {
                using var wb = new ClosedXML.Excel.XLWorkbook(dlg.FileName);
                var ws = wb.Worksheets.First();
                var rows = ws.RowsUsed().Skip(1);
                foreach (var r in rows)
                {
                    var lotCode = r.Cell(1).GetString().Trim();
                    var prodCode = r.Cell(2).GetString().Trim();
                    var cartonsStr = r.Cell(3).GetString().Trim();
                    var dateStr = r.Cell(4).GetString().Trim();
                    if (string.IsNullOrWhiteSpace(lotCode)) continue;
                    int.TryParse(cartonsStr, out var cartons);
                    using var scope = AppContainer.NewScope();
                    var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                    var lot = db.Lots.AsNoTracking().FirstOrDefault(l => l.LotCode == lotCode);
                    if (lot == null) continue;
                    var prod = !string.IsNullOrWhiteSpace(prodCode) ? db.Products.AsNoTracking().FirstOrDefault(p => p.ProductCode == prodCode) : null;
                    var row = new PlanRowUi
                    {
                        LotId = lot.Id, LotCode = lot.LotCode, RawProductId = lot.ProductId,
                        CustomerId = lot.CustomerId, CustomerName = db.Customers.Where(c => c.Id == lot.CustomerId).Select(c => c.CustomerName).FirstOrDefault() ?? "—",
                        ProductId = prod?.Id ?? 0, ProductName = prod?.ProductNameAr ?? "—",
                        Cartons = cartons > 0 ? cartons : 1,
                        QtyKg = prod != null && prod.CartonWeightKg > 0 ? cartons * prod.CartonWeightKg : 0,
                        DateValue = DatesErp.Core.Common.UiFormat.TryParseDate(dateStr, out var d) ? d : StartBox.SelectedDate
                    };
                    RefreshDisplayedRawFactor(row, db);
                    _rows.Add(row); added++;
                }
            }
            AppContainer.Get<DialogService>().Info($"تم استيراد {added} بند من Excel.");
            EnsureEmptyRow();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.ImportExcel"); }
    }
}
