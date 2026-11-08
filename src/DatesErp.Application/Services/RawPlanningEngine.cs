using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Exceptions;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// محرك قاعدة تحويل المنتج التام إلى الخام.
/// لا يقبل هذا المحرك رقماً خاماً من واجهة المستخدم؛ الكمية الوحيدة التي يدخلها
/// المستخدم هي كمية المنتج التام، ثم تُحسب كمية الخام من تعريف رسمي.
/// </summary>
public sealed class RawRequirementResult
{
    public bool IsConfigured { get; init; }
    public string ErrorCode { get; init; }
    public string Error { get; init; }
    public int FinishedProductId { get; init; }
    public int? RawProductId { get; init; }
    public decimal FinishedQtyKg { get; init; }
    public decimal RawKgPerFinishedKg { get; init; }
    public decimal RawRequiredQtyKg { get; init; }
    public IReadOnlyList<int> AvailableRawProductIds { get; init; } = Array.Empty<int>();

    public static RawRequirementResult Failure(int finishedProductId, decimal finishedQtyKg, string code, string error,
        IReadOnlyList<int> rawIds = null)
        => new()
        {
            FinishedProductId = finishedProductId,
            FinishedQtyKg = finishedQtyKg,
            ErrorCode = code,
            Error = error,
            AvailableRawProductIds = rawIds ?? Array.Empty<int>()
        };
}

/// <summary>مصدر خام رسمي مع معامل محسوب بصيغة كجم خام لكل كجم تام.</summary>
public sealed class RawSourceRule
{
    public int RawProductId { get; init; }
    public int? PackagingTypeId { get; init; }
    public decimal RawKgPerFinishedKg { get; init; }
}

public static class RawRequirementEngine
{
    /// <summary>
    /// يعيد المصادر الخام المرتبطة بالصنف التام (ProductRawSource أو SourceProductId) للتحقق من هوية الخام فقط؛
    /// الاحتساب الفعلي للخام أصبح مباشراً في سطر الخطة (1:1 افتراضياً أو بنسبة فرز/تخصيص مباشر) دون الاعتماد على معامل بطاقة الصنف.
    /// </summary>
    public static List<RawSourceRule> GetRules(DatesErpDbContext db, int finishedProductId, int? packagingTypeId = null)
    {
        var explicitRules = db.ProductRawSources.AsNoTracking()
            .Where(x => x.FinishedProductId == finishedProductId && x.IsActive)
            .ToList();
        if (explicitRules.Count > 0)
        {
            var exact = packagingTypeId is int pack
                ? explicitRules.Where(x => x.PackagingTypeId == pack).ToList()
                : new List<ProductRawSource>();
            var generic = explicitRules.Where(x => x.PackagingTypeId == null).ToList();
            var selected = packagingTypeId == null
                ? explicitRules
                : exact.Count > 0 ? exact : (generic.Count > 0 ? generic : explicitRules);
            return selected
                .GroupBy(x => x.RawProductId)
                .Select(g =>
                {
                    var row = g.OrderBy(x => x.PriorityNo).ThenBy(x => x.Id).First();
                    return new RawSourceRule
                    {
                        RawProductId = row.RawProductId,
                        PackagingTypeId = row.PackagingTypeId,
                        RawKgPerFinishedKg = 1m
                    };
                })
                .ToList();
        }

        var rawId = db.Products.AsNoTracking()
            .FirstOrDefault(x => x.Id == finishedProductId)?.SourceProductId;
        if (rawId is int sourceRawId && sourceRawId > 0)
        {
            return new List<RawSourceRule>
            {
                new RawSourceRule
                {
                    RawProductId = sourceRawId,
                    RawKgPerFinishedKg = 1m
                }
            };
        }

        return new List<RawSourceRule>();
    }

    public static RawRequirementResult Calculate(DatesErpDbContext db, int finishedProductId,
        double finishedQtyKg, int? selectedRawProductId = null, int? packagingTypeId = null,
        double? explicitRawQtyKg = null, double? wastePercent = null)
    {
        if (!double.IsFinite(finishedQtyKg) || finishedQtyKg <= 0)
            return RawRequirementResult.Failure(finishedProductId, 0, "FINISHED_QTY_REQUIRED", "كمية المنتج التام يجب أن تكون أكبر من صفر.");

        var product = db.Products.AsNoTracking().FirstOrDefault(x => x.Id == finishedProductId);
        if (product == null || !product.IsActive || product.ItemType != "Finished")
            return RawRequirementResult.Failure(finishedProductId, (decimal)finishedQtyKg, "FINISHED_PRODUCT_REQUIRED", "الصنف التام غير موجود أو غير نشط.");

        var rules = GetRules(db, finishedProductId, packagingTypeId);
        var rawIds = rules.Select(x => x.RawProductId).Distinct().ToList();
        int resolvedRawId;
        if (rules.Count == 0)
        {
            if (selectedRawProductId is int directRaw && directRaw > 0)
            {
                resolvedRawId = directRaw;
                rawIds = new List<int> { directRaw };
            }
            else
            {
                return RawRequirementResult.Failure(
                    finishedProductId, (decimal)finishedQtyKg, "RAW_SOURCE_REQUIRED",
                    $"اربط الصنف التام «{product.ProductNameAr}» بالصنف الخام المصدر في شاشة الأصناف أو اختر دفعة خام مباشرة.");
            }
        }
        else if (selectedRawProductId is int selected)
        {
            var rule = rules.FirstOrDefault(x => x.RawProductId == selected);
            if (rule != null)
            {
                resolvedRawId = rule.RawProductId;
            }
            else
            {
                // §التوجيه الصارم: عدم التعديل على بطاقة الصنف عند التخطيط — يُقبل أي خام يختاره المستخدم
                resolvedRawId = selected;
            }
        }
        else if (rules.Count == 1)
        {
            resolvedRawId = rules[0].RawProductId;
        }
        else
        {
            return RawRequirementResult.Failure(
                finishedProductId, (decimal)finishedQtyKg, "RAW_SOURCE_REQUIRED",
                $"الصنف التام «{product.ProductNameAr}» له أكثر من مصدر خام مسموح. حدد الدفعة أو المصدر الخام للسطر.", rawIds);
        }

        var raw = db.Products.AsNoTracking().FirstOrDefault(x => x.Id == resolvedRawId);
        if (raw == null || !raw.IsActive || raw.ItemType != "Raw")
            return RawRequirementResult.Failure(finishedProductId, (decimal)finishedQtyKg, "RAW_SOURCE_INVALID", "الصنف الخام المحدد غير موجود أو غير نشط.", rawIds);

        var finished = (decimal)finishedQtyKg;
        decimal required;
        if (explicitRawQtyKg is double expKg && double.IsFinite(expKg) && expKg > 0.001)
            required = decimal.Round((decimal)expKg, 4, MidpointRounding.AwayFromZero);
        else if (wastePercent is double wp && double.IsFinite(wp) && wp > 0)
            required = decimal.Round(finished * (1m + (decimal)wp / 100m), 4, MidpointRounding.AwayFromZero);
        else
            required = decimal.Round(finished, 4, MidpointRounding.AwayFromZero);

        var effectiveFactor = finished > 0m
            ? decimal.Round(required / finished, 6, MidpointRounding.AwayFromZero)
            : 1m;

        return new RawRequirementResult
        {
            IsConfigured = true,
            FinishedProductId = finishedProductId,
            RawProductId = resolvedRawId,
            FinishedQtyKg = finished,
            RawKgPerFinishedKg = effectiveFactor,
            RawRequiredQtyKg = required,
            AvailableRawProductIds = rawIds
        };
    }

    public static void EnsureConfigured(DatesErpDbContext db, int finishedProductId, double finishedQtyKg,
        int? selectedRawProductId = null, int? packagingTypeId = null,
        double? explicitRawQtyKg = null, double? wastePercent = null)
    {
        var result = Calculate(db, finishedProductId, finishedQtyKg, selectedRawProductId, packagingTypeId, explicitRawQtyKg, wastePercent);
        if (!result.IsConfigured) throw new DomainException(result.Error, result.ErrorCode);
    }
}

/// <summary>حالة خام واحدة على مفتاح المخزن/الدفعة كما يراها التخطيط.</summary>
public sealed class RawAvailabilitySnapshot
{
    public int WarehouseId { get; init; }
    public int LotId { get; init; }
    public double ActualStockKg { get; init; }
    public double PlannedReservationKg { get; init; }
    public double OtherCommitmentsKg { get; init; }
    public double NotReadyKg { get; init; }
    public double AvailableForPlanningKg { get; init; }
    public double NewPlanNeedKg { get; init; }
    public double RemainingAfterPlanningKg => Math.Max(0, AvailableForPlanningKg - NewPlanNeedKg);
    public bool IsSufficient => NewPlanNeedKg <= AvailableForPlanningKg + 0.001;
}

/// <summary>
/// دفتر الالتزامات الخام المركزي. كل خدمة تخطيط/أمر تستخدم نفس الحساب:
/// الحركات/StockBalance = فعلي، تخصيصات الخطط = حجز تخطيطي، والأمر المستقل = التزام آخر.
/// أمر ناتج من خطة لا يُجمع مرة ثانية لأنه مغطى بحجز تخصيص خطته.
/// </summary>
public sealed class RawPlanningAvailabilityEngine
{
    private readonly DatesErpDbContext _db;

    /// <summary>توافق للخطط القديمة بلا تخصيص: يوزع الخام المتبقي بنسبة إنتاج التام.</summary>
    public static double LegacyRawRemainingKg(ProductionPlanItem item)
    {
        if (item == null || item.RawRequiredQtyKg <= 0) return 0;
        if (item.PlannedQtyKg <= 0) return (double)item.RawRequiredQtyKg;
        var ratio = Math.Max(0, Math.Min(1, item.ProducedQtyKg / item.PlannedQtyKg));
        return Math.Max(0, (double)item.RawRequiredQtyKg * (1 - ratio));
    }

    public static double LegacyRawRemainingKg(DatesErpDbContext db, ProductionPlanItem item)
    {
        // A missing persisted requirement is not a reservation. Recomputing it from a
        // coefficient entered later would silently reserve a previously pending draft
        // before the planner explicitly re-saves it.
        if (item == null || item.RawRequiredQtyKg <= 0) return 0;
        return LegacyRawRemainingKg(item);
    }
    public RawPlanningAvailabilityEngine(DatesErpDbContext db) => _db = db;

    private double StandaloneRawRemaining(ProductionOrderItem item)
    {
        if (item == null || item.PlannedQtyKg <= 0) return 0;
        int? rawId = item.LotId is int lotId
            ? _db.Lots.AsNoTracking().Where(x => x.Id == lotId).Select(x => (int?)x.ProductId).FirstOrDefault()
            : null;
        var result = RawRequirementEngine.Calculate(_db, item.ProductId, item.PlannedQtyKg, rawId, item.PackagingTypeId);
        if (!result.IsConfigured) return 0;
        var ratio = Math.Max(0, Math.Min(1, item.ProducedQtyKg / item.PlannedQtyKg));
        return Math.Max(0, (double)result.RawRequiredQtyKg * (1 - ratio));
    }

    private double MaturingTreatmentByDate(Lot lot, DateTime? asOfDate)
    {
        if (!asOfDate.HasValue || lot.UnderTreatmentQtyKg <= 0.001) return 0;
        var cutoff = asOfDate.Value.Date.AddDays(1).AddTicks(-1);
        var active = _db.RawTreatments.AsNoTracking()
            .Where(x => x.LotId == lot.Id && x.Status == TreatmentStatuses.InProgress)
            .Select(x => new { x.ExpectedReadyAt, Remaining = x.QtyKg - x.ReleasedQtyKg - x.RejectedQtyKg })
            .ToList();
        if (active.Count > 0)
        {
            return active
                .Where(x => x.Remaining > 0 && x.ExpectedReadyAt <= cutoff)
                .Sum(x => x.Remaining);
        }
        var receiptUntil = _db.ShipmentItems.AsNoTracking()
            .Where(x => x.Id == lot.ShipmentItemId && x.TreatmentRequired == true)
            .Select(x => (DateTime?)x.TreatmentUntilDate)
            .FirstOrDefault();
        return receiptUntil.HasValue && asOfDate.Value.Date >= receiptUntil.Value.Date
            ? Math.Max(0, lot.UnderTreatmentQtyKg)
            : 0;
    }

    private bool IsRawSourceWarehouse(int warehouseId)
        => _db.Warehouses.AsNoTracking()
            .Any(w => w.Id == warehouseId && w.IsActive
                && w.WarehouseType == "Raw" && w.WarehouseCode != "WTRT");

    public RawAvailabilitySnapshot ForLot(int warehouseId, int lotId, double newPlanNeedKg = 0,
        int? excludePlanId = null, int? excludeOrderId = null, DateTime? asOfDate = null)
    {
        var lot = _db.Lots.AsNoTracking().FirstOrDefault(x => x.Id == lotId);
        if (lot == null || !IsRawSourceWarehouse(warehouseId))
            return new RawAvailabilitySnapshot { WarehouseId = warehouseId, LotId = lotId, NewPlanNeedKg = Math.Max(0, newPlanNeedKg) };

        // قراءة رصيد الخام الحقيقي فقط (ProductId == lot.ProductId أو فارغ تاريخياً) في مخازن الخام حصراً —
        // لا نخلط أبداً أرصدة المنتج التام في مخزن WFG الحاملة لنفس LotId مع التمور الخام!
        var actual = _db.StockBalances.AsNoTracking()
            .Where(x => x.WarehouseId == warehouseId && x.LotId == lotId
                && (x.ProductId == null || x.ProductId == lot.ProductId))
            .Sum(x => (double?)x.QtyKg) ?? 0;
        var defaultRawWh = _db.Warehouses.AsNoTracking()
            .Where(w => w.WarehouseCode == "WRM").Select(w => (int?)w.Id).FirstOrDefault();
        var receiptWarehouse = _db.Shipments.AsNoTracking().Where(x => x.Id == lot.ShipmentId)
            .Select(x => x.ReceivingWarehouseId).FirstOrDefault() ?? defaultRawWh;
        var rawWarehouseIds = _db.Warehouses.AsNoTracking()
            .Where(w => w.IsActive && w.WarehouseType == "Raw" && w.WarehouseCode != "WTRT")
            .Select(w => w.Id).ToHashSet();
        var treatmentWhId = _db.Warehouses.AsNoTracking()
            .Where(w => w.WarehouseCode == "WTRT").Select(w => (int?)w.Id).FirstOrDefault();
        var hasAnyRawOrTrtBalance = _db.StockBalances.AsNoTracking()
            .Any(x => x.LotId == lotId
                && (x.ProductId == null || x.ProductId == lot.ProductId)
                && (rawWarehouseIds.Contains(x.WarehouseId) || (treatmentWhId != null && x.WarehouseId == treatmentWhId.Value)));

        // بيانات ما قبل StockBalance: لا نخترع مخزناً آخر، ونستخدم دفتر الدفعة فقط عندما لا توجد أي أرصدة موزعة.
        if (!hasAnyRawOrTrtBalance && actual <= 0 && receiptWarehouse == warehouseId)
            actual = lot.InStockQtyKg;

        var planRows = _db.ProductionPlanItemAllocations.AsNoTracking()
            .Where(a => a.WarehouseId == warehouseId && a.LotId == lotId && a.Status != "Released" && a.Status != "Consumed")
            .Join(_db.ProductionPlanItems.AsNoTracking(), a => a.PlanItemId, i => i.Id, (a, i) => new { a, i })
            .Join(_db.ProductionPlans.AsNoTracking(), x => x.i.PlanId, p => p.Id, (x, p) => new { x.a, x.i, p })
            .Where(x => x.p.PlanType != "Template" && x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && !x.p.IsClosed && !x.i.IsClosed)
            .Where(x => excludePlanId == null || x.i.PlanId != excludePlanId)
            .AsEnumerable()
            .Sum(x => x.a.AllocatedQtyKg - x.a.ConsumedQtyKg - x.a.ReleasedQtyKg > 0
                ? x.a.AllocatedQtyKg - x.a.ConsumedQtyKg - x.a.ReleasedQtyKg : 0);

        var allocationItemIds = _db.ProductionPlanItemAllocations.AsNoTracking()
            .Select(x => x.PlanItemId).ToHashSet();
        // الخطط التاريخية بلا تخصيص تُخصم من مخزن الاستلام الأصلي للدفعة فقط لمنع خصمها مرتين عبر المخازن
        var legacyPlanRows = (receiptWarehouse == null || receiptWarehouse == warehouseId)
            ? _db.ProductionPlanItems.AsNoTracking()
                .Where(i => i.LotId == lotId && !allocationItemIds.Contains(i.Id) && !i.IsClosed)
                .Join(_db.ProductionPlans.AsNoTracking(), i => i.PlanId, p => p.Id, (i, p) => new { i, p })
                .Where(x => x.p.PlanType != "Template" && x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && !x.p.IsClosed
                    && (excludePlanId == null || x.i.PlanId != excludePlanId))
                .ToList()
                .Sum(x => LegacyRawRemainingKg(_db, x.i))
            : 0;
        var planned = planRows + legacyPlanRows;

        // فقط الأوامر المستقلة التزام آخر، والأوامر غير المخصصة لمخزن تُخصم من مخزن الاستلام فقط (لا تتضاعف عبر المخازن).
        var standaloneOrderItems = _db.ProductionOrderItems.AsNoTracking()
            .Where(i => i.LotId == lotId && i.PlanItemId == null && !i.IsClosed)
            .Join(_db.ProductionOrders.AsNoTracking(), i => i.OrderId, o => o.Id, (i, o) => new { i, o })
            .Where(x => x.o.Status != DocStatuses.Cancelled && x.o.Status != DocStatuses.Closed
                && (excludeOrderId == null || x.i.OrderId != excludeOrderId))
            .ToList();
        var standaloneIds = standaloneOrderItems.Select(x => x.i.Id).ToHashSet();
        var allOrderAllocationRows = _db.Set<ProductionOrderItemAllocation>().AsNoTracking()
            .Where(a => standaloneIds.Contains(a.OrderItemId) && a.LotId == lotId)
            .ToList();
        var orderAllocationRows = allOrderAllocationRows
            .Where(a => a.WarehouseId == warehouseId).ToList();
        var allocatedOrderItemIds = allOrderAllocationRows.Select(a => a.OrderItemId).ToHashSet();
        double unallocatedStandalone = (receiptWarehouse == null || receiptWarehouse == warehouseId)
            ? standaloneOrderItems.Where(x => !allocatedOrderItemIds.Contains(x.i.Id)).Sum(x => StandaloneRawRemaining(x.i))
            : 0;
        double other = orderAllocationRows.Sum(a =>
                a.AllocatedQtyKg > a.ConsumedQtyKg ? a.AllocatedQtyKg - a.ConsumedQtyKg : 0)
            + unallocatedStandalone;

        // المعالجة (WRM <-> WTRT):
        // - عند وجود StockBalances: بدء المعالجة نقل الكمية فعلاً من WRM إلى WTRT، فلا نطرح UnderTreatmentQtyKg مرة ثانية من WRM،
        //   بل نضيف في التواريخ المستقبلية الكمية التي ستنهي معالجتها بحلول asOfDate عائدةً إلى مخزن الخام المصدر.
        // - عند غياب StockBalances (بيانات قديمة): actual أُخذ من lot.InStockQtyKg الشامل للمعالجة، فنطرح الجزء غير الجاهز فقط.
        bool isPrimaryRawStore = receiptWarehouse == null || receiptWarehouse == warehouseId;
        double maturingKg = isPrimaryRawStore ? MaturingTreatmentByDate(lot, asOfDate) : 0;
        double effectiveStock = actual;
        double notReady = 0;
        if (hasAnyRawOrTrtBalance)
        {
            effectiveStock = actual + Math.Min(Math.Max(0, lot.UnderTreatmentQtyKg), maturingKg);
            if (isPrimaryRawStore)
                effectiveStock = Math.Min(effectiveStock, lot.InStockQtyKg);
        }
        else if (isPrimaryRawStore && lot.UnderTreatmentQtyKg > 0)
        {
            notReady = Math.Max(0, Math.Min(actual, lot.UnderTreatmentQtyKg - maturingKg));
        }

        var available = Math.Max(0, effectiveStock - planned - other - notReady);
        return new RawAvailabilitySnapshot
        {
            WarehouseId = warehouseId,
            LotId = lotId,
            ActualStockKg = Math.Max(0, effectiveStock),
            PlannedReservationKg = Math.Max(0, planned),
            OtherCommitmentsKg = Math.Max(0, other),
            NotReadyKg = notReady,
            AvailableForPlanningKg = available,
            NewPlanNeedKg = Math.Max(0, newPlanNeedKg)
        };
    }

    public List<RawAvailabilitySnapshot> ForLotAllWarehouses(int lotId, double newPlanNeedKg = 0,
        int? excludePlanId = null, int? excludeOrderId = null, DateTime? asOfDate = null)
    {
        var lot = _db.Lots.AsNoTracking().FirstOrDefault(x => x.Id == lotId);
        if (lot == null) return new();
        var rawWarehouses = _db.Warehouses.AsNoTracking()
            .Where(w => w.IsActive && w.WarehouseType == "Raw" && w.WarehouseCode != "WTRT")
            .Select(w => w.Id).ToHashSet();
        var warehouseIds = _db.StockBalances.AsNoTracking()
            .Where(x => x.LotId == lotId && (x.ProductId == null || x.ProductId == lot.ProductId) && rawWarehouses.Contains(x.WarehouseId))
            .Select(x => x.WarehouseId).Distinct().ToList();
        var receiptWarehouse = _db.Lots.Where(x => x.Id == lotId)
            .Join(_db.Shipments, l => l.ShipmentId, s => s.Id, (l, s) => s.ReceivingWarehouseId).FirstOrDefault()
            ?? _db.Warehouses.AsNoTracking().Where(w => w.WarehouseCode == "WRM").Select(w => (int?)w.Id).FirstOrDefault();
        if (receiptWarehouse is int receipt && rawWarehouses.Contains(receipt) && !warehouseIds.Contains(receipt))
            warehouseIds.Add(receipt);
        return warehouseIds.Select(w => ForLot(w, lotId, newPlanNeedKg, excludePlanId, excludeOrderId, asOfDate)).ToList();
    }
}
