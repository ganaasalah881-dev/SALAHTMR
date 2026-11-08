#nullable enable annotations
using System.ComponentModel;
using System.Globalization;
using DatesErp.Core.Interfaces.Services;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>Shared actual WPF row model. Text stays text until validated: blank/NaN/fractional cartons never become zero silently.</summary>
public sealed class ActualProductionRow : INotifyPropertyChanged, IDataErrorInfo
{
    private readonly bool _historical;
    private readonly bool _continuation;
    public ActualDeliveryItemDto Source { get; }
    public ActualProductionRow(ActualDeliveryItemDto source, bool recorded, bool continuation = false)
    {
        Source = source;
        _historical = recorded && !continuation;
        _continuation = continuation;
        // في جلسة الاستكمال تبقى الكمية السابقة ظاهرة منفصلة، ويبدأ إدخال الجلسة الجديدة فارغاً.
        _actual = _historical ? source.ActualCartons.ToString(CultureInfo.InvariantCulture) : "";
    }
    public string Customer => Source.Customer;
    public string Product => Source.Product;
    public string Unit => Source.Unit;
    public int Planned => Source.PlannedCartons;
    public int PreviouslyProduced => _continuation ? Source.ActualCartons : 0;
    public int RemainingBefore => Math.Max(0, Planned - PreviouslyProduced);
    public double RawMaterialBasisKg => Source.RawMaterialBasisKg > 0 ? Source.RawMaterialBasisKg : Source.PlannedQtyKg;
    private int? _warehouseId;
    public int? WarehouseId { get => _warehouseId; set { if (_warehouseId == value) return; _warehouseId = value; Changed(nameof(WarehouseId)); } }
    private string _warehouseName = "—";
    public string WarehouseName { get => _warehouseName; set { if (_warehouseName == value) return; _warehouseName = value; Changed(nameof(WarehouseName)); } }
    private string _actual;
    public string Actual { get => _actual; set { _actual = value; Changed(nameof(Actual)); Changed(nameof(Difference)); Changed(nameof(DiffState)); Changed(nameof(Error)); } }
    /// <summary>حالة الفرق للتلوين الصريح — اكتمال المخطط، نقص، أو إدخال غير صالح.</summary>
    public string DiffState { get { if (!TryQuantity(out var q)) return "invalid"; return Planned - PreviouslyProduced - q == 0 ? "match" : "gap"; } }
    public bool TryQuantity(out int quantity)
    {
        if (_historical)
        {
            quantity = Source.ActualCartons;
            return quantity >= 0 && quantity <= Planned;
        }
        return int.TryParse(Normalize(Actual), NumberStyles.Integer, CultureInfo.CurrentCulture, out quantity)
            && quantity >= 0 && quantity <= RemainingBefore;
    }
    public string Difference => TryQuantity(out var q)
        ? (Planned - PreviouslyProduced - q).ToString(CultureInfo.CurrentCulture) : "—";
    public string Error => TryQuantity(out _) ? ""
        : $"أدخل كراتين جلسة صحيحة بين صفر والمتبقي ({RemainingBefore:N0})؛ الخانة لا تُملأ تلقائيًا.";
    public string this[string name] => name == nameof(Actual) ? Error : "";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private static string Normalize(string text) => new string((text ?? "").Trim().Select(c =>
        c >= '٠' && c <= '٩' ? (char)('0' + c - '٠') : c >= '۰' && c <= '۹' ? (char)('0' + c - '۰')
            : c == '٫' ? CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator[0] : c).ToArray());
    public static bool TryNonnegative(string text, out double value) => double.TryParse(Normalize(text), NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value) && value >= 0;
}
public sealed class DeliveryEditRow : INotifyPropertyChanged
{
    public int ItemId { get; init; }
    public int ProductId { get; init; }
    public int? ByProductId { get; init; }
    public int? LotId { get; init; }
    public int? CustomerId { get; init; }
    public int? PackagingTypeId { get; init; }
    public string Product { get; init; } = "";
    public string Customer { get; init; } = "";
    public string Specification { get; init; } = "";
    public string Lot { get; init; } = "—";
    public string Unit { get; init; } = "كجم";
    public string OutputType { get; init; } = "منتج تام";
    public string MappingStatus { get; init; } = "";
    public bool CanDeliver { get; init; } = true;
    public bool IsLoss { get; init; }
    public double PlannedKg { get; init; }
    public int PlannedCartons { get; init; }
    public double ActualKg { get; init; }
    public int ActualCartons { get; init; }
    public double PreviouslyDeliveredKg { get; init; }
    public double ExistingDocumentReceivedKg { get; init; }
    public double RemainingKg { get; init; }
    public string Notes { get; init; } = "";

    private double _qtyKg;
    public double QtyKg
    {
        get => _qtyKg;
        set { _qtyKg = value; Changed(nameof(QtyKg)); Changed(nameof(QtyAndRemaining)); }
    }
    public string QtyAndRemaining => $"{QtyKg:N1} / {RemainingKg:N1}";

    private int _packageCount;
    public int PackageCount
    {
        get => _packageCount;
        set { _packageCount = value; Changed(nameof(PackageCount)); }
    }

    private int? _warehouseId;
    public int? WarehouseId
    {
        get => _warehouseId;
        set { _warehouseId = value; Changed(nameof(WarehouseId)); Changed(nameof(WarehouseName)); }
    }
    public string WarehouseName { get; init; } = "—";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ActualSecondaryRow : INotifyPropertyChanged
{
    private ActualByProductDefinitionDto _definition = null!;
    private ActualByProductSourceDto _source = null!;
    private string _quantity = "";
    public ActualByProductDefinitionDto Definition
    {
        get => _definition;
        set { if (ReferenceEquals(_definition, value)) return; _definition = value; Changed(nameof(Definition)); }
    }
    public ActualByProductSourceDto Source
    {
        get => _source;
        set { if (ReferenceEquals(_source, value)) return; _source = value; Changed(nameof(Source)); }
    }
    public string Unit => "كجم";
    public string Quantity
    {
        get => _quantity;
        set { if (_quantity == value) return; _quantity = value ?? ""; Changed(nameof(Quantity)); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
