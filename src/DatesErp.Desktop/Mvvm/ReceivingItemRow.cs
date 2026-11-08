#nullable enable annotations
using System.ComponentModel;
using DatesErp.Core.Common;

namespace DatesErp.Desktop.Mvvm;

/// <summary>
/// صف استلام — نموذج الإدخال المباشر من الجدول نفسه (1.50.54).
/// اختيار الصنف من رقم الصنف أو اسم الصنف داخل الصف، والنظام يجلب بياناته تلقائياً.
/// البيانات الأساسية (رقم/اسم/وحدة) للقراءة فقط بعد الاختيار.
/// </summary>
public sealed class ReceivingItemRow : INotifyPropertyChanged
{
    private int _rowNo;
    private bool _isEditable;
    private bool? _treatmentRequired;
    private DateTime? _until;
    private string _status = "مستلم", _state;
    private int _productId;
    private int? _packId;
    private string _productCode = "";
    private string _productName = "";
    private string _packName = "";
    private string _receiptUnit = "";
    private int _packageCount;
    private double _unitWeightKg;
    private double _qtyKg;

    public int ShipmentItemId { get; set; }

    public int RowNo { get => _rowNo; set { _rowNo = value; Raise(nameof(RowNo)); } }

    public int ProductId
    {
        get => _productId;
        set { _productId = value; Raise(nameof(ProductId)); Raise(nameof(IsEmptyRow)); Raise(nameof(DisplayCode)); Raise(nameof(DisplayName)); }
    }

    public int? PackId
    {
        get => _packId;
        set { _packId = value; Raise(nameof(PackId)); }
    }

    public string ProductCode
    {
        get => _productCode;
        set { _productCode = value ?? ""; Raise(nameof(ProductCode)); Raise(nameof(DisplayCode)); }
    }

    public string ProductName
    {
        get => _productName;
        set { _productName = value ?? ""; Raise(nameof(ProductName)); Raise(nameof(DisplayName)); }
    }

    public string PackName
    {
        get => _packName;
        set { _packName = value ?? ""; Raise(nameof(PackName)); }
    }

    public string ReceiptUnit
    {
        get => _receiptUnit;
        set
        {
            var next = value ?? "";
            if (_receiptUnit == next) return;
            _receiptUnit = next;
            // تبديل وحدة الاستلام يعيد اشتقاق الوزن من العدد × وزن الوحدة.
            if (_packageCount > 0 && _unitWeightKg > 0) RecalcQty();
            Raise(nameof(ReceiptUnit));
            Raise(nameof(IsInvalid));
            Raise(nameof(ValidationError));
        }
    }

    public int PackageCount
    {
        get => _packageCount;
        set
        {
            _packageCount = value;
            Raise(nameof(PackageCount));
            RecalcQty();
        }
    }

    public double UnitWeightKg
    {
        get => _unitWeightKg;
        set
        {
            _unitWeightKg = value;
            Raise(nameof(UnitWeightKg));
            RecalcQty();
        }
    }

    public double QtyKg
    {
        get => _qtyKg;
        set
        {
            // §v1.50.80 — الكمية (الوزن) مشتقة من عدد الوحدات × وزن الوحدة؛
            // إدخال الكمية يدوياً لا يغيّر عدد الوحدات ولا وزن الوحدة.
            _qtyKg = value;
            Raise(nameof(QtyKg));
            Raise(nameof(IsInvalid));
            Raise(nameof(ValidationError));
        }
    }

    private void RecalcQty()
    {
        if (_packageCount > 0 && _unitWeightKg > 0)
            QtyKg = _packageCount * _unitWeightKg;
        // لا نعيد حساباً إذا أدخل المستخدم الكمية يدوياً وترك العدد صفراً — نحترم إدخاله
    }

    public string Status { get => _status; set { _status = value; Raise(nameof(Status)); } }

    public bool HasLegacyTreatment { get; set; }

    public bool? TreatmentRequired
    {
        get => _treatmentRequired;
        set
        {
            _treatmentRequired = value;
            if (value != true) _until = null;
            _state = null;
            Raise(nameof(TreatmentRequired)); Raise(nameof(TreatmentChoiceAr)); Raise(nameof(ShowUntilDate));
            Raise(nameof(TreatmentUntilDate)); Raise(nameof(TreatmentStateAr));
        }
    }

    public string? TreatmentChoiceAr
    {
        get => TreatmentRequired == null ? null : TreatmentRequired.Value ? "نعم" : "لا";
        set => TreatmentRequired = value switch { "نعم" => true, "لا" => false, _ => null };
    }

    public DateTime? TreatmentUntilDate
    {
        get => _until;
        set { _until = TreatmentRequired == false ? null : value?.Date; Raise(nameof(TreatmentUntilDate)); Raise(nameof(TreatmentStateAr)); }
    }

    public bool ShowUntilDate => TreatmentRequired == true || HasLegacyTreatment;
    public bool IsEditable { get => _isEditable; set { _isEditable = value; Raise(nameof(IsEditable)); } }

    public string TreatmentStateAr
    {
        get => _state ?? (TreatmentRequired == null ? "اختر نعم/لا" : TreatmentRequired == false ? "لا يحتاج معالجة"
            : TreatmentUntilDate == null ? "اختر حتى تاريخ" : "معالجة حتى " + TreatmentUntilDate.Value.ToString("dd/MM/yyyy"));
        set { _state = value; Raise(nameof(TreatmentStateAr)); }
    }

    // §1.50.54 — خصائص مساعدة للواجهة الجديدة
    public bool IsEmptyRow => ProductId == 0;
    public string DisplayCode => string.IsNullOrWhiteSpace(ProductCode) ? "— اضغط لاختيار الصنف —" : ProductCode;
    public string DisplayName => string.IsNullOrWhiteSpace(ProductName) ? "—" : ProductName;
    public bool IsInvalid => !IsEmptyRow && (
        (!string.Equals(_receiptUnit?.Trim(), "كجم", StringComparison.OrdinalIgnoreCase) && (PackageCount <= 0 || UnitWeightKg <= 0))
        || QtyKg <= 0 || TreatmentRequired == null);
    public string ValidationError => IsInvalid
        ? (!string.Equals(_receiptUnit?.Trim(), "كجم", StringComparison.OrdinalIgnoreCase) && PackageCount <= 0 ? "أدخل عدد الوحدات"
            : !string.Equals(_receiptUnit?.Trim(), "كجم", StringComparison.OrdinalIgnoreCase) && UnitWeightKg <= 0 ? "أدخل وزن العبوة"
            : QtyKg <= 0 ? "أدخل الكمية" : "اختر المعالجة نعم/لا")
        : null;

    /// <summary>تعبئة الصف من كيان المنتج المختار — وحدة الاستلام تتبع العبوة/الوحدة المختارة (كجم/كرتون/سلة).</summary>
    public void FillFromProduct(DatesErp.Core.Domain.Entities.Product p, string defaultPackName = null, int? defaultPackId = null, double? defaultUnitWeight = null)
    {
        ProductId = p.Id;
        ProductCode = p.ProductCode ?? "";
        ProductName = p.ProductNameAr ?? "";
        if (defaultPackId.HasValue)
        {
            PackId = defaultPackId;
            PackName = defaultPackName ?? "";
        }
        if (defaultUnitWeight.HasValue && defaultUnitWeight.Value > 0)
            UnitWeightKg = defaultUnitWeight.Value;
        // §v1.50.80 — وحدة الاستلام تُقرأ من بطاقة الصنف (وحدته المعلنة) عند الاختيار،
        // ثم يحدّدها المستخدم فعلياً من العبوة المختارة في شاشة الاستلام (سلة/كرتون/كجم).
        ReceiptUnit = DatesErp.Application.Services.UnitsPolicy.NormalizeReceiptUnit(
            p.UnitOfMeasure ?? "كجم", null, 0, 0);
        // لا نلمس الكمية — المستخدم يدخلها بعد الاختيار
    }

    public void ValidateTreatment(DateTime receiptDate)
        => ReceivingLineTreatmentPolicy.Validate(TreatmentRequired, TreatmentUntilDate, receiptDate);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
