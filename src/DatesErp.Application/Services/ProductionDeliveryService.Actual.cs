using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

public partial class ProductionDeliveryService
{
    protected override bool RetryTransactionDeadlocks => true;
    protected override System.Data.IsolationLevel TransactionIsolation => System.Data.IsolationLevel.Serializable;

    /// <summary>المخرجات الثانوية الرسمية تُقرأ من جدول تعريفات ByProducts، وهو المرجع الذي تستخدمه ExecutionByProduct.ByProductId.</summary>
    private static bool IsKilogramUnit(string unit)
        => (unit ?? "").Trim().ToLowerInvariant() is "كجم" or "كغ" or "kg" or "كيلوجرام";

    public List<ActualByProductDefinitionDto> GetActualByProducts()
    {
        if (Session == null || !Session.Can("production", "View")) throw new PermissionDeniedException("عرض الإنتاج");
        var definitions = Db.ByProducts.AsNoTracking().Where(b => b.IsActive).OrderBy(b => b.Id).ToList();
        if (definitions.Count > 0)
        {
            return definitions.Select(b => new ActualByProductDefinitionDto
            {
                Id = b.Id,
                Name = b.ByProductNameAr,
                Unit = string.IsNullOrWhiteSpace(b.UnitOfMeasure) ? "كجم" : b.UnitOfMeasure
            }).ToList();
        }

        return Db.Products.AsNoTracking()
            .Where(p => p.IsActive && (p.ItemType == "ByProduct" || p.GroupCode == UnitsPolicy.GroupByProduct))
            .OrderBy(p => p.Id)
            .Select(p => new ActualByProductDefinitionDto { Id = p.Id, Name = p.ProductNameAr, Unit = "كجم" })
            .ToList();
    }

    public List<WarehouseOptionDto> GetProductionDeliveryWarehouses()
    {
        if (Session == null || !Session.Can("production", "View")) throw new PermissionDeniedException("عرض مخازن تسليم الإنتاج");
        return Db.Warehouses.AsNoTracking().Where(w => w.IsActive).OrderBy(w => w.WarehouseNameAr)
            .Select(w => new WarehouseOptionDto
            {
                Id = w.Id, Code = w.WarehouseCode, Name = w.WarehouseNameAr, Type = w.WarehouseType
            }).ToList();
    }

    private List<TodayProductionRowDto> GetFallbackPlanRows(IReadOnlyCollection<int> orderIds)
    {
        var ids = orderIds.Distinct().ToList();
        if (ids.Count == 0) return new();

        var orders = Db.ProductionOrders.AsNoTracking().Where(o => ids.Contains(o.Id)).ToDictionary(o => o.Id);
        var items = Db.ProductionOrderItems.AsNoTracking().Where(i => ids.Contains(i.OrderId)).OrderBy(i => i.Id).ToList();
        var planItemIds = items.Where(i => i.PlanItemId != null).Select(i => i.PlanItemId.Value).Distinct().ToList();
        var planItems = Db.ProductionPlanItems.AsNoTracking().Where(i => planItemIds.Contains(i.Id)).ToDictionary(i => i.Id);
        var planIds = planItems.Values.Select(i => i.PlanId).Distinct().ToList();
        var plans = Db.ProductionPlans.AsNoTracking().Where(p => planIds.Contains(p.Id)
            && p.IsApproved && p.Status == DocStatuses.Approved && !p.IsClosed).ToDictionary(p => p.Id);
        var productIds = planItems.Values.Select(i => i.ProductId).Distinct().ToList();
        var productNames = Db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id))
            .ToDictionary(p => p.Id, p => p.ProductNameAr);
        var customerIds = planItems.Values.Where(i => i.CustomerId != null).Select(i => i.CustomerId.Value).Distinct().ToList();
        var customerNames = Db.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id))
            .ToDictionary(c => c.Id, c => c.CustomerName);
        var shiftIds = planItems.Values.Select(i => i.SuggestedShiftId).Concat(orders.Values.Select(o => o.ShiftId))
            .Where(i => i != null).Select(i => i.Value).Distinct().ToList();
        var shifts = Db.Shifts.AsNoTracking().Where(s => shiftIds.Contains(s.Id))
            .ToDictionary(s => s.Id, s => s.ShiftNameAr);
        var lineIds = planItems.Values.Select(i => i.SuggestedLineId).Concat(orders.Values.Select(o => o.LineId))
            .Where(i => i != null).Select(i => i.Value).Distinct().ToList();
        var lines = Db.ProductionLines.AsNoTracking().Where(l => lineIds.Contains(l.Id))
            .ToDictionary(l => l.Id, l => l.LineNameAr);
        var closed = Db.ProductionExecutions.AsNoTracking().Where(e => ids.Contains(e.OrderId) && e.IsDayClosed)
            .Select(e => e.OrderId).ToHashSet();

        return items.Where(i => orders.ContainsKey(i.OrderId) && i.PlanItemId is int pid && planItems.ContainsKey(pid)
                && orders[i.OrderId].SourcePlanId == planItems[pid].PlanId
                && plans.ContainsKey(planItems[pid].PlanId))
            .Select(i =>
            {
                var order = orders[i.OrderId];
                var plan = planItems[i.PlanItemId!.Value];
                var scheduled = plan.ScheduledDate?.Date;
                var shift = plan.SuggestedShiftId ?? order.ShiftId;
                var line = plan.SuggestedLineId ?? order.LineId;
                var customer = plan.CustomerId is int cid && customerNames.TryGetValue(cid, out var cn)
                    ? cn : "غير محدد في الخطة";
                return new TodayProductionRowDto
                {
                    PlanId = plan.PlanId, PlanItemId = plan.Id, PlanNumber = plans[plan.PlanId].DocumentNumber,
                    CustomerId = plan.CustomerId, CustomerName = customer,
                    ProductId = plan.ProductId, ProductName = productNames.GetValueOrDefault(plan.ProductId, "صنف غير موجود"),
                    PlannedCartons = plan.PlannedCartons, PlannedKg = plan.PlannedQtyKg,
                    ScheduledDate = scheduled?.ToString("dd/MM/yyyy"), IsToday = scheduled == Db.BusinessNow.Date,
                    ShiftId = shift, ShiftName = shift is int sid ? shifts.GetValueOrDefault(sid, "وردية غير موجودة") : "غير محددة في الخطة",
                    LineId = line, LineName = line is int lid ? lines.GetValueOrDefault(lid, "خط غير موجود") : "غير محدد في الخطة",
                    OrderId = order.Id, OrderNumber = order.DocumentNumber,
                    IsPending = false, DayClosed = closed.Contains(order.Id),
                    Status = closed.Contains(order.Id) ? "مقفل" : DocStatuses.ToArabic(order.Status)
                };
            }).ToList();
    }

    public List<ActualDeliveryOrderDto> GetActualDeliveryOrders(int? selectedOrderId = null)
        => GetActualDeliveryOrders(selectedOrderId, includeCompleted: false);

    public List<ActualDeliveryOrderDto> GetActualDeliveryOrders(int? selectedOrderId, bool includeCompleted)
    {
        var closedExeOrderIds = Db.ProductionExecutions.AsNoTracking()
            .Where(e => e.IsDayClosed)
            .Select(e => e.OrderId)
            .ToHashSet();

        // الأوامر التي لها تنفيذ مقفل ولم يُنشأ لها أمر تسليم بعد — تبقى ظاهرة حتى يُنشأ الأمر
        var closedExeList = Db.ProductionExecutions.AsNoTracking()
            .Where(e => e.IsDayClosed)
            .Select(e => new { e.Id, e.OrderId })
            .ToList();
        // المسودة جزء من دورة التسليم وليست تسليماً مكتملاً: يجب أن تبقى ظاهرة
        // حتى يتمكن مدير الإنتاج من تعديلها ثم تحريرها. بعد التحرير (Issued/Completed)
        // تُحجب من القائمة التشغيلية، بينما الإلغاء يعيد التنفيذ إلى قائمة الإنشاء.
        var deliveredExeIds = Db.ProductionDeliveries.AsNoTracking()
            .Where(d => d.SourceType == DeliverySources.FromActual
                && d.Status != DocStatuses.Cancelled
                && d.Status != DocStatuses.Draft)
            .Select(d => d.SourceId)
            .ToHashSet();
        // احسم الحالة على آخر تنفيذ مقفل لكل أمر، لا على أي تنفيذ تاريخي.
        // وجود تنفيذ قديم بلا تسليم لا يجوز أن يعيد أمراً مُحرَّراً حديثاً
        // إلى القائمة التشغيلية.
        var latestClosedExeByOrder = closedExeList
            .GroupBy(e => e.OrderId)
            .Select(g => g.OrderByDescending(e => e.Id).First())
            .ToList();
        var closedExeWithNoDelivery = latestClosedExeByOrder
            .Where(e => !deliveredExeIds.Contains(e.Id))
            .Select(e => e.OrderId)
            .ToHashSet();
        var orders = Db.ProductionOrders.AsNoTracking()
            .Where(o => ((o.SourceType == "FromPlan" && o.SourcePlanId != null) || o.SourceType == "Standalone")
                && o.IsApproved
                && o.Status != DocStatuses.Cancelled
                && (selectedOrderId.HasValue
                    ? o.Id == selectedOrderId.Value
                    : (includeCompleted
                        || !o.IsClosed && !closedExeOrderIds.Contains(o.Id)
                        || closedExeWithNoDelivery.Contains(o.Id)
                        || closedExeOrderIds.Contains(o.Id))))
            .OrderByDescending(o => o.Id)
            .ToList();

        if (orders.Count == 0) return new();

        var orderIds = orders.Select(o => o.Id).ToList();
        var items = Db.ProductionOrderItems.AsNoTracking().Where(i => orderIds.Contains(i.OrderId)).ToList();
        var orderItemIds = items.Select(i => i.Id).Distinct().ToList();
        var allocations = Db.Set<ProductionOrderItemAllocation>().AsNoTracking()
            .Where(a => orderItemIds.Contains(a.OrderItemId)).ToList();
        var rawLotIds = items.Where(i => i.LotId != null).Select(i => i.LotId!.Value)
            .Concat(allocations.Select(a => a.LotId)).Distinct().ToList();
        var rawLots = Db.Lots.AsNoTracking().Where(l => rawLotIds.Contains(l.Id)).ToDictionary(l => l.Id);
        var productIds = items.Select(i => i.ProductId).Concat(rawLots.Values.Select(l => l.ProductId)).Distinct().ToList();
        var products = Db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionary(p => p.Id, p => p.ProductNameAr);

        var planItemIds = items.Where(i => i.PlanItemId != null).Select(i => i.PlanItemId!.Value).Distinct().ToList();
        var planItems = Db.ProductionPlanItems.AsNoTracking().Where(pi => planItemIds.Contains(pi.Id)).ToDictionary(pi => pi.Id);
        var planCustomerByItem = planItems.ToDictionary(kv => kv.Key, kv => kv.Value.CustomerId);

        var planIds = orders.Where(o => o.SourcePlanId != null).Select(o => o.SourcePlanId!.Value)
            .Concat(planItems.Values.Select(pi => pi.PlanId))
            .Distinct().ToList();
        var plans = Db.ProductionPlans.AsNoTracking().Where(p => planIds.Contains(p.Id)).ToDictionary(p => p.Id, p => p.DocumentNumber);

        var shiftIds = orders.Where(o => o.ShiftId != null).Select(o => o.ShiftId!.Value).Distinct().ToList();
        var shifts = Db.Shifts.AsNoTracking().Where(s => shiftIds.Contains(s.Id)).ToDictionary(s => s.Id, s => s.ShiftNameAr);
        var lineIds = orders.Where(o => o.LineId != null).Select(o => o.LineId!.Value).Distinct().ToList();
        var lineNames = Db.ProductionLines.AsNoTracking().Where(l => lineIds.Contains(l.Id))
            .ToDictionary(l => l.Id, l => l.LineNameAr);

        var customerIds = orders.Where(o => o.CustomerId != null).Select(o => o.CustomerId!.Value)
            .Concat(items.Where(i => i.CustomerId != null).Select(i => i.CustomerId!.Value))
            .Concat(planItems.Values.Where(pi => pi.CustomerId != null).Select(pi => pi.CustomerId!.Value))
            .Distinct().ToList();
        var customers = Db.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionary(c => c.Id, c => c.CustomerName);

        var executionRows = Db.ProductionExecutions.AsNoTracking()
            .Include(e => e.Downtimes).Include(e => e.ByProducts)
            .Where(e => orderIds.Contains(e.OrderId) && e.IsDayClosed)
            .OrderBy(e => e.Id)
            .ToList();
        // كل جلسة مكتملة تبقى جزءاً من سجل الأمر؛ نستخدم الأحدث للمعرّف/الفحص،
        // ونجمع الكميات والمخرجات عبر الجلسات حتى لا تختفي الجلسات الجزئية السابقة.
        var executionsByOrder = executionRows
            .GroupBy(e => e.OrderId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var executions = executionsByOrder
            .ToDictionary(kv => kv.Key, kv => kv.Value.OrderByDescending(e => e.Id).First());

        var exeIds = executionRows.Select(e => e.Id).ToList();
        var executionOrderById = executionRows.ToDictionary(e => e.Id, e => e.OrderId);
        var qualityChecks = Db.QualityChecks.AsNoTracking()
            .Where(q => q.ExecutionId != null && exeIds.Contains(q.ExecutionId.Value))
            .ToList()
            .GroupBy(q => q.ExecutionId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(q => q.Id).First());
        var deliveryRows = Db.ProductionDeliveries.AsNoTracking()
            .Where(d => d.SourceType == DeliverySources.FromActual && exeIds.Contains(d.SourceId)
                && d.Status != DocStatuses.Cancelled)
            .ToList();
        var deliveries = deliveryRows
            .Where(d => executionOrderById.ContainsKey(d.SourceId))
            .GroupBy(d => executionOrderById[d.SourceId])
            .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.Id).First());
        var ordersWithDraftDelivery = deliveryRows
            .Where(d => d.Status == DocStatuses.Draft && executionOrderById.ContainsKey(d.SourceId))
            .Select(d => executionOrderById[d.SourceId])
            .ToHashSet();

        var byProductDefs = Db.ByProducts.AsNoTracking().ToDictionary(b => b.Id, b => new ActualByProductDefinitionDto
        {
            Id = b.Id, Name = b.ByProductNameAr, Unit = b.UnitOfMeasure
        });

        var result = new List<ActualDeliveryOrderDto>();
        foreach (var order in orders)
        {
            var orderItems = items.Where(i => i.OrderId == order.Id).OrderBy(i => i.Id).ToList();
            var orderExecutions = executionsByOrder.GetValueOrDefault(order.Id) ?? new List<ProductionExecution>();
            executions.TryGetValue(order.Id, out var exe);
            var qc = exe == null ? null : qualityChecks.GetValueOrDefault(exe.Id);
            var productionDelivery = deliveries.GetValueOrDefault(order.Id);
            var sourceContext = exe == null ? null : GetSourceContext(DeliverySources.FromActual, exe.Id);
            var sourceLines = sourceContext?.Lines ?? new List<DeliverySourceLine>();
            var hasRemainingDelivery = exe != null && sourceLines.Any(l => l.ProductId > 0 && l.RemainingQtyKg > 0.001);
            bool hasRemainingProduction = orderItems.Any(i => !i.IsClosed
                && (i.ProducedCartons < i.PlannedCartons || i.ProducedQtyKg + 0.001 < i.PlannedQtyKg));
            bool can = order.IsApproved && !order.IsClosed && order.Status != DocStatuses.Cancelled
                && hasRemainingProduction;
            // يُخفى الأمر المكتمل تسليمه فقط إذا لم يبق إنتاج مخطط لاستكماله.
            if (!includeCompleted && !selectedOrderId.HasValue && exe != null && productionDelivery != null
                && productionDelivery.Status != DocStatuses.Draft && !hasRemainingDelivery && !can)
                continue;

            var planNum = order.SourcePlanId != null && plans.TryGetValue(order.SourcePlanId.Value, out var pn) ? pn : "—";
            var shiftName = order.ShiftId != null && shifts.TryGetValue(order.ShiftId.Value, out var sn) ? sn : "وردية غير محددة";

            var itemCustomerNames = orderItems.Select(i =>
            {
                int? cid = i.CustomerId ?? (i.PlanItemId != null && planItems.TryGetValue(i.PlanItemId.Value, out var pi) ? pi.CustomerId : null) ?? order.CustomerId;
                return cid != null && customers.TryGetValue(cid.Value, out var cn) ? cn : null;
            }).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).Distinct().ToList();

            if (itemCustomerNames.Count == 0 && order.CustomerId != null && customers.TryGetValue(order.CustomerId.Value, out var orderCust))
            {
                itemCustomerNames.Add(orderCust);
            }

            var customerSummary = itemCustomerNames.Count == 0 ? "عام / غير محدد"
                : itemCustomerNames.Count == 1 ? itemCustomerNames[0]
                : $"عدة عملاء ({itemCustomerNames.Count}): {string.Join("، ", itemCustomerNames)}";
            var byProductTotals = orderExecutions.SelectMany(e => e.ByProducts)
                .Where(b => b.Qty > 0)
                .GroupBy(b => new { b.ByProductId, b.SourceOrderItemId, b.RawProductId, b.RawLotId, b.CustomerId })
                .Select(g => new ByProductQtyDto
                {
                    ByProductId = g.Key.ByProductId, QtyKg = (double)g.Sum(b => b.Qty),
                    SourceOrderItemId = g.Key.SourceOrderItemId, RawProductId = g.Key.RawProductId,
                    RawLotId = g.Key.RawLotId, CustomerId = g.Key.CustomerId
                }).ToList();
            var byProductIds = byProductTotals.Select(b => b.ByProductId).ToHashSet();
            double consumedRawTotal = orderExecutions.Sum(e => e.ConsumedRawKg);
            double wastageTotal = orderExecutions.Sum(e => e.WastageQtyKg);
            double downtimeTotal = orderExecutions.SelectMany(e => e.Downtimes).Sum(d => d.Hours);
            string downtimeReasons = string.Join("؛ ", orderExecutions.SelectMany(e => e.Downtimes)
                .Select(d => d.ReasonAr).Where(reason => !string.IsNullOrWhiteSpace(reason)));
            bool hasDraftDelivery = ordersWithDraftDelivery.Contains(order.Id);
            double actualTotalKg = orderItems.Sum(i => i.ProducedQtyKg);
            double stockReceivedTotalKg = sourceLines.Sum(l => l.StockReceivedQtyKg);
            double remainingFinishedGoodsKg = actualTotalKg > stockReceivedTotalKg
                ? actualTotalKg - stockReceivedTotalKg : 0;

            result.Add(new ActualDeliveryOrderDto
            {
                OrderId = order.Id,
                Label = $"{order.DocumentNumber} — {customerSummary} — {shiftName}",
                Customer = customerSummary,
                Shift = shiftName,
                ProductionDate = order.ProductionDate?.ToString("dd/MM/yyyy"),
                OrderNumber = order.DocumentNumber,
                LineName = order.LineId is int lineId ? lineNames.GetValueOrDefault(lineId, "خط غير محدد") : "خط غير محدد",
                PlanNumber = planNum,
                PlannedQtyKg = orderItems.Sum(i => i.PlannedQtyKg),
                ActualQtyKg = actualTotalKg,
                StockReceivedQtyKg = stockReceivedTotalKg,
                RemainingQtyKg = remainingFinishedGoodsKg,
                WastageQtyKg = wastageTotal,
                ExecutionId = exe?.Id ?? 0,
                ProductionDeliveryId = productionDelivery?.Id ?? 0,
                Recorded = exe != null,
                CanRecord = can,
                CanCreateDelivery = exe != null && hasRemainingDelivery && !hasDraftDelivery
                    && productionDelivery?.Status != DocStatuses.Draft,
                Status = productionDelivery != null
                    ? $"آخر أمر تسليم: {productionDelivery.DocumentNumber} — {DocStatuses.ToArabic(productionDelivery.Status)}"
                        + (hasRemainingDelivery ? " — توجد كمية متبقية لتسليم جزئي جديد" : " — اكتمل التسليم")
                        + (can ? " — أمر الإنتاج غير مكتمل ويمكن استكماله" : "")
                    : exe != null ? "إنتاج فعلي محفوظ — لا تنشأ حركة مخزنية من شاشة الإنتاج"
                        + (can ? " — توجد كمية مخططة متبقية لاستكمال الإنتاج" : "")
                    : can ? "أدخل الفعلي فقط؛ المخطط ثابت من أمر الإنتاج" : "الأمر غير قابل للتسجيل (مغلق أو ملغى).",
                ReceiptNumber = Db.FinishedGoodsReceipts.AsNoTracking()
                    .Where(r => r.OrderId == order.Id && r.Status != DocStatuses.Cancelled && r.ReceiptNumber != null)
                    .OrderByDescending(r => r.Id).Select(r => r.ReceiptNumber).FirstOrDefault(),
                QualityNumber = qc?.DocumentNumber,
                ProductionDeliveryNumber = productionDelivery?.DocumentNumber,
                ProductionDeliveryStatus = productionDelivery == null ? null : DocStatuses.ToArabic(productionDelivery.Status),
                ConsumedRawKg = consumedRawTotal,
                DowntimeHours = downtimeTotal,
                DowntimeReason = string.IsNullOrWhiteSpace(downtimeReasons) ? null : downtimeReasons,
                Notes = exe?.ClosingNotes,
                RecordedByProductDefinitions = byProductIds.Where(id => byProductDefs.ContainsKey(id))
                    .Select(id => byProductDefs[id]).ToList(),
                ByProducts = byProductTotals,
                ByProductSources = orderItems.SelectMany(i =>
                {
                    int? itemCustId = i.CustomerId
                        ?? (i.PlanItemId != null && planCustomerByItem.TryGetValue(i.PlanItemId.Value, out var planCust) ? planCust : null)
                        ?? order.CustomerId;
                    var sourceLots = (i.LotId is int directLot ? new[] { directLot } : Array.Empty<int>())
                        .Concat(allocations.Where(a => a.OrderItemId == i.Id).Select(a => a.LotId)).Distinct().ToArray();
                    return sourceLots.Where(rawLots.ContainsKey).Select(rawLotId =>
                    {
                        var lot = rawLots[rawLotId];
                        return new ActualByProductSourceDto
                        {
                            OrderItemId = i.Id, RawProductId = lot.ProductId,
                            RawProductName = products.GetValueOrDefault(lot.ProductId, $"خام #{lot.ProductId}"),
                            RawLotId = lot.Id, RawLotCode = lot.LotCode,
                            CustomerId = itemCustId,
                            CustomerName = itemCustId is int cid && customers.TryGetValue(cid, out var cname) ? cname : "غير محدد"
                        };
                    });
                }).GroupBy(x => (x.OrderItemId, x.RawLotId, x.CustomerId)).Select(g => g.First()).ToList(),
                Items = orderItems.Select(i =>
                {
                    int? itemCustId = i.CustomerId ?? (i.PlanItemId != null && planItems.TryGetValue(i.PlanItemId.Value, out var pi) ? pi.CustomerId : null) ?? order.CustomerId;
                    string itemCustName = itemCustId != null && customers.TryGetValue(itemCustId.Value, out var cn) ? cn : customerSummary;

                    return new ActualDeliveryItemDto
                    {
                        OrderItemId = i.Id,
                        ProductId = i.ProductId,
                        LotId = i.LotId,
                        CustomerId = itemCustId,
                        PackagingTypeId = i.PackagingTypeId,
                        Product = i.ProductId != 0 && products.TryGetValue(i.ProductId, out var prodName) ? prodName : "صنف غير محدد",
                        Customer = itemCustName,
                        PlannedCartons = i.PlannedCartons,
                        ActualCartons = i.ProducedCartons,
                        PlannedQtyKg = i.PlannedQtyKg,
                        ActualQtyKg = i.ProducedQtyKg,
                        RawMaterialBasisKg = allocations.Any(a => a.OrderItemId == i.Id)
                            ? allocations.Where(a => a.OrderItemId == i.Id).Sum(a => a.AllocatedQtyKg)
                            : i.PlannedQtyKg,
                        PreviouslyReceivedQtyKg = sourceLines.Where(l => l.ByProductId == null && l.ProductId == i.ProductId
                                && l.LotId == i.LotId && l.CustomerId == itemCustId && l.PackagingTypeId == i.PackagingTypeId)
                            .Sum(l => l.StockReceivedQtyKg)
                    };
                }).ToList()
            });
        }
        return result;
    }

    public OpResult SaveActualProduction(ActualProductionDto input)
    {
        // One operation, not an implicit grant of warehouse or quality permissions.
        Require("production", "Create"); Require("execution", "Edit");
        ExecutionCloseTrace.Write($"SaveActualProduction ENTER OrderId={input?.OrderId.ToString() ?? "<null>"}");
        // تسجيل الفعلي ينتهي عند جلسة التنفيذ. لا يمنح ضمنياً صلاحيات المخازن
        // ولا ينشئ أمر/سند استلام تام؛ تلك مرحلة مستقلة بعد تحرير أمر التسليم.
        var result = RunOp(() =>
        {
            if (input == null) throw new DomainException("بيانات التنفيذ غير موجودة.");
            if (!double.IsFinite(input.ConsumedRawKg) || input.ConsumedRawKg < 0)
                throw new DomainException("قيمة الخام المحسوبة لا تكون سالبة أو غير صالحة؛ اترك حساب الخام للنظام.");
            if (!double.IsFinite(input.DowntimeHours) || input.DowntimeHours < 0 || input.DowntimeHours > 24)
                throw new DomainException("ساعات التوقف يجب أن تكون بين صفر و24 ساعة.");
            if (input.DowntimeHours > 0 && string.IsNullOrWhiteSpace(input.DowntimeReason))
                throw new DomainException("أدخل سبب التوقف في خانة السبب الواحدة.");
            if (input.DowntimeHours == 0 && !string.IsNullOrWhiteSpace(input.DowntimeReason))
                throw new DomainException("أدخل ساعات التوقف أو أفرغ سببه عندما لا يوجد توقف.");
            Db.ChangeTracker.Clear();
            var order = Db.ProductionOrders.Include(o => o.Items).ThenInclude(i => i.Allocations).SingleOrDefault(o => o.Id == input.OrderId)
                ?? throw new DomainException("أمر الإنتاج غير موجود.");
            if (order.SourceType == "Standalone")
            {
                if (order.ProductionDate != null && order.ProductionDate.Value.Date > Db.BusinessNow.Date)
                    throw new DomainException("لا يمكن تسجيل تنفيذ قبل تاريخ الأمر المجدول.");
            }
            else
            {
                var canonical = new ProductionOrderService(Db, Session, Numbering).GetScheduledProduction().Rows
                    .Where(r => r.OrderId == input.OrderId).ToList();
                if (canonical.Count == 0)
                {
                    canonical = GetFallbackPlanRows(new[] { input.OrderId })
                        .Where(r => r.OrderId == input.OrderId).ToList();
                    ExecutionCloseTrace.Write($"SaveActualProduction FALLBACK_ROWS OrderId={input.OrderId} Count={canonical.Count}");
                }
                if (canonical.Count == 0)
                    throw new DomainException("الأمر ليس نسخة مطابقة لخطة معتمدة مجدولة وغير مقفلة؛ حدّث الشاشة.");
                if (canonical.Any(r => !UiFormat.TryParseDate(r.ScheduledDate, out var day) || day.Date > Db.BusinessNow.Date))
                    throw new DomainException("لا يمكن تسجيل تنفيذ قبل يوم الخطة؛ يمكن تسجيل الأمر المتأخر حتى تاريخ اليوم.");
            }
            if (order.IsApproved && !order.IsClosed
                && order.Status is (DocStatuses.Draft or DocStatuses.Approved or DocStatuses.Scheduled or DocStatuses.PendingDelivery))
                order.Status = DocStatuses.InProgress;
            if (!order.IsApproved || order.IsClosed || order.Status is not (DocStatuses.InProgress or DocStatuses.Stopped))
                throw new DomainException("يلزم أمر اليوم المعتمد غير المقفل (الملغى أو المقفل لا يُسجَّل) — اختر الأمر الصحيح من القائمة.");
            if (!order.Items.Any(i => !i.IsClosed
                    && (i.ProducedCartons < i.PlannedCartons || i.ProducedQtyKg + 0.001 < i.PlannedQtyKg)))
                throw new DomainException("اكتمل الإنتاج المخطط للأمر؛ لا توجد كمية متبقية لتسجيل جلسة أخرى.");
            if (input.WarehouseRoute != ProductionWarehouseRoutes.Direct && input.WarehouseRoute != ProductionWarehouseRoutes.Quality)
                throw new DomainException("اختر مسار الإقفال: مباشر إلى مخزن التام أو إلى الفحص.", "WAREHOUSE_ROUTE_REQUIRED");
            if (input.Items == null || input.Items.Any(i => i == null)
                || input.Items.Select(i => i.OrderItemId).Distinct().Count() != input.Items.Count
                || !input.Items.Select(i => i.OrderItemId).ToHashSet().SetEquals(order.Items.Select(i => i.Id)))
                throw new DomainException("يلزم تسجيل بنود الأمر نفسها مرة واحدة، دون إضافة صنف أو إسقاط بند.");
            if (order.Items.Any(i => (i.LotId == null && i.Allocations.Count == 0) || i.PlannedCartons <= 0 || !double.IsFinite(i.PlannedQtyKg) || i.PlannedQtyKg <= 0))
                throw new DomainException("بيانات الأمر لا تتضمن دفعات الخام وكميات كرتونية سليمة؛ أصلح المرجع رسميًا دون اختيار صنف هنا.");
            var actual = new List<CloseItemQtyDto>();
            foreach (var row in input.Items)
            {
                var source = order.Items.Single(i => i.Id == row.OrderItemId);
                int remainingItemCartons = source.PlannedCartons - source.ProducedCartons;
                if (row.ActualCartons < 0 || row.ActualCartons > remainingItemCartons)
                    throw new DomainException($"فعلي هذه الجلسة لا يكون سالباً ولا يتجاوز متبقي البند ({Math.Max(0, remainingItemCartons):N0} كرتون)؛ لا تعديل تلقائي للخطة.");
                actual.Add(new CloseItemQtyDto { OrderItemId = source.Id, ProducedCartons = row.ActualCartons,
                    ProducedKg = row.ActualCartons * (source.PlannedQtyKg / source.PlannedCartons) });
            }
            if (actual.Sum(i => (long)i.ProducedCartons) is <= 0 or > int.MaxValue)
                throw new DomainException("أدخل كمية إنتاج فعلية قابلة للتسليم.");
            var secondary = input.ByProducts ?? new();
            if (secondary.Any(b => b == null || !double.IsFinite(b.QtyKg) || b.QtyKg <= 0 || b.QtyKg >= 1e15 || Math.Abs(b.QtyKg - Math.Round(b.QtyKg, 4)) > 1e-9 || Math.Round(b.QtyKg, 4) <= 0))
                throw new DomainException("اختر كل مخرج ثانوي بكمية موجبة صالحة للتخزين.");
            var sourceLotIds = order.Items.SelectMany(i => (i.LotId is int directLot
                    ? new[] { directLot } : Array.Empty<int>()).Concat(i.Allocations.Select(a => a.LotId)))
                .Distinct().ToList();
            var rawLotsById = Db.Lots.AsNoTracking().Where(l => sourceLotIds.Contains(l.Id)).ToDictionary(l => l.Id);
            var sourcePlanItemIds = order.Items.Where(i => i.PlanItemId != null).Select(i => i.PlanItemId!.Value).Distinct().ToList();
            var sourcePlanCustomers = Db.ProductionPlanItems.AsNoTracking().Where(pi => sourcePlanItemIds.Contains(pi.Id))
                .ToDictionary(pi => pi.Id, pi => pi.CustomerId);
            var sourceRows = order.Items.SelectMany(i =>
            {
                int? customerId = i.CustomerId
                    ?? (i.PlanItemId is int planItemId && sourcePlanCustomers.TryGetValue(planItemId, out var planCustomer) ? planCustomer : null)
                    ?? order.CustomerId;
                var lotIds = (i.LotId is int directLot ? new[] { directLot } : Array.Empty<int>())
                    .Concat(i.Allocations.Select(a => a.LotId)).Distinct().ToArray();
                return lotIds.Where(rawLotsById.ContainsKey).Select(lotId => new ByProductQtyDto
                {
                    SourceOrderItemId = i.Id, RawProductId = rawLotsById[lotId].ProductId,
                    RawLotId = lotId, CustomerId = customerId
                });
            }).ToList();
            foreach (var byProduct in secondary)
            {
                bool omittedSource = byProduct.SourceOrderItemId == null && byProduct.RawProductId == null && byProduct.RawLotId == null && byProduct.CustomerId == null;
                if (omittedSource)
                {
                    if (sourceRows.Count == 0)
                        throw new DomainException("لا توجد بنود خام مرتبطة بأمر الإنتاج لتخصيص المخرج الثانوي.");
                    byProduct.SourceOrderItemId = sourceRows[0].SourceOrderItemId;
                    byProduct.RawProductId = sourceRows[0].RawProductId;
                    byProduct.RawLotId = sourceRows[0].RawLotId;
                    byProduct.CustomerId = sourceRows[0].CustomerId;
                }
                if (byProduct.SourceOrderItemId == null || byProduct.RawProductId == null || byProduct.RawLotId == null
                    || !sourceRows.Any(source => source.SourceOrderItemId == byProduct.SourceOrderItemId
                        && source.RawProductId == byProduct.RawProductId && source.RawLotId == byProduct.RawLotId
                        && source.CustomerId == byProduct.CustomerId))
                    throw new DomainException("مصدر المخرج الثانوي (بند الخام/الدفعة/العميل) لا يطابق تخصيصاً في أمر الإنتاج.");
            }
            if (secondary.GroupBy(b => new { b.ByProductId, b.SourceOrderItemId, b.RawProductId, b.RawLotId, b.CustomerId }).Any(g => g.Count() > 1))
                throw new DomainException("المخرج الثانوي مكرر لنفس بند الخام والعميل؛ ادمج كميته في سطر واحد.");
            // ByProductId هو مفتاح تعريف المخرج الثانوي؛ لا نخلطه مع ProductId.
            var definitions = GetActualByProducts().Select(b => b.Id).ToHashSet();
            if (secondary.Any(b => !definitions.Contains(b.ByProductId)))
                throw new DomainException("المخرج الثانوي غير معرَّف في شاشة الأصناف (نوع: مخرج ثانوي) أو موقوف — أضفه من بطاقة الأصناف.");
            var beforeExecutions = Db.ProductionExecutions.AsNoTracking()
                .Where(e => e.OrderId == order.Id)
                .OrderBy(e => e.Id)
                .Select(e => $"Id={e.Id},OrderId={e.OrderId},IsDayClosed={e.IsDayClosed},Status={e.Status},EndDateTime={e.EndDateTime:O}")
                .ToList();
            ExecutionCloseTrace.Write($"SaveActualProduction BEFORE_CLOSE OrderId={order.Id} Executions=[{string.Join(" | ", beforeExecutions)}]");
            var execution = CreateActualExecutionService();
            void Must(OpResult r)
            {
                ExecutionCloseTrace.Write($"CloseProductionDay RESULT OrderId={order.Id} Ok={r.Ok} Message={r.Message}");
                if (!r.Ok) throw new DomainException(r.Message);
            }
            var closeResult = execution.CloseProductionDay(order.Id, actual.Sum(i => i.ProducedKg), actual.Sum(i => i.ProducedCartons),
                0, 0, 0, false, input.DowntimeHours > 0 ? new() { new DowntimeDto { Hours = input.DowntimeHours, ReasonAr = input.DowntimeReason.Trim() } } : new(),
                input.WarehouseRoute == ProductionWarehouseRoutes.Quality, input.Notes?.Trim(), secondary, input.ConsumedRawKg, actual);
            Must(closeResult);
            var exe = closeResult.Id > 0
                ? Db.ProductionExecutions.AsNoTracking().FirstOrDefault(e => e.Id == closeResult.Id && e.OrderId == order.Id)
                : null;
            ExecutionCloseTrace.Write($"SaveActualProduction AFTER_CLOSE OrderId={order.Id} ExecutionId={exe?.Id.ToString() ?? "<null>"} IsDayClosed={exe?.IsDayClosed.ToString() ?? "<null>"} Status={exe?.Status ?? "<null>"} EndDateTime={exe?.EndDateTime?.ToString("O") ?? "<null>"}");
            if (exe == null || exe.Status != DocStatuses.Completed || exe.EndDateTime == null)
                throw new DomainException(
                    "تم تسجيل العملية دون تثبيت إقفال يوم الإنتاج في سجل التنفيذ — لم تُعتمد العملية.",
                    "EXECUTION_CLOSE_NOT_PERSISTED");
            var persistedExecution = Db.ProductionExecutions.First(e => e.Id == exe.Id);
            persistedExecution.WarehouseRoute = input.WarehouseRoute;
            foreach (var row in actual.Where(i => i.ProducedCartons > 0))
            {
                var source = order.Items.Single(i => i.Id == row.OrderItemId);
                int? customerId = source.CustomerId
                    ?? (source.PlanItemId is int planId && sourcePlanCustomers.TryGetValue(planId, out var planCust) ? planCust : null)
                    ?? order.CustomerId;
                Db.ProductionExecutionItems.Add(new ProductionExecutionItem
                {
                    ExecutionId = exe.Id, OrderItemId = source.Id, ProductId = source.ProductId,
                    LotId = source.LotId, CustomerId = customerId, PackagingTypeId = source.PackagingTypeId,
                    ActualCartons = row.ProducedCartons, ActualQtyKg = (decimal)row.ProducedKg
                });
            }
            Db.SaveChanges();
            // لا نغلق رأس الأمر عند إقفال يوم جزئي؛ يبقى قابلاً للاستكمال حتى تكتمل بنوده.
            if (order.Items.All(i => i.IsClosed || i.ProducedQtyKg + 0.001 >= i.PlannedQtyKg))
            {
                order.Status = DocStatuses.Completed;
                order.IsClosed = true;
                order.ClosedDate = Db.BusinessNow;
                Db.SaveChanges();
            }
            var qc = Db.QualityChecks.SingleOrDefault(q => q.ExecutionId == exe.Id);
            if (qc != null)
            {
                qc.TotalCheckedCartons = actual.Sum(i => i.ProducedCartons);
                qc.CheckDate = Db.BusinessNow;
                qc.ExpectedCheckDate = Db.BusinessNow.Date.AddDays(2);
                foreach (var row in actual.Where(i => i.ProducedCartons > 0))
                {
                    var source = order.Items.Single(s => s.Id == row.OrderItemId);
                    qc.Items.Add(new QualityCheckItem
                    {
                        OrderItemId = source.Id,
                        ProductId = source.ProductId,
                        LotId = source.LotId,
                        CustomerId = source.CustomerId ?? order.CustomerId,
                        PackagingTypeId = source.PackagingTypeId,
                        CheckedCartons = 0,
                        CheckedQtyKg = 0,
                        Notes = $"مرسل للفحص: {row.ProducedCartons} كرتون / {row.ProducedKg:N1} كجم — لا كمية مقبولة قبل إدخال نتيجة الجودة"
                    });
                }
                Db.SaveChanges();
                RaiseWorkflowTask(new NewTaskRequest
                {
                    Stage = WorkflowStage.Quality,
                    TaskType = WorkflowTaskTypes.QualityCheck,
                    DocumentType = WorkflowDocTypes.QualityCheck,
                    DocumentId = qc.Id,
                    DocumentNumber = qc.DocumentNumber,
                    BusinessDate = Db.BusinessNow.Date,
                    DueDate = qc.ExpectedCheckDate,
                    RequiredCapability = WorkflowCapabilities.QualityInspect,
                    FromCapability = WorkflowCapabilities.ProductionDayClose,
                    Title = $"فحص جودة الإنتاج {qc.DocumentNumber} — أمر {order.DocumentNumber}"
                });
                Db.SaveChanges();
            }
            if (order.Status == DocStatuses.Completed || order.Items.All(i => i.ProducedCartons >= i.PlannedCartons))
            {
                CompleteDocumentTasks(WorkflowDocTypes.ProductionOrder, order.Id, WorkflowTaskTypes.PlanExecution, actionResult: "Executed", notes: "اكتمل تسجيل الإنتاج الفعلي");
                Db.SaveChanges();
            }
            // لا يُنشأ هنا سند استلام مخزني ولا تُرحّل أرصدة مخزن التام.
            // أمر تسليم الإنتاج يُنشأ لاحقاً صراحةً من التنفيذ الفعلي بواسطة مدير الإنتاج.
            int totalPlannedCartons = order.Items.Sum(i => i.PlannedCartons);
            int totalProducedCartons = order.Items.Sum(i => i.ProducedCartons);
            int remainingOrderCartons = totalPlannedCartons > totalProducedCartons
                ? totalPlannedCartons - totalProducedCartons : 0;
            _audit.Log("الإنتاج الفعلي", "تسجيل الفعلي وتوجيهه للمخزن/الجودة", "ProductionExecution", exe.DocumentNumber, exe.Id,
                newValues: new { order.Id, SessionCartons = exe.ActualCartons, TotalPlannedCartons = totalPlannedCartons,
                    TotalProducedCartons = totalProducedCartons, RemainingCartons = remainingOrderCartons,
                    Route = input.WarehouseRoute, exe.ConsumedRawKg, QualityId = qc?.Id });
            string routeMessage = input.WarehouseRoute == ProductionWarehouseRoutes.Direct
                ? "أُرسل مباشرة إلى قائمة استلام مخزن التام؛ لا يتطلب فحصاً. يحفظ أمين المخزن الاستلام لترحيله للمخزون."
                : "أُرسل إلى الفحص، ولن يظهر في قائمة استلام المخزن إلا بعد اكتمال الفحص واعتماده؛ تُرحّل الكمية المقبولة فقط.";
            return OpResult.Success(
                $"تم تسجيل جلسة الإنتاج الفعلي: {exe.ActualCartons:N0} كرتون. إجمالي الأمر {totalProducedCartons:N0} من {totalPlannedCartons:N0} كرتون؛ المتبقي {remainingOrderCartons:N0}. {routeMessage}",
                exe.Id, exe.DocumentNumber);
        });
        ExecutionCloseTrace.Write($"SaveActualProduction EXIT OrderId={input?.OrderId.ToString() ?? "<null>"} Ok={result.Ok} Message={result.Message}");
        return result;
    }
}
