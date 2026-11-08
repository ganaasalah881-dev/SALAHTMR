using DatesErp.Core.Domain.Entities;

namespace DatesErp.Core.Interfaces.Services;

/// <summary>Only actual values are writable; all identities/planned amounts come from the issued today order.</summary>
public sealed class ActualProductionDto
{
    public int OrderId { get; set; }
    public List<ActualProductionItemDto> Items { get; set; } = new();
    public double ConsumedRawKg { get; set; }
    public double DowntimeHours { get; set; }
    public string DowntimeReason { get; set; }
    public List<ByProductQtyDto> ByProducts { get; set; } = new();
    /// <summary>المسار بعد تسجيل الفعلي: Direct للمخزن مباشرة أو Quality للجودة أولاً.</summary>
    public string WarehouseRoute { get; set; } = ProductionWarehouseRoutes.Quality;
    public string Notes { get; set; }
}
public sealed class ActualProductionItemDto
{
    public int OrderItemId { get; set; }
    public int ActualCartons { get; set; }
}
public sealed class ActualDeliveryOrderDto
{
    public int OrderId { get; init; }
    public string Label { get; init; }
    public string Customer { get; init; }
    public string Shift { get; init; }
    public string ProductionDate { get; init; }
    public string OrderNumber { get; init; }
    public string LineName { get; init; }
    public string PlanNumber { get; init; }
    public double PlannedQtyKg { get; init; }
    public double ActualQtyKg { get; init; }
    public double StockReceivedQtyKg { get; init; }
    public double RemainingQtyKg { get; init; }
    public double WastageQtyKg { get; init; }
    public int ExecutionId { get; init; }
    public int ProductionDeliveryId { get; init; }
    public bool Recorded { get; init; }
    public bool CanRecord { get; init; }
    public bool CanCreateDelivery { get; init; }
    public string Status { get; init; }
    public string ReceiptNumber { get; init; }
    public string QualityNumber { get; init; }
    public string ProductionDeliveryNumber { get; init; }
    public string ProductionDeliveryStatus { get; init; }
    public double ConsumedRawKg { get; init; }
    public double DowntimeHours { get; init; }
    public string DowntimeReason { get; init; }
    public string Notes { get; init; }
    public List<ActualDeliveryItemDto> Items { get; init; } = new();
    public List<ActualByProductDefinitionDto> RecordedByProductDefinitions { get; init; } = new();
    public List<ActualByProductSourceDto> ByProductSources { get; init; } = new();
    public List<ByProductQtyDto> ByProducts { get; init; } = new();
}
public sealed class ActualDeliveryItemDto
{
    public int OrderItemId { get; init; }
    public int ProductId { get; init; }
    public int? LotId { get; init; }
    public int? CustomerId { get; init; }
    public int? PackagingTypeId { get; init; }
    public string Product { get; init; }
    public string Customer { get; init; }
    public string Unit { get; init; } = "كرتون";
    public int PlannedCartons { get; init; }
    public int ActualCartons { get; init; }
    public double PlannedQtyKg { get; init; }
    public double ActualQtyKg { get; init; }
    /// <summary>أساس تقدير الخام آلياً: مجموع تخصيص الخام للأمر، أو الوزن المخطط عند غياب تفاصيل التخصيص.</summary>
    public double RawMaterialBasisKg { get; init; }
    public double PreviouslyReceivedQtyKg { get; init; }
    public int DifferenceCartons => PlannedCartons - ActualCartons;
}
public sealed class ActualByProductDefinitionDto
{
    public int Id { get; init; }
    public string Name { get; init; }
    public string Unit { get; init; }
}
public sealed class ActualByProductSourceDto
{
    public int OrderItemId { get; init; }
    public int RawProductId { get; init; }
    public string RawProductName { get; init; }
    public int RawLotId { get; init; }
    public string RawLotCode { get; init; }
    public int? CustomerId { get; init; }
    public string CustomerName { get; init; }
    public string Label => $"بند #{OrderItemId} — {RawProductName} — {CustomerName}{(string.IsNullOrWhiteSpace(RawLotCode) ? "" : $" — دفعة {RawLotCode}")}";
}
