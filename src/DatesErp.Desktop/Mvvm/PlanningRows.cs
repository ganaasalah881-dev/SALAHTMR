#nullable enable annotations
using System.Collections.ObjectModel;
using System.ComponentModel;
using DatesErp.Core.Interfaces.Services;
namespace DatesErp.Desktop.Views.Screens;

public class LotEditorRow : INotifyPropertyChanged
{
    public Func<int, string> QuantityGuard { get; set; }
    public string QuantityError { get; private set; }
    private PlanCapacityRow _capacity;
    public PlanCapacityRow Capacity { get => _capacity; set { _capacity = value; OnChange(nameof(Capacity)); OnChange(nameof(CapAlert)); OnChange(nameof(ProductCapacityDisplay)); } }
    private void GuardQuantity(int quantity)
    {
        if (quantity < 0)
        {
            QuantityError = "الكمية لا يمكن أن تكون سالبة.";
            OnChange(nameof(QuantityError));
            throw new ArgumentException(QuantityError);
        }
        // تجاوز الطاقة ليس خطأ إدخال: اقبل وأظهر الكمية التامة المحسوبة، واعرض سبب التحذير
        // في شريط الطاقة/التلميح. يرفض مسار الإنزال الكمية عند إعادة التحقق من السعة.
        QuantityError = null;
        OnChange(nameof(QuantityError));
        QuantityGuard?.Invoke(quantity);
    }

    public List<ProductOption> RawOptions { get; set; } = new();
    public bool CanChangeRaw => LotId == null;
    public Func<int, List<ProductOption>> LoadFinishedProducts { get; set; }
    /// <summary>Official raw/final factor for the selected raw source and package; zero means unavailable.</summary>
    public Func<int, int?, int?, double> ResolveRawFactor { get; set; }
    private int? _rawProductId;
    public int? RawProductId
    {
        get => _rawProductId;
        set
        {
            if (_rawProductId == value) return;
            _rawProductId = value;
            ReloadFinishedProducts();
            OnChange(nameof(RawProductId));
            Recalc();
        }
    }
    public string LinkMessage => RawProductId == null ? "اختر الصنف الخام أولاً." : AllProducts.Count == 0
        ? "لا توجد أصناف تامة نشطة مرتبطة بهذا الخام." : "";
    public void ReloadFinishedProducts()
    {
        List<ProductOption> next;
        try { next = RawProductId != null && LoadFinishedProducts != null ? LoadFinishedProducts(RawProductId.Value) : new(); }
        catch { ApplyLinkedProducts(new()); throw; }
        ApplyLinkedProducts(next);
    }
    private void ApplyLinkedProducts(List<ProductOption> next)
    {
        if (ProductId != null && !next.Any(p => p.Id == ProductId))
        { ProductId = null; CartonsText = "0"; IsChecked = false; }
        if (!AllProducts.Select(p => (p.Id, p.Name, p.SelectionDisplayName, p.CartonWeightKg, p.DefaultCartonWeightKg, p.MoldsCount, p.DefaultPackagingTypeId))
            .SequenceEqual(next.Select(p => (p.Id, p.Name, p.SelectionDisplayName, p.CartonWeightKg, p.DefaultCartonWeightKg, p.MoldsCount, p.DefaultPackagingTypeId))))
            AllProducts = next;
        OnChange(nameof(LinkMessage));
    }

    // هوية الشحنة/الدفعة
    public int? LotId { get; set; }
    public int? ShipmentId { get; set; }
    public string ShipmentNo { get; set; }
    /// <summary>مخزن الدفعة المعروض؛ لا يُعد بديلاً عن تخصيص الخطة المحفوظ.</summary>
    public int? WarehouseId { get; set; }
    public string WarehouseName { get; set; }
    public string LotCode { get; set; }
    public int? CustomerId { get; set; }
    public string CustomerName { get; set; }
    public string RawName { get; set; }
    /// <summary>أعمدة الرصيد التفصيلية حسب الدفعة/المخزن — لا تُختزل في إجمالي العميل.</summary>
    public double OnHandKg { get; set; }
    public double ReservedKg { get; set; }
    public double OtherCommitmentsKg { get; set; }
    public double Available { get; set; }
    public double AvailableKg => Available;
    /// <summary>خام مطلوب/مخصص بالكجم؛ لا يساوي وزن الإنتاج التام إلا إذا نصّ المعامل الرسمي على ذلك.</summary>
    public double SelectedQtyKg => RawRequiredKg;
    public string FinishedWeightDisplay => ComputedKg > 0
        ? $"{ComputedKg:N1} كجم"
        : (int.TryParse(CartonsText, out var cartons) && cartons > 0 ? "غير محسوب — وزن العبوة غير معرّف" : "—");
    public string PackWeightDisplay => PackWeight > 0 ? PackWeight.ToString("N1") : "غير معرّف";
    public string RawRequirementDisplay
    {
        get
        {
            if (RawRequiredKg > 0) return $"{RawRequiredKg:N1} كجم";
            if (ProductId == null || !int.TryParse(CartonsText, out var cartons) || cartons <= 0) return "—";
            if (PackWeight <= 0) return "غير محسوب — وزن العبوة غير معرّف";
            return "—";
        }
    }
    /// <summary>نسبة الفرز/الفاقد المتوقعة للدفعة (%) — اختيارية في سطر الخطة بدلاً من معامل الصنف.</summary>
    private double _wastePercent;
    private string _wastePercentText = "0";
    public double WastePercent => _wastePercent;
    public string WastePercentText
    {
        get => _wastePercentText;
        set
        {
            var norm = string.IsNullOrWhiteSpace(value) ? "0" : value.Trim();
            if (_wastePercentText == norm) return;
            _wastePercentText = norm;
            if (double.TryParse(norm, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var wp)
                || double.TryParse(norm, out wp))
            {
                _wastePercent = Math.Max(0, Math.Min(500, wp));
                _customRawRequiredKg = null;
            }
            OnChange(nameof(WastePercentText));
            OnChange(nameof(WastePercent));
            Recalc();
        }
    }
    private double? _customRawRequiredKg;
    public double? CustomRawRequiredKg => _customRawRequiredKg;
    public string RawRequiredInputText
    {
        get => RawRequiredKg > 0 ? RawRequiredKg.ToString("0.#") : "";
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                _customRawRequiredKg = null;
                Recalc();
                OnChange(nameof(RawRequiredInputText));
                return;
            }
            if ((double.TryParse(value.Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var rk)
                || double.TryParse(value.Trim(), out rk)) && rk > 0)
            {
                _customRawRequiredKg = Math.Round(rk, 2);
                if (ComputedKg > 0.001)
                {
                    _wastePercent = Math.Max(0, Math.Round(((_customRawRequiredKg.Value - ComputedKg) / ComputedKg) * 100.0, 2));
                    _wastePercentText = _wastePercent.ToString("0.#");
                    OnChange(nameof(WastePercent));
                    OnChange(nameof(WastePercentText));
                }
                Recalc();
                OnChange(nameof(RawRequiredInputText));
            }
        }
    }
    /// <summary>اسم قديم محفوظ للتوافق؛ القيمة المعروضة هي احتياج الخام لا وزن الإنتاج التام.</summary>
    public string SelectedQtyDisplay => RawRequiredKg > 0 ? $"{RawRequiredKg:N1} كجم" : "غير محسوب";
    public string DaysInStockText { get; set; } = "";
    public string PresetDate { get; set; }

    // الوردية
    public List<ShiftOption> AllShifts { get; set; } = new();
    private int? _shiftId;
    public int? ShiftId
    {
        get => _shiftId;
        set { _shiftId = value; var hit = value != null ? AllShifts.FirstOrDefault(s => s.Id == value.Value) : null; if (hit != null) ShiftName = hit.Name; OnChange(nameof(ShiftId)); }
    }
    public string ShiftName { get; set; } = "—";
    private DateTime? _dateValue;
    public DateTime? DateValue
    {
        get => _dateValue;
        set { if (_dateValue == value) return; _dateValue = value; OnChange(nameof(DateValue)); Recalc(); }
    }

    // ═══ §المعالجة والتعقيم — حالة جاهزية الخام (أحمر) + منع الإنتاج قبل تاريخ المعالجة ═══
    public bool TreatmentRequired { get; set; }
    public DateTime? TreatmentUntilDate { get; set; }
    public DateTime? TreatmentReadyDate { get; set; }
    public double UnderTreatmentKg { get; set; }
    /// <summary>يُستخدم عندما جُلب الرصيد من دفتر الخدمة على تاريخ الصف؛ يمنع إضافة كمية المعالجة مرة ثانية.</summary>
    public bool AvailabilityIncludesDateTreatment { get; set; }
    public bool TreatmentBlocked { get; private set; }
    public string TreatmentBlockReason { get; private set; }
    public string TreatmentDisplay
    {
        get
        {
            if (!TreatmentRequired) return "لا معالجة";
            bool under = UnderTreatmentKg > 0;
            if (!under) return "معالجة مكتملة ✓";
            DateTime? until = TreatmentBlockUntil();
            string date = until.HasValue ? $" حتى {until:dd/MM/yyyy}" : " — موعد الجاهزية غير معرّف";
            if (TreatmentBlocked) return $"🔴 تحت المعالجة: {UnderTreatmentKg:N1} كجم{date}";
            return $"🟢 ستصبح جاهزة: {UnderTreatmentKg:N1} كجم{date}";
        }
    }
    private DateTime? TreatmentBlockUntil()
    {
        DateTime? until = null;
        if (TreatmentUntilDate != null) until = TreatmentUntilDate;
        if (TreatmentReadyDate != null && (until == null || TreatmentReadyDate > until)) until = TreatmentReadyDate;
        return until?.Date;
    }
    private void RecalcTreatment()
    {
        var date = DateValue?.Date;
        var until = TreatmentBlockUntil();
        bool blocked = TreatmentRequired && UnderTreatmentKg > 0 && date.HasValue && until.HasValue
            && date.Value < until.Value;
        TreatmentBlocked = blocked;
        TreatmentBlockReason = blocked
            ? $"خام الدفعة قيد المعالجة حتى {until:dd/MM/yyyy} — لا يمكن الإنتاج قبل هذا التاريخ."
            : null;
        OnChange(nameof(TreatmentDisplay)); OnChange(nameof(TreatmentBlocked)); OnChange(nameof(TreatmentBlockReason));
    }

    // تعريف الصنف التام — §1.50.67 FIX: عرض الوحدة بوحدتين (كرتون + كجم) بدل كجم فقط
    public Dictionary<int, string> ProductUnits { get; set; } = new();
    public Dictionary<int, double> ProductCartonWeights { get; set; } = new();
    public string UnitDisplay
    {
        get
        {
            if (_productId == null) return "—";
            string u = ProductUnits.TryGetValue(_productId.Value, out var uu) && !string.IsNullOrWhiteSpace(uu) ? uu : "كرتون";
            // PackWeight already follows UnitsPolicy: an explicit/default selected
            // package wins; without one, use the finished-product card weight.
            double cw = PackWeight;
            if (cw <= 0 && ProductCartonWeights.TryGetValue(_productId.Value, out var cc)) cw = cc;
            // إذا كان وزن الكرتون معروف اعرض "كرتون (2 كجم)" بدل "كجم" فقط
            if (cw > 0) return $"{u} ({cw:N1} كجم)";
            return u;
        }
    }
    public Dictionary<int, double> PerProductAvailable { get; set; } = new();
    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (value && QuantityGuard != null && int.TryParse(CartonsText, out var q))
            { try { GuardQuantity(q); } catch (ArgumentException) { OnChange(nameof(IsChecked)); return; } }
            _isChecked = value; OnChange(nameof(IsChecked));
        }
    }

    private bool _cartonsAutoPopulated;
    private int? _productId;
    public int? ProductId
    {
        get => _productId;
        set
        {
            bool shouldFillAvailableRaw = _cartonsAutoPopulated
                || !DatesErp.Core.Common.UiFormat.TryParseInt(_cartonsText, out var currentCartons)
                || currentCartons <= 0;
            if (_productId != value) _packId = null; // never carry a prior product's hidden package into the next product
            _productId = value;
            OnChange(nameof(ProductId));
            OnChange(nameof(UnitDisplay));
            RebuildPacks();
            if (_pendingSourceUnits is double pending && pending > 0)
            {
                ApplyPlannedSourceUnitsToCartons();
                _cartonsAutoPopulated = false;
            }
            else
            {
                Recalc();
                if (value != null && shouldFillAvailableRaw) PopulateCartonsFromAvailableRaw();
            }
        }
    }
    private double EffectiveRawFactor(int finishedProductId)
    {
        double configured = 0;
        try { configured = ResolveRawFactor?.Invoke(finishedProductId, RawProductId, _packId) ?? 0; }
        catch { configured = 0; }
        if (configured <= 0 && AllProducts != null)
            configured = AllProducts.FirstOrDefault(p => p.Id == finishedProductId)?.RawKgPerFinishedKg ?? 0;
        // التوافق مع تعريفات التحويل القديمة: عند عدم وجود معامل منفصل تبقى القاعدة 1 كجم خام/كجم تام.
        return double.IsFinite(configured) && configured > 0 ? configured : 1.0;
    }

    private void PopulateCartonsFromAvailableRaw()
    {
        if (_productId is not int productId) return;
        Recalc(); // يحمّل وزن العبوة الافتراضي من تعريف الصنف قبل التحويل.
        double availableRawKg = Ctx?.AvailableKg ?? Available;
        if (availableRawKg <= 0.001 || PackWeight <= 0) return;
        double rawPerFinishedKg = EffectiveRawFactor(productId);
        double rawPerCarton = PackWeight * rawPerFinishedKg * (1.0 + _wastePercent / 100.0);
        if (!double.IsFinite(rawPerCarton) || rawPerCarton <= 0) return;
        int cartons = (int)Math.Floor(availableRawKg / rawPerCarton + 1e-6);
        if (cartons <= 0) return;
        try { GuardQuantity(cartons); } catch (ArgumentException) { }
        _customRawRequiredKg = null;
        _cartonsAutoPopulated = true;
        _syncingQuantities = true;
        try
        {
            _cartonsText = cartons.ToString();
            OnChange(nameof(CartonsText));
            _isChecked = true;
            OnChange(nameof(IsChecked));
            Recalc();
        }
        finally { _syncingQuantities = false; }
    }

    private ObservableCollection<PackOption> _packs = new();
    public ObservableCollection<PackOption> Packs { get => _packs; private set { _packs = value; OnChange(nameof(Packs)); } }
    private int? _packId;
    public int? PackId
    {
        get => _packId;
        set
        {
            _packId = value;
            OnChange(nameof(PackId));
            if (_pendingSourceUnits is double pending && pending > 0)
                ApplyPlannedSourceUnitsToCartons();
            else
                Recalc();
        }
    }

    private bool _syncingQuantities;
    private double? _pendingSourceUnits;

    // §الإنتاج (كرتون) — يتزامن ثنائياً مع الكمية بوحدة الاستلام (كجم/كرتون/سلة).
    private string _cartonsText = "0";
    public string CartonsText
    {
        get => _cartonsText;
        set
        {
            string norm = DatesErp.Core.Common.UiFormat.NormalizeDigits(value)?.Trim() ?? "0";
            if (DatesErp.Core.Common.UiFormat.TryParseInt(norm, out var q))
            {
                QuantityError = null;
                // قيم الإنتاج المشتقة من وحدة الاستلام تُعرض حتى عند تجاوز الطاقة؛
                // الحارس يبقى على الإدخال اليدوي، والإدراج النهائي يعيد فحص الطاقة ويرفضه.
                if (!_syncingQuantities) GuardQuantity(q);
            }
            else { QuantityError = "أدخل عدداً صحيحاً من الكراتين."; OnChange(nameof(QuantityError)); }
            if (!_syncingQuantities)
            {
                _pendingSourceUnits = null;
                _customRawRequiredKg = null;
                _cartonsAutoPopulated = false;
            }
            _cartonsText = norm; OnChange(nameof(CartonsText)); Recalc();
            if (QuantityGuard != null && DatesErp.Core.Common.UiFormat.TryParseInt(norm, out _)) IsChecked = q > 0;
        }
    }
    private double _computedKg;
    public double ComputedKg
    {
        get => _computedKg;
        private set
        {
            _computedKg = value;
            OnChange(nameof(ComputedKg)); OnChange(nameof(FinishedWeightDisplay));
            OnChange(nameof(RawRequirementDisplay)); OnChange(nameof(SelectedQtyKg)); OnChange(nameof(SelectedQtyDisplay));
        }
    }

    // مواصفات الصنف التام (تلقائية من العبوة المختارة — لا يُدخلها المستخدم)
    public double PackWeight
    {
        get => _packWeight;
        set
        {
            _packWeight = value;
            OnChange(nameof(PackWeight)); OnChange(nameof(PackWeightDisplay));
            OnChange(nameof(FinishedWeightDisplay)); OnChange(nameof(RawRequirementDisplay));
        }
    }
    private double _packWeight;
    public int MoldsCount { get => _moldsCount; private set { _moldsCount = value; OnChange(nameof(MoldsCount)); } }
    private int _moldsCount;

    // الخام المطلوب + كفاية الخام (تلقائي)
    public double RawKgPerFinishedKg { get; private set; }
    public double RawRequiredKg { get => _rawRequiredKg; private set { _rawRequiredKg = value; OnChange(nameof(RawRequiredKg)); OnChange(nameof(SelectedQtyKg)); OnChange(nameof(SelectedQtyDisplay)); } }
    private double _rawRequiredKg;
    public double RawShortageKg { get => _rawShortageKg; private set { _rawShortageKg = value; OnChange(nameof(RawShortageKg)); } }
    private double _rawShortageKg;
    public bool RawSufficient { get => _rawSufficient; private set { _rawSufficient = value; OnChange(nameof(RawSufficient)); } }
    private bool _rawSufficient;
    public string RawStatusText { get => _rawStatusText; private set { _rawStatusText = value; OnChange(nameof(RawStatusText)); } }
    private string _rawStatusText;

    // ═══ §سحب الخام والتخطيط بوحدة الاستلام (كجم / كرتون / سلة بحسب ما تم استلامه) ═══
    public ShipmentQuantityContext Ctx { get; set; }
    public string ReceiptUnitText => string.IsNullOrWhiteSpace(Ctx?.ReceiptUnit) ? "كجم" : Ctx.ReceiptUnit;
    public double UnitWeight => Ctx != null
        ? (Ctx.UnitWeightKg > 0 ? Ctx.UnitWeightKg
            : (Ctx.ReceivedUnits > 0 && Ctx.ReceivedKg > 0 ? Math.Round(Ctx.ReceivedKg / Ctx.ReceivedUnits, 3) : 1.0))
        : 1.0;
    public double EffectiveSourceUnitWeightKg
    {
        get
        {
            // التحويل الموثوق هو بين الكجم ووحدة الاستلام الفعلية للدفعة فقط؛
            // لا نخمن وزن سلة/كرتون بديل من قيم افتراضية.
            if (string.Equals(SourceMode, "كجم", StringComparison.OrdinalIgnoreCase)) return 1.0;
            return UnitWeight > 0 ? UnitWeight : 1.0;
        }
    }
    public List<string> SourceModes { get; private set; } = new();
    private string _sourceMode;
    public string SourceMode
    {
        get => _sourceMode;
        set
        {
            if (_sourceMode == value || string.IsNullOrWhiteSpace(value)) return;
            _sourceMode = value;
            OnChange(nameof(SourceMode));
            OnChange(nameof(AvailableDisplay));
            OnChange(nameof(PlannedSourceUnitsText));
            Recalc();
        }
    }
    /// <summary>يبني خيارات طريقة السحب والتخطيط من وحدة الاستلام الفعلية (سلة/كرتون/كجم) + كجم.</summary>
    public void SetContext()
    {
        var m = new List<string>();
        string u = ReceiptUnitText;
        if (!string.IsNullOrWhiteSpace(u) && u != "—" && !m.Contains(u)) m.Add(u);
        if (!m.Contains("كجم")) m.Add("كجم");
        SourceModes = m;
        if (string.IsNullOrEmpty(_sourceMode) || !m.Contains(_sourceMode)) _sourceMode = m[0];
        OnChange(nameof(SourceModes)); OnChange(nameof(SourceMode)); OnChange(nameof(ContextDisplay));
        OnChange(nameof(ReceiptUnitText)); OnChange(nameof(AvailableDisplay)); OnChange(nameof(PlannedSourceUnitsText));
        Recalc();
    }
    public string ContextDisplay
    {
        get
        {
            if (Ctx == null) return "—";
            string receipt = string.Equals(ReceiptUnitText, "كجم", StringComparison.OrdinalIgnoreCase)
                ? $"مستلم {Ctx.ReceivedKg:N0} كجم — متاح {Ctx.AvailableKg:N0} كجم"
                : $"{ReceiptUnitText} ({UnitWeight:0.##} كجم): مستلم {Ctx.ReceivedUnits:N0} ({Ctx.ReceivedKg:N0} كجم) — متاح {Math.Floor(Ctx.AvailableUnits):N0} ({Ctx.AvailableKg:N0} كجم)";
            string package = PackWeight > 0 ? $"وزن العبوة {PackWeight:N1} كجم" : "وزن العبوة غير معرّف";
            if (MoldsCount > 0) package += $" · {MoldsCount} قالب";
            string rawStatus = string.IsNullOrWhiteSpace(RawStatusText) ? "" : $" · الخام: {RawStatusText}";
            return $"{receipt} · {package}{rawStatus}";
        }
    }
    /// <summary>المتاح يتغيّر مع وحدة التخطيط المختارة: بالوحدة المستلمة (سلة/كرتون + كجم) أو بالكيلو.</summary>
    public string AvailableDisplay
    {
        get
        {
            if (Ctx == null) return "—";
            bool units = !string.Equals(SourceMode, "كجم", StringComparison.OrdinalIgnoreCase);
            if (!units) return $"{Ctx.AvailableKg:N1} كجم";
            double unitKg = EffectiveSourceUnitWeightKg;
            double availableUnits = unitKg > 0 ? Math.Floor(Ctx.AvailableKg / unitKg) : 0;
            return $"{availableUnits:N0} {SourceMode} ({Ctx.AvailableKg:N1} كجم)";
        }
    }

    /// <summary>
    /// إدخال الكمية المراد تخطيطها مباشرةً بوحدة الاستلام المختارة (كجم / كرتون / سلة بحسب ما تم استلامه)،
    /// مع اشتقاق عدد الكراتين التامة واحتياج الخام تلقائياً.
    /// </summary>
    public string PlannedSourceUnitsText
    {
        get
        {
            if (_pendingSourceUnits is double p && p > 0) return p.ToString("0.##");
            return SourceQtyInUnit > 0 ? SourceQtyInUnit.ToString("0.##") : "";
        }
        set
        {
            if (_syncingQuantities) return;
            var trimmed = DatesErp.Core.Common.UiFormat.NormalizeDigits(value)?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                _pendingSourceUnits = null;
                _customRawRequiredKg = null;
                _syncingQuantities = true;
                try
                {
                    CartonsText = "0";
                    IsChecked = false;
                }
                finally { _syncingQuantities = false; }
                OnChange(nameof(PlannedSourceUnitsText));
                return;
            }
            if (DatesErp.Core.Common.UiFormat.TryParseDouble(trimmed, out var units) && units >= 0)
            {
                if (units <= 0.0001)
                {
                    _pendingSourceUnits = null;
                    _customRawRequiredKg = null;
                    _syncingQuantities = true;
                    try { CartonsText = "0"; IsChecked = false; }
                    finally { _syncingQuantities = false; }
                    OnChange(nameof(PlannedSourceUnitsText));
                    return;
                }
                _pendingSourceUnits = Math.Round(units, 2);
                ApplyPlannedSourceUnitsToCartons();
                OnChange(nameof(PlannedSourceUnitsText));
            }
        }
    }

    private void ApplyPlannedSourceUnitsToCartons()
    {
        if (_pendingSourceUnits is not double units || units <= 0)
        {
            Recalc();
            return;
        }
        double uw = EffectiveSourceUnitWeightKg;
        double rawKg = Math.Round(units * (uw > 0 ? uw : 1.0), 2);
        _customRawRequiredKg = rawKg;

        var pack = AllPacks.FirstOrDefault(p => p.Id == _packId);
        var prod = _productId != null ? AllProducts.FirstOrDefault(p => p.Id == _productId) : null;
        double packW = 0;
        if (pack?.UnitWeightKg > 0) packW = pack.UnitWeightKg;
        else if (prod != null && prod.CartonWeightKg > 0) packW = prod.CartonWeightKg;
        else if (prod != null && prod.MoldsCount > 0 && prod.MoldWeightKg > 0) packW = prod.MoldsCount * prod.MoldWeightKg;
        else if (prod != null && prod.DefaultCartonWeightKg > 0) packW = prod.DefaultCartonWeightKg;
        else if (PackWeight > 0) packW = PackWeight;

        if (_productId != null && packW > 0)
        {
            double rawPerFinishedKg = EffectiveRawFactor(_productId.Value);
            double netFinishedKg = rawKg / (rawPerFinishedKg * (1.0 + _wastePercent / 100.0));
            int ctn = (int)Math.Floor(netFinishedKg / packW + 1e-6);
            if (ctn <= 0 && netFinishedKg > 0.001) ctn = 1;
            // استعلم عن الطاقة لعرض التنبيه، لكن لا تمنع تحويل وحدة الاستلام إلى تام.
            // سيظهر الوزن/الكراتين فوراً؛ ويظل الإدراج الجماعي محجوباً إذا كانت الطاقة متجاوزة.
            QuantityGuard?.Invoke(ctn);
            _syncingQuantities = true;
            try
            {
                _cartonsText = ctn.ToString();
                OnChange(nameof(CartonsText));
                _pendingSourceUnits = null;
                _cartonsAutoPopulated = false;
                Recalc();
                _isChecked = ctn > 0;
                OnChange(nameof(IsChecked));
            }
            finally { _syncingQuantities = false; }
        }
        else
        {
            SourceUnit = string.Equals(SourceMode, "كجم", StringComparison.OrdinalIgnoreCase) ? "كجم" : ReceiptUnitText;
            SourceUnitWeightKg = uw;
            SourceQtyKg = rawKg;
            SourceQtyInUnit = units;
            Recalc();
        }
    }

    // التتبع المحفوظ للسحب (يُشتق من الإنتاج × وزن العبوة — يُنقل إلى بند الخطة)
    public string SourceUnit { get; set; }
    public double SourceQtyInUnit { get; set; }
    public double SourceUnitWeightKg { get; set; }
    public double SourceQtyKg { get; set; }
    public string SourceError { get => _sourceError; private set { _sourceError = value; OnChange(nameof(SourceError)); } }
    private string _sourceError;

    // مراجع الحساب
    private List<ProductOption> _allProducts = new();
    public List<ProductOption> AllProducts { get => _allProducts; set { _allProducts = value; OnChange(nameof(AllProducts)); OnChange(nameof(LinkMessage)); } }
    public List<PackOption> AllPacks { get; set; } = new();
    private void RebuildPacks()
    {
        Packs = new ObservableCollection<PackOption>(ProductId == null ? new List<PackOption>() : AllPacks);
        var prod = ProductId != null ? AllProducts.FirstOrDefault(p => p.Id == ProductId) : null;
        int? prefer = prod?.DefaultPackagingTypeId;
        if (_packId != null && Packs.Any(p => p.Id == _packId)) { /* keep an explicit package */ }
        else if (prefer != null && Packs.Any(p => p.Id == prefer)) _packId = prefer;
        else _packId = null; // no configured default: don't guess from the first global package
        OnChange(nameof(PackId));
    }

    public void Recalc()
    {
        DatesErp.Core.Common.UiFormat.TryParseInt(_cartonsText, out var ctn);
        var pack = AllPacks.FirstOrDefault(p => p.Id == _packId);
        var prod = _productId != null ? AllProducts.FirstOrDefault(p => p.Id == _productId) : null;
        // نفس أولوية UnitsPolicy.CartonWeight في الخدمة: العبوة الصريحة أولاً،
        // ثم وزن بطاقة الصنف، ثم عدد القوالب × وزن القالب. لا نختار أول عبوة عامة تلقائياً.
        double packW = 0;
        if (pack?.UnitWeightKg > 0) packW = pack.UnitWeightKg;
        else if (prod != null && prod.CartonWeightKg > 0) packW = prod.CartonWeightKg;
        else if (prod != null && prod.MoldsCount > 0 && prod.MoldWeightKg > 0) packW = prod.MoldsCount * prod.MoldWeightKg;
        else if (prod != null && prod.DefaultCartonWeightKg > 0) packW = prod.DefaultCartonWeightKg;
        else if (PackWeight > 0) packW = PackWeight;

        PackWeight = packW;
        // اتبع تعريف العبوة المكتمل عند اختيارها، وهو نفس مرجع UnitsPolicy.PackagingDefinition؛
        // عند غيابه تُستخدم مواصفة بطاقة الصنف ولا يُختار قالب عام بالتخمين.
        MoldsCount = pack != null && pack.MoldsCount > 0 && pack.MoldWeightKg > 0
            ? pack.MoldsCount : (prod?.MoldsCount ?? 0);
        ComputedKg = ctn > 0 && packW > 0 ? Math.Round(ctn * packW, 2) : 0;
        // احتياج الخام يُحسب تلقائياً من وزن المنتج التام + نسبة الفرز/الفاقد للسطر (أو التخصيص المباشر للدفعة) دون معامل بطاقة الصنف.
        double conversionFactor = _productId is int finishedProductId ? EffectiveRawFactor(finishedProductId) : 1.0;
        double computedRaw = _customRawRequiredKg is double customRaw && customRaw > 0.001
            ? customRaw
            : (ComputedKg > 0 ? Math.Round(ComputedKg * conversionFactor * (1.0 + _wastePercent / 100.0), 2) : 0);
        double rawFactor = _productId == null ? 0 : (ComputedKg > 0.001 && computedRaw > 0 ? computedRaw / ComputedKg : 1.0);
        RawKgPerFinishedKg = rawFactor;
        OnChange(nameof(RawKgPerFinishedKg));
        RawRequiredKg = _productId == null ? 0 : computedRaw;
        OnChange(nameof(RawRequiredInputText));

        // §1.50.66 — تحقق فوري من تطابق وزن العبوة بين بطاقة الصنف والعبوة المحددة
        if (prod != null && pack != null && prod.CartonWeightKg > 0 && pack.UnitWeightKg > 0)
        {
            double diff = Math.Abs(prod.CartonWeightKg - pack.UnitWeightKg);
            double tol = Math.Max(0.5, prod.CartonWeightKg * 0.05);
            if (diff > tol)
            {
                QuantityError = $"⚠️ تعارض: وزن الكرتون في بطاقة الصنف ({prod.CartonWeightKg:N1} كجم) يختلف عن وزن العبوة المحددة ({pack.UnitWeightKg:N1} كجم) للصنف «{prod.Name}». سيُستخدم وزن العبوة المحددة ({packW:N1} كجم)؛ راجع التعريفين قبل التخطيط.";
                OnChange(nameof(QuantityError));
            }
            else
            {
                if (QuantityError != null && QuantityError.Contains("وزن الكرتون في بطاقة الصنف"))
                {
                    QuantityError = null;
                    OnChange(nameof(QuantityError));
                }
            }
        }

        if (Ctx == null)
        {
            // بند بلا شحنة/دفعة (إدخال يدوي) — لا تتبع خام، ولا يُرسل أي سحب إلى الخلفية.
            SourceUnit = null; SourceUnitWeightKg = 0; SourceQtyKg = 0; SourceQtyInUnit = 0;
            RawShortageKg = RawRequiredKg;
            RawSufficient = RawRequiredKg <= 0;
            _sourceError = RawRequiredKg > 0 ? "اربط البند بدفعة خام ومخزن ليُحجز الخام." : null;
            RawStatusText = RawRequiredKg > 0 ? "اربط دفعة خام" : "—";
        }
        else
        {
            // الوحدة المختارة هي إما الكجم أو وحدة استلام هذه الدفعة حصراً؛
            // تُشتق كمية الخام بالوحدة المختارة ثم تظهر الكراتين/الوزن التام تلقائياً.
            double uw = EffectiveSourceUnitWeightKg;
            bool byKg = string.Equals(SourceMode, "كجم", StringComparison.OrdinalIgnoreCase);
            double effectiveRaw = RawRequiredKg > 0 ? RawRequiredKg
                : (_pendingSourceUnits is double pu && pu > 0 ? Math.Round(pu * (byKg ? 1.0 : uw), 2) : 0);
            SourceUnit = SourceMode;
            SourceUnitWeightKg = byKg ? 1 : uw;
            SourceQtyKg = effectiveRaw;
            SourceQtyInUnit = _pendingSourceUnits is double pUnits && pUnits > 0 && RawRequiredKg <= 0
                ? pUnits
                : (byKg ? effectiveRaw : (uw > 0 ? Math.Round(effectiveRaw / uw, 2) : 0));

            double availKg = Ctx.AvailableKg;
            var date = DateValue?.Date;
            var untilT = TreatmentBlockUntil();
            bool treatMatures = TreatmentRequired && UnderTreatmentKg > 0 && untilT.HasValue
                && date.HasValue && date.Value >= untilT.Value;
            // §بعد تاريخ المعالجة تُضاف الكمية التي ستكتمل إلى المتاح؛ قبلها تُحسب المتاح فقط.
            double availForRow = treatMatures && !AvailabilityIncludesDateTreatment ? availKg + UnderTreatmentKg : availKg;
            RawShortageKg = Math.Max(0, effectiveRaw - availForRow);
            RawSufficient = effectiveRaw > 0 && effectiveRaw <= availForRow + 0.001;
            _sourceError = effectiveRaw <= 0 ? null
                : RawSufficient ? null
                : $"غير كافٍ — المطلوب {effectiveRaw:N1} كجم / المتاح {availForRow:N1} كجم (عجز {RawShortageKg:N1} كجم)";
            RawStatusText = effectiveRaw <= 0 ? "أدخل كمية التخطيط"
                : (_productId == null ? "اختر الصنف التام لإكمال الحساب" : (RawSufficient ? "متوفر ✓" : "غير كافٍ ❌"));
        }
        OnChange(nameof(PackWeight)); OnChange(nameof(PackWeightDisplay)); OnChange(nameof(UnitDisplay)); OnChange(nameof(MoldsCount));
        OnChange(nameof(ComputedKg)); OnChange(nameof(FinishedWeightDisplay)); OnChange(nameof(RawRequirementDisplay)); OnChange(nameof(ProductCapacityDisplay));
        OnChange(nameof(RawRequiredKg)); OnChange(nameof(RawShortageKg)); OnChange(nameof(RawSufficient));
        OnChange(nameof(RawStatusText)); OnChange(nameof(SourceError)); OnChange(nameof(PlannedSourceUnitsText));
        OnChange(nameof(ContextDisplay));
        RecalcTreatment();
    }

    private string _capAlert = "حدد الصنف والكمية…";
    public string CapAlert { get => QuantityError ?? Capacity?.Error ?? (Capacity == null ? "اختر الصنف والكمية" : $"الحد الأقصى {Capacity.MaximumCartons:N0} كرتون | المتبقي {Capacity.RemainingCartons:N0}"); private set { _capAlert = value; OnChange(nameof(CapAlert)); } }

    /// <summary>§v1.50.35 — طاقة الصنف بجوار الصنف التام في نافذة الاختيار.</summary>
    public string ProductCapacityDisplay
    {
        get
        {
            if (Capacity != null && Capacity.ProductCapacity > 0)
                return Capacity.ProductCapacity.ToString("N0") + " كرتون/وردية";
            var prod = _productId != null ? AllProducts.FirstOrDefault(p => p.Id == _productId) : null;
            if (prod != null && prod.HourlyRate > 0) return prod.HourlyRate.ToString("N0") + " كرتون/س";
            return "—";
        }
    }
    public bool IsOverCapacity => QuantityError != null || Capacity?.Error != null;
    public bool IsInvalid => !string.IsNullOrEmpty(QuantityError) || IsOverCapacity;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChange(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class ProductOption
{
    public int Id { get; set; }
    public string Name { get; set; }
    /// <summary>اسم الاختيار مع وزن الكرتون الفعلي للعبوة الافتراضية، لا رقم ثابت في الواجهة.</summary>
    public string SelectionDisplayName => DefaultCartonWeightKg > 0
        ? $"{Name} — {DefaultCartonWeightKg:N1} كجم/كرتون"
        : Id > 0 ? $"{Name} — وزن العبوة غير معرّف" : Name;
    public double DefaultCartonWeightKg { get; set; }
    /// <summary>§v1.50.35 — مواصفات بطاقة الصنف التام (شاشة الأصناف) لا عبوة عامة.</summary>
    public double CartonWeightKg { get; set; }
    public int MoldsCount { get; set; }
    public double MoldWeightKg { get; set; }
    public double HourlyRate { get; set; }
    /// <summary>كجم خام لكل كجم منتج تام من قاعدة التحويل الرسمية.</summary>
    public double RawKgPerFinishedKg { get; set; }
    public int? DefaultPackagingTypeId { get; set; }
}
/// <summary>§B92 — خيار وردية في الاختيار اليدوي (الاسم يتضمن الساعات الفعالة لقرار الإدارة).</summary>
public class ShiftOption { public int Id { get; set; } public string Name { get; set; } }
public class PackOption { public int Id { get; set; } public string Name { get; set; } public double UnitWeightKg { get; set; } public int MoldsCount { get; set; } public double MoldWeightKg { get; set; } }


public class PlanRowUi : System.ComponentModel.INotifyPropertyChanged
{
    public Func<int, string> QuantityGuard { get; set; }
    public string QuantityError { get; private set; }
    private PlanCapacityRow _capacity;
    public PlanCapacityRow Capacity { get => _capacity; set { _capacity = value; OnChanged(nameof(Capacity)); } }
    private void GuardQuantity(int quantity)
    {
        QuantityError = quantity < 0 ? "الكمية لا يمكن أن تكون سالبة." : QuantityGuard?.Invoke(quantity);
        OnChanged(nameof(QuantityError));
        if (!string.IsNullOrEmpty(QuantityError)) throw new ArgumentException(QuantityError);
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));

    private int _no;
    public int No { get => _no; set { if (_no != value) { _no = value; OnChanged(nameof(No)); } } }

    /// <summary>
    /// §B108 — معرّف بند الخطة المحفوظ (0 = صف جديد لم يُحفظ بعد).
    /// </summary>
    public int ItemId { get; set; }

    public int? CustomerId { get; set; }
    public string CustomerName { get; set; }
    public int? ShipmentId { get; set; }
    public string ShipmentNo { get; set; }
    /// <summary>المخزن الظاهر لمصدر الخام في هذا الصف.</summary>
    public int? WarehouseId { get; set; }
    public string WarehouseName { get; set; }
    public int? LotId { get; set; }
    public int? RawProductId { get; set; }
    public string LotCode { get; set; }
    public string RawName { get; set; }
    public double OnHandKg { get; set; }
    public double ReservedKg { get; set; }
    public double OtherCommitmentsKg { get; set; }
    public double UnderTreatmentKg { get; set; }
    public DateTime? TreatmentReadyDate { get; set; }
    public string TreatmentStatus => UnderTreatmentKg > 0
        ? $"تحت المعالجة: {UnderTreatmentKg:N1} كجم" + (TreatmentReadyDate.HasValue ? $" حتى {TreatmentReadyDate:dd/MM/yyyy}" : "")
        : "جاهز/لا معالجة";
    public double AvailableKg { get; set; }
    private double _allocationSelectedQtyKg;
    public double AllocationSelectedQtyKg
    {
        get => _allocationSelectedQtyKg;
        set
        {
            if (Math.Abs(_allocationSelectedQtyKg - value) < 0.001) return;
            _allocationSelectedQtyKg = value;
            OnChanged(nameof(AllocationSelectedQtyKg));
            OnChanged(nameof(SelectedQtyKg));
            OnChanged(nameof(RawNeedDisplay));
            OnChanged(nameof(RawPlanStatus));
        }
    }
    private double _rawKgPerFinishedKg = 1.0;
    public double RawKgPerFinishedKg
    {
        get => _rawKgPerFinishedKg > 0 ? _rawKgPerFinishedKg : 1.0;
        set
        {
            double effective = value > 0 ? value : 1.0;
            if (Math.Abs(_rawKgPerFinishedKg - effective) < 0.000001) return;
            _rawKgPerFinishedKg = effective;
            OnChanged(nameof(RawKgPerFinishedKg));
            OnChanged(nameof(RawRequiredKg));
            OnChanged(nameof(RawRequiredInputText));
            OnChanged(nameof(SelectedQtyKg));
            OnChanged(nameof(RawNeedDisplay));
            OnChanged(nameof(RawPlanStatus));
            OnChanged(nameof(RemainingAfterKg));
            OnChanged(nameof(PlanningStatus));
        }
    }
    private double _wastePercent;
    private string _wastePercentText = "0";
    public double WastePercent => _wastePercent;
    public string WastePercentText
    {
        get => _wastePercentText;
        set
        {
            var norm = string.IsNullOrWhiteSpace(value) ? "0" : value.Trim();
            if (_wastePercentText == norm) return;
            _wastePercentText = norm;
            if (double.TryParse(norm, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var wp)
                || double.TryParse(norm, out wp))
            {
                _wastePercent = Math.Max(0, Math.Min(500, wp));
                _customRawRequiredKg = null;
                NotifyRawChanged();
            }
            OnChanged(nameof(WastePercentText));
            OnChanged(nameof(WastePercent));
        }
    }
    private double? _customRawRequiredKg;
    public double? CustomRawRequiredKg => _customRawRequiredKg;
    public string RawRequiredInputText
    {
        get => RawRequiredKg > 0 ? RawRequiredKg.ToString("0.#") : "";
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                _customRawRequiredKg = null;
                NotifyRawChanged();
                return;
            }
            if ((double.TryParse(value.Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var rk)
                || double.TryParse(value.Trim(), out rk)) && rk > 0)
            {
                _customRawRequiredKg = Math.Round(rk, 2);
                if (QtyKg > 0.001)
                {
                    _wastePercent = Math.Max(0, Math.Round(((_customRawRequiredKg.Value - QtyKg) / QtyKg) * 100.0, 2));
                    _wastePercentText = _wastePercent.ToString("0.#");
                    OnChanged(nameof(WastePercent));
                    OnChanged(nameof(WastePercentText));
                }
                NotifyRawChanged();
            }
        }
    }
    public void ApplySavedRawRequirement(double savedRawKg, double? explicitWastePercent = null, double? explicitCustomRawKg = null)
    {
        _savedRawRequiredKg = savedRawKg;
        if (explicitCustomRawKg is double custom && custom > 0.001)
        {
            _customRawRequiredKg = Math.Round(custom, 2);
            if (QtyKg > 0.001)
            {
                _wastePercent = Math.Max(0, Math.Round(((custom - QtyKg) / QtyKg) * 100.0, 2));
                _wastePercentText = _wastePercent.ToString("0.#");
            }
        }
        else if (explicitWastePercent is double wp && wp > 0)
        {
            _wastePercent = Math.Round(wp, 2);
            _wastePercentText = _wastePercent.ToString("0.#");
            _customRawRequiredKg = null;
        }
        else if (savedRawKg > 0.001 && QtyKg > 0.001 && Math.Abs(savedRawKg - QtyKg) > 0.05)
        {
            _customRawRequiredKg = Math.Round(savedRawKg, 2);
            _wastePercent = Math.Max(0, Math.Round(((savedRawKg - QtyKg) / QtyKg) * 100.0, 2));
            _wastePercentText = _wastePercent.ToString("0.#");
        }
        NotifyRawChanged();
        OnChanged(nameof(WastePercent));
        OnChanged(nameof(WastePercentText));
        OnChanged(nameof(SavedRawRequiredKg));
    }
    private void NotifyRawChanged()
    {
        SyncSourceDrawFromRawRequirement();
        OnChanged(nameof(RawRequiredKg));
        OnChanged(nameof(RawRequiredInputText));
        OnChanged(nameof(SourceQtyInputText));
        OnChanged(nameof(SourceDisplay));
        OnChanged(nameof(AvailableDisplay));
        OnChanged(nameof(AvailableLotUnitDisplay));
        OnChanged(nameof(SelectedQtyKg));
        OnChanged(nameof(RawNeedDisplay));
        OnChanged(nameof(RawPlanStatus));
        OnChanged(nameof(RemainingAfterKg));
        OnChanged(nameof(PlanningStatus));
    }
    private double _savedRawRequiredKg;
    /// <summary>Requirement saved with this item.</summary>
    public double SavedRawRequiredKg
    {
        get => _savedRawRequiredKg;
        set
        {
            if (Math.Abs(_savedRawRequiredKg - value) < 0.001) return;
            _savedRawRequiredKg = value;
            OnChanged(nameof(SavedRawRequiredKg));
            OnChanged(nameof(SelectedQtyKg));
            OnChanged(nameof(RawNeedDisplay));
            OnChanged(nameof(RawPlanStatus));
        }
    }
    public string RawPlanStatus
    {
        get
        {
            double need = RawRequiredKg;
            if (need <= 0.001) return "أدخل كمية الإنتاج";
            if (LotId != null && need > AvailableForPlanningDateKg + 0.001)
                return $"عجز خام ({need - AvailableForPlanningDateKg:N1} كجم) ❌";
            if (ItemId > 0 && Math.Abs(SavedRawRequiredKg - need) > 0.05)
                return "تغيّر الاحتياج — احفظ التحديث";
            if (ItemId <= 0)
                return LotId != null ? "جاهز للحجز عند الحفظ ✓" : "محسوب (بلا دفعة خام)";
            if (AllocationSelectedQtyKg > 0.001)
                return "محسوب ومخصص ✓";
            return LotId != null ? "محسوب ومرتبط بالدفعة ✓" : "محسوب — بلا دفعة خام";
        }
    }
    /// <summary>المتاح بحسب تاريخ بند الخطة؛ يضم الخام الذي تنتهي معالجته قبله.</summary>
    public double AvailableForPlanningDateKg
        => UnderTreatmentKg > 0 && TreatmentReadyDate.HasValue && DateValue.HasValue
            && DateValue.Value.Date >= TreatmentReadyDate.Value.Date
            ? AvailableKg + UnderTreatmentKg
            : AvailableKg;
    /// <summary>احتياج الخام المباشر للسطر: مخصص يدوياً أو محسوب تلقائياً من وزن التام + نسبة الفرز%.</summary>
    public double RawRequiredKg => _customRawRequiredKg is double custom && custom > 0.001
        ? custom
        : (QtyKg > 0 ? Math.Round(QtyKg * (1.0 + _wastePercent / 100.0), 2) : 0);
    public string PlanningStatus => RawRequiredKg <= AvailableForPlanningDateKg + 0.001 ? "كافٍ" : "غير كافٍ";
    public double SelectedQtyKg => RawRequiredKg > 0 ? RawRequiredKg : AllocationSelectedQtyKg;
    public string RawNeedDisplay => RawRequiredKg > 0 ? $"{RawRequiredKg:N1} كجم" : "—";
    public int ProductId { get; set; }
    public string ProductName { get; set; }
    private int? _planPackId;
    public int? PackId { get => _planPackId; set { _planPackId = value; OnChanged(nameof(PackId)); OnChanged(nameof(RawNeedDisplay)); OnChanged(nameof(RawPlanStatus)); } }
    /// <summary>Packaging selection used when this row's displayed official factor was loaded.</summary>
    private int? _rawFactorPackagingTypeId;
    public int? RawFactorPackagingTypeId
    {
        get => _rawFactorPackagingTypeId;
        set
        {
            if (_rawFactorPackagingTypeId == value) return;
            _rawFactorPackagingTypeId = value;
            OnChanged(nameof(RawFactorPackagingTypeId));
            OnChanged(nameof(RawNeedDisplay));
            OnChanged(nameof(RawPlanStatus));
        }
    }
    public string PackName { get; set; }
    /// <summary>§B80: وحدة الصنف التام كما في بطاقته (شاشة الأصناف) — مثل «كرتون 5كجم».</summary>
    public string UnitDisplay { get; set; }
    private double _qtyKg;
    public double QtyKg 
    { 
        get => _qtyKg; 
        set 
        { 
            if (Math.Abs(_qtyKg - value) > 0.001) 
            { 
                // §1.50.68 FIX: إذا غيّر المستخدم الكيلو يدوياً، أعد حساب الكراتين تلقائياً
                // لأن الكرتون هو الوحدة الأساسية - يمنع خطأ "لا تطابق عدد الكراتين"
                if (CartonWeight > 0 && value > 0)
                {
                    int newCartons = (int)Math.Round(value / CartonWeight);
                    if (newCartons > 0 && newCartons != _cartons)
                    {
                        _cartons = newCartons;
                        _cartonsText = newCartons.ToString();
                        OnChanged(nameof(Cartons));
                        OnChanged(nameof(CartonsText));
                    }
                }
                _qtyKg = value;
                if (_customRawRequiredKg is double custom && custom > 0.001 && value > 0.001)
                {
                    _wastePercent = Math.Max(0, Math.Round(((custom - value) / value) * 100.0, 2));
                    _wastePercentText = _wastePercent.ToString("0.#");
                    OnChanged(nameof(WastePercent));
                    OnChanged(nameof(WastePercentText));
                }
                OnChanged(nameof(QtyKg));
                OnChanged(nameof(RawRequiredKg));
                OnChanged(nameof(RawRequiredInputText));
                OnChanged(nameof(RawPlanStatus));
                OnChanged(nameof(PlanningStatus));
                OnChanged(nameof(SelectedQtyKg));
                OnChanged(nameof(RawNeedDisplay));
                OnChanged(nameof(RemainingAfterKg));
                ValidateCartonKgImmediate();
            } 
        } 
    }
    /// <summary>§B58: وزن كرتون الصنف لاشتقاق الكيلو عند تعديل الكراتين داخل الجدول.</summary>
    public double CartonWeight { get; set; }
    private int _cartons;
    public int Cartons { get => _cartons; set { if (_cartons != value) { GuardQuantity(value); _cartons = value; _cartonsText = value.ToString(); OnChanged(nameof(Cartons)); OnChanged(nameof(CartonsText)); ValidateCartonKgImmediate(); } } }
    private bool _syncingPlanQuantities;
    private string _cartonsText = "0";
    /// <summary>§1.50.66: تحرير الكراتين داخل الجدول يعيد حساب الوزن المكافئ آلياً + تحقق فوري.</summary>
    public string CartonsText
    {
        get => _cartonsText;
        set
        {
            string norm = DatesErp.Core.Common.UiFormat.NormalizeDigits(value)?.Trim() ?? "0";
            if (DatesErp.Core.Common.UiFormat.TryParseInt(norm, out var proposed))
            {
                QuantityError = null;
                GuardQuantity(proposed);
            }
            else { QuantityError = "أدخل عدداً صحيحاً من الكراتين."; OnChanged(nameof(QuantityError)); }
            if (_cartonsText == norm) return;
            if (!_syncingPlanQuantities) _customRawRequiredKg = null;
            _cartonsText = norm; OnChanged(nameof(CartonsText));
            if (DatesErp.Core.Common.UiFormat.TryParseInt(norm, out var c) && c >= 0)
            {
                _cartons = c; OnChanged(nameof(Cartons));
                if (CartonWeight > 0) { QtyKg = Math.Round(c * CartonWeight, 1); OnChanged(nameof(QtyKg)); }
                NotifyRawChanged();
                ValidateCartonKgImmediate();
            }
        }
    }

    private void ValidateCartonKgImmediate()
    {
        try
        {
            if (Cartons <= 0 || CartonWeight <= 0 || QtyKg <= 0) 
            {
                if (QuantityError != null && QuantityError.Contains("لا تطابق عدد الكراتين")) { QuantityError = null; OnChanged(nameof(QuantityError)); }
                return;
            }
            double computed = Math.Round(Cartons * CartonWeight, 1);
            double tol = Math.Max(1.0, QtyKg * 0.02);
            if (Math.Abs(QtyKg - computed) > tol)
            {
                // §1.50.68 FIX: بدل منع الحفظ، صحح تلقائياً لأن الكرتون هو الأساس
                // في الصورة: 3000 كجم و 4000 كرتون × 2.5 = 10000 كجم → نصحح الكيلو إلى 10000
                // أو إذا المستخدم أدخل كيلو، نكون قد صححنا الكراتين في setter أعلاه
                // هنا نصحح الكيلو ليطابق الكراتين (الوحدة الأساسية)
                _qtyKg = computed;
                OnChanged(nameof(QtyKg));
                OnChanged(nameof(RawRequiredKg));
                OnChanged(nameof(SelectedQtyKg));
                OnChanged(nameof(RawNeedDisplay));
                OnChanged(nameof(RawPlanStatus));
                OnChanged(nameof(PlanningStatus));
                OnChanged(nameof(RemainingAfterKg));
                QuantityError = null;
                OnChanged(nameof(QuantityError));
            }
            else
            {
                if (QuantityError != null && QuantityError.Contains("لا تطابق عدد الكراتين")) { QuantityError = null; OnChanged(nameof(QuantityError)); }
            }
        }
        catch { }
    }

    private string _date;
    public string Date
    {
        get => _date;
        set
        {
            if (_date == value) return;
            _date = value; OnChanged(nameof(Date));
            // §B80: المزامنة مع DatePicker — تحرير نصي أو اختيار من التقويم يحدّث الطرفين
            if (DatesErp.Core.Common.UiFormat.TryParseDate(value, out var dv)) { _dateValue = dv; OnChanged(nameof(DateValue)); }
        }
    }
    private DateTime? _dateValue;
    /// <summary>§B80: تاريخ الإنتاج كتاريخ حقيقي لعمود DatePicker — قابل للتعديل دائماً في الجدول.</summary>
    public DateTime? DateValue
    {
        get => _dateValue;
        set
        {
            if (_dateValue == value) return;
            _dateValue = value; OnChanged(nameof(DateValue));
            _date = value?.ToString("dd/MM/yyyy") ?? ""; OnChanged(nameof(Date));
            OnChanged(nameof(AvailableForPlanningDateKg));
            OnChanged(nameof(PlanningStatus));
            OnChanged(nameof(RemainingAfterKg));
        }
    }
    private int _shiftId;
    public int ShiftId { get => _shiftId; set { if (_shiftId != value) { _shiftId = value; OnChanged(nameof(ShiftId)); } } }
    public string ShiftName { get; set; }
    public string LineName { get; set; }
    private int _lineId;
    public int LineId { get => _lineId; set { if (_lineId != value) { _lineId = value; OnChanged(nameof(LineId)); } } }
    public int Priority { get; set; }

    // ═══ §تتبع سحب الخام من الشحنة والتخطيط بوحدة الاستلام (كجم/كرتون/سلة) + تعدد المخازن ═══
    public List<PlanItemAllocationDto> PreservedAllocations { get; set; } = new();
    public string LotReceiptUnit { get; set; } = "كجم";
    public double LotReceiptUnitWeightKg { get; set; } = 1.0;

    public string SourceUnit { get; set; } = "كجم";
    public double SourceQtyInUnit { get; set; }
    public double SourceUnitWeightKg { get; set; } = 1.0;
    private double _sourceQtyKg;
    public double SourceQtyKg { get => _sourceQtyKg; set { _sourceQtyKg = value; OnChanged(nameof(SourceQtyKg)); OnChanged(nameof(SelectedQtyKg)); OnChanged(nameof(RawNeedDisplay)); OnChanged(nameof(RawPlanStatus)); OnChanged(nameof(RemainingAfterKg)); OnChanged(nameof(AvailableDisplay)); OnChanged(nameof(AvailableLotUnitDisplay)); OnChanged(nameof(SourceDisplay)); OnChanged(nameof(SourceQtyInputText)); } }

    public void ConfigureReceiptUnitContext(string? lotReceiptUnit, double lotUnitWeightKg, string? activeSourceUnit = null)
    {
        string normReceipt = string.IsNullOrWhiteSpace(lotReceiptUnit) || lotReceiptUnit == "—" ? "كجم" : lotReceiptUnit.Trim();
        double normUw = string.Equals(normReceipt, "كجم", StringComparison.OrdinalIgnoreCase)
            ? 1.0
            : (lotUnitWeightKg > 0 ? lotUnitWeightKg : 1.0);
        LotReceiptUnit = normReceipt;
        LotReceiptUnitWeightKg = normUw;

        var modes = new List<string> { normReceipt };
        if (!string.Equals(normReceipt, "كجم", StringComparison.OrdinalIgnoreCase))
            modes.Add("كجم");
        SourceModes = modes;

        string chosen = !string.IsNullOrWhiteSpace(activeSourceUnit) && modes.Contains(activeSourceUnit.Trim())
            ? activeSourceUnit.Trim()
            : normReceipt;
        _sourceMode = chosen;
        SyncSourceDrawFromRawRequirement();
        OnChanged(nameof(SourceModes));
        OnChanged(nameof(SourceMode));
        OnChanged(nameof(SourceQtyInputText));
        OnChanged(nameof(AvailableLotUnitDisplay));
        OnChanged(nameof(AvailableDisplay));
        OnChanged(nameof(SourceDisplay));
    }

    // §1.50.78 — وحدة الاستلام/السحب selectable في الجدول الرئيسي + إدخال الكمية بوحدة الاستلام مباشرةً
    public List<string> SourceModes { get; set; } = new() { "كجم" };
    private string _sourceMode = "كجم";
    public string SourceMode 
    { 
        get => _sourceMode; 
        set 
        { 
            if (_sourceMode == value || string.IsNullOrWhiteSpace(value)) return; 
            _sourceMode = value; 
            OnChanged(nameof(SourceMode)); 
            RecalcSourceQtyFromMode();
            OnChanged(nameof(SourceQtyInputText));
            OnChanged(nameof(AvailableLotUnitDisplay));
            OnChanged(nameof(AvailableDisplay));
            OnChanged(nameof(SourceDisplay));
        } 
    }

    private double EffectiveRowUnitWeightKg
    {
        get
        {
            if (string.Equals(SourceMode, "كجم", StringComparison.OrdinalIgnoreCase)) return 1.0;
            if (LotReceiptUnitWeightKg > 0 && LotReceiptUnitWeightKg != 1.0) return LotReceiptUnitWeightKg;
            if (SourceUnitWeightKg > 0 && SourceUnitWeightKg != 1.0) return SourceUnitWeightKg;
            return LotReceiptUnitWeightKg > 0 ? LotReceiptUnitWeightKg : 1.0;
        }
    }

    public string SourceQtyInputText
    {
        get
        {
            double raw = RawRequiredKg > 0 ? RawRequiredKg : SourceQtyKg;
            if (raw <= 0.001) return "";
            bool byKg = string.Equals(SourceMode, "كجم", StringComparison.OrdinalIgnoreCase);
            if (byKg) return raw.ToString("0.#");
            double uw = EffectiveRowUnitWeightKg;
            double units = uw > 0 ? Math.Round(raw / uw, 2) : raw;
            return units > 0 ? units.ToString("0.##") : "";
        }
        set
        {
            if (_syncingPlanQuantities) return;
            var trimmed = value?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(trimmed)) return;
            if ((double.TryParse(trimmed, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var units)
                || double.TryParse(trimmed, out units)) && units > 0)
            {
                bool byKg = string.Equals(SourceMode, "كجم", StringComparison.OrdinalIgnoreCase);
                double uw = EffectiveRowUnitWeightKg;
                double rawKg = byKg ? Math.Round(units, 2) : Math.Round(units * uw, 2);
                _customRawRequiredKg = rawKg;
                if (CartonWeight > 0)
                {
                    double netFinishedKg = rawKg / (1.0 + _wastePercent / 100.0);
                    int ctn = (int)Math.Floor(netFinishedKg / CartonWeight + 1e-6);
                    if (ctn <= 0 && netFinishedKg > 0.001) ctn = 1;
                    _syncingPlanQuantities = true;
                    try
                    {
                        GuardQuantity(ctn);
                        _cartons = ctn;
                        _cartonsText = ctn.ToString();
                        _qtyKg = Math.Round(ctn * CartonWeight, 1);
                        OnChanged(nameof(Cartons));
                        OnChanged(nameof(CartonsText));
                        OnChanged(nameof(QtyKg));
                    }
                    finally { _syncingPlanQuantities = false; }
                }
                NotifyRawChanged();
            }
        }
    }

    public string AvailableLotUnitDisplay
    {
        get
        {
            if (LotId == null) return "—";
            double avail = AvailableForPlanningDateKg;
            bool byKg = string.Equals(SourceMode, "كجم", StringComparison.OrdinalIgnoreCase)
                || string.Equals(LotReceiptUnit, "كجم", StringComparison.OrdinalIgnoreCase);
            if (byKg) return $"{avail:N0} كجم";
            double uw = EffectiveRowUnitWeightKg;
            double units = uw > 0 ? Math.Floor(avail / uw) : avail;
            return $"{units:N0} {LotReceiptUnit} ({avail:N0} كجم)";
        }
    }

    public string AvailableDisplay 
    { 
        get 
        {
            double raw = RawRequiredKg > 0 ? RawRequiredKg : SourceQtyKg;
            if (raw <= 0) return "—";
            bool byKg = string.Equals(SourceMode, "كجم", StringComparison.OrdinalIgnoreCase);
            if (byKg) return $"{raw:N0} كجم";
            double uw = EffectiveRowUnitWeightKg;
            double units = uw > 0 ? raw / uw : SourceQtyInUnit;
            return $"{units:0.##} {LotReceiptUnit} / {raw:N0} كجم";
        }
    }
    public string SourceDisplay
    {
        get
        {
            double raw = RawRequiredKg > 0 ? RawRequiredKg : SourceQtyKg;
            if (raw <= 0) return "—";
            bool byKg = string.Equals(SourceMode, "كجم", StringComparison.OrdinalIgnoreCase);
            if (byKg) return $"{raw:N0} كجم";
            double uw = EffectiveRowUnitWeightKg;
            double units = uw > 0 ? raw / uw : SourceQtyInUnit;
            return $"{units:0.##} {LotReceiptUnit} ({raw:N0} كجم)";
        }
    }
    private void SyncSourceDrawFromRawRequirement()
    {
        double raw = RawRequiredKg;
        if (raw > 0.001) _sourceQtyKg = raw;
        RecalcSourceQtyFromMode();
    }
    private void RecalcSourceQtyFromMode()
    {
        double raw = RawRequiredKg > 0.001 ? RawRequiredKg : _sourceQtyKg;
        if (raw <= 0) return;
        bool byKg = string.Equals(SourceMode, "كجم", StringComparison.OrdinalIgnoreCase);
        if (byKg)
        {
            SourceUnit = "كجم";
            SourceUnitWeightKg = 1.0;
            SourceQtyKg = raw;
            SourceQtyInUnit = Math.Round(raw, 2);
        }
        else
        {
            double uw = EffectiveRowUnitWeightKg;
            SourceUnit = string.IsNullOrWhiteSpace(LotReceiptUnit) ? "كجم" : LotReceiptUnit;
            SourceUnitWeightKg = uw > 0 ? uw : 1.0;
            SourceQtyKg = raw;
            SourceQtyInUnit = uw > 0 ? Math.Round(raw / uw, 2) : Math.Round(raw, 2);
        }
        OnChanged(nameof(SourceUnit));
        OnChanged(nameof(SourceUnitWeightKg));
        OnChanged(nameof(SourceQtyInUnit));
    }

    // §1.50.57 — المتبقي بعد التخطيط: المتاح - المجدول — يجيب سؤال المستخدم "عند إضافة صنف جديد كم ستظهر كميته"
    public double RemainingAfterKg => Math.Max(0, AvailableForPlanningDateKg - RawRequiredKg);
    public bool IsInvalid => !string.IsNullOrEmpty(QuantityError) || Cartons <= 0;
}

/// <summary>حجوزات خام مؤقتة لبنود مسودة التخطيط؛ تجمعها حسب المخزن والدفعة قبل الحفظ.</summary>
public static class PlanningDraftRawReservationCalculator
{
    public static Dictionary<(int WarehouseId, int LotId), double> Calculate(IEnumerable<PlanRowUi> rows)
    {
        var result = new Dictionary<(int WarehouseId, int LotId), double>();
        foreach (var row in rows?.Where(r => r.LotId != null) ?? Enumerable.Empty<PlanRowUi>())
        {
            double need = row.RawRequiredKg > 0.001 ? row.RawRequiredKg : row.SourceQtyKg;
            if (need <= 0.001) continue;
            var allocations = row.PreservedAllocations?.Where(a => a.WarehouseId is > 0 && a.AllocatedQtyKg > 0.001).ToList()
                ?? new List<PlanItemAllocationDto>();
            if (allocations.Count > 0)
            {
                double original = allocations.Sum(a => a.AllocatedQtyKg);
                double allocated = 0;
                for (int i = 0; i < allocations.Count; i++)
                {
                    var allocation = allocations[i];
                    double part = i == allocations.Count - 1
                        ? Math.Max(0, need - allocated)
                        : Math.Round(need * allocation.AllocatedQtyKg / original, 3);
                    allocated += part;
                    Add(result, (allocation.WarehouseId!.Value, allocation.LotId), part);
                }
            }
            else if (row.WarehouseId is int warehouseId && warehouseId > 0)
            {
                Add(result, (warehouseId, row.LotId!.Value), need);
            }
        }
        return result;
    }

    private static void Add(Dictionary<(int WarehouseId, int LotId), double> result,
        (int WarehouseId, int LotId) key, double quantityKg)
    {
        result.TryGetValue(key, out var current);
        result[key] = current + quantityKg;
    }
}
