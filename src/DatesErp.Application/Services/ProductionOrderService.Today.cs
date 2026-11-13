using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

public partial class ProductionOrderService
{
    public const string TodayOrdersOnlyMessage = "تم إيقاف مسار اختيار/تجزئة الأوامر. استخدم أمر إنتاج اليوم؛ الأصناف والكميات والتاريخ تأتي من خطة اليوم المعتمدة فقط.";
    public const string NoTodayPlanMessage = "لا توجد بنود إنتاج معتمدة ومجدولة لليوم. راجع جهة التخطيط؛ لا يمكن إنشاء أمر إنتاج.";
    protected override System.Data.IsolationLevel TransactionIsolation => System.Data.IsolationLevel.Serializable;

    private sealed record DayEntry(ProductionPlan Plan, ProductionPlanItem Item)
    {
        public int? Shift => Item.SuggestedShiftId ?? Plan.ShiftId;
        public int? Line => Item.SuggestedLineId ?? Plan.LineId;
    }

    private List<DayEntry> ApprovedScheduledEntries(DateTime? day = null)
    {
        // Production Order ينشأ فقط من خطة معتمدة (لا ملغاة ولا مغلقة).
        // الحالة المعتمدة هي المرجع، مع دعم خطط قديمة حُفظ لها محضر اعتماد لكن علم IsApproved غير مضبوط.
        // يلزم العلم أو بيانات اعتماد موثقة (المُعتمد والتاريخ) لمنع إصدار خطة سُحب اعتمادها مع بقاء حالة قديمة.
        var plans = Db.ProductionPlans.AsNoTracking()
            .Where(p => p.Status == DocStatuses.Approved && !p.IsClosed
                && (p.IsApproved || (p.ApprovedBy != null && p.ApprovedDate != null)))
            .ToDictionary(p => p.Id);
        var ids = plans.Keys.ToList();
        if (ids.Count == 0) return new List<DayEntry>();
        var sessions = Db.ProductionPlanSessions.AsNoTracking()
            .Where(s => ids.Contains(s.PlanId)).ToDictionary(s => s.Id);
        var items = Db.ProductionPlanItems.AsNoTracking()
            .Include(i => i.Allocations)
            .Where(i => ids.Contains(i.PlanId)).ToList();

        // توافق الخطط القديمة: بعض النسخ تحفظ يوم التشغيل في جلسة الخطة أو رأس الخطة اليومية،
        // ولا تكرر ScheduledDate في كل بند. لا نُسقطها لمجرد اختلاف شكل التخزين القديم.
        var scheduled = new List<DayEntry>();
        foreach (var item in items)
        {
            var plan = plans[item.PlanId];
            if (item.ScheduledDate is null)
            {
                if (item.SessionId is int sessionId && sessions.TryGetValue(sessionId, out var session)
                    && session.PlanId == item.PlanId)
                {
                    item.ScheduledDate = session.SessionDate.Date;
                    item.SuggestedShiftId ??= session.ShiftId;
                    item.SuggestedLineId ??= session.LineId;
                }
                else if (string.Equals(plan.PlanType, "Daily", StringComparison.OrdinalIgnoreCase)
                    && plan.StartDate is DateTime startDate && plan.EndDate is DateTime endDate
                    && startDate.Date == endDate.Date)
                {
                    item.ScheduledDate = startDate.Date;
                    item.SuggestedShiftId ??= plan.ShiftId;
                    item.SuggestedLineId ??= plan.LineId;
                }
            }
            if (item.ScheduledDate is not DateTime scheduledDate) continue;
            scheduledDate = scheduledDate.Date;
            if (day.HasValue && scheduledDate != day.Value.Date) continue;
            item.ScheduledDate = scheduledDate;
            scheduled.Add(new DayEntry(plan, item));
        }
        return scheduled.OrderBy(e => e.Item.ScheduledDate).ThenBy(e => e.Plan.Id)
            .ThenBy(e => e.Item.PriorityNo).ThenBy(e => e.Item.Id).ToList();
    }

    private List<DayEntry> TodayEntries(DateTime day) => ApprovedScheduledEntries(day);

    private List<ProductionOrderItem> ExistingItems(IEnumerable<int> planItemIds)
    {
        var ids = planItemIds.ToList();
        return Db.ProductionOrderItems.AsNoTracking()
            .Where(i => i.PlanItemId != null && ids.Contains(i.PlanItemId.Value))
            .Join(Db.ProductionOrders.AsNoTracking().Where(o => o.Status != DocStatuses.Cancelled),
                i => i.OrderId, o => o.Id, (i, o) => i).ToList();
    }

    public TodayProductionDto GetTodayProduction()
        => BuildProductionSheet(Db.BusinessNow.Date);

    public TodayProductionDto GetScheduledProduction()
        => BuildProductionSheet(null);

    public List<TodayProductionRowDto> GetOrderRows(int orderId)
    {
        try
        {
            var sheetRows = BuildProductionSheet(null).Rows.Where(r => r.OrderId == orderId).ToList();
            if (sheetRows.Count > 0) return sheetRows;
        }
        catch
        {
            // قد يفشل فحص الصلاحية في ورقة اليوم عند استعراض أمر سابق بصلاحية العرض؛ نكمل القراءة المباشرة للأمر.
        }

        var order = Db.ProductionOrders.AsNoTracking().Include(o => o.Items).FirstOrDefault(o => o.Id == orderId);
        if (order == null) return new List<TodayProductionRowDto>();

        var plan = order.SourcePlanId != null ? Db.ProductionPlans.AsNoTracking().FirstOrDefault(p => p.Id == order.SourcePlanId.Value) : null;
        var pItems = Db.ProductionOrderItems.AsNoTracking().Where(i => i.OrderId == orderId).ToList();
        var productIds = pItems.Select(i => i.ProductId).Distinct().ToList();
        var products = Db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionary(p => p.Id, p => p.ProductNameAr);
        var customerIds = pItems.Where(i => i.CustomerId != null).Select(i => i.CustomerId!.Value)
            .Concat(order.CustomerId != null ? new[] { order.CustomerId.Value } : Array.Empty<int>())
            .Distinct().ToList();
        var customers = Db.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionary(c => c.Id, c => c.CustomerName);
        var lotIds = pItems.Where(i => i.LotId != null).Select(i => i.LotId!.Value).Distinct().ToList();
        var lots = Db.Lots.AsNoTracking().Where(l => lotIds.Contains(l.Id)).ToDictionary(l => l.Id);
        var rawIds = lots.Values.Select(l => l.ProductId).Distinct().ToList();
        var rawNames = Db.Products.AsNoTracking().Where(p => rawIds.Contains(p.Id)).ToDictionary(p => p.Id, p => p.ProductNameAr);
        var shiftName = order.ShiftId != null ? Db.Shifts.AsNoTracking().Where(s => s.Id == order.ShiftId.Value).Select(s => s.ShiftNameAr).FirstOrDefault() : null;
        var lineName = order.LineId != null ? Db.ProductionLines.AsNoTracking().Where(l => l.Id == order.LineId.Value).Select(l => l.LineNameAr).FirstOrDefault() : null;
        var packageIds = pItems.Where(i => i.PackagingTypeId != null).Select(i => i.PackagingTypeId!.Value).Distinct().ToList();
        var packages = Db.PackagingTypes.AsNoTracking().Where(p => packageIds.Contains(p.Id)).ToDictionary(p => p.Id, p => p.PackageNameAr);

        return pItems.Select((it, index) =>
        {
            var custId = it.CustomerId ?? order.CustomerId;
            string custName = custId != null && customers.TryGetValue(custId.Value, out var cn) ? cn : "—";
            string prodName = products.GetValueOrDefault(it.ProductId, $"صنف #{it.ProductId}");
            string lotCode = it.LotId != null && lots.TryGetValue(it.LotId.Value, out var lot) ? lot.LotCode : "—";
            string rawName = it.LotId != null && lots.TryGetValue(it.LotId.Value, out var l2) && rawNames.TryGetValue(l2.ProductId, out var rn) ? rn : "—";
            string packName = it.PackagingTypeId != null && packages.TryGetValue(it.PackagingTypeId.Value, out var pn) ? pn : "—";

            return new TodayProductionRowDto
            {
                LineNo = index + 1,
                OrderId = order.Id,
                OrderNumber = order.DocumentNumber,
                PlanId = order.SourcePlanId ?? 0,
                PlanItemId = it.PlanItemId ?? 0,
                PlanNumber = plan?.DocumentNumber ?? "—",
                PlanTitle = plan?.PlanTitle ?? "—",
                CustomerId = custId,
                CustomerName = custName,
                ProductId = it.ProductId,
                ProductName = prodName,
                RawName = rawName,
                LotCode = lotCode,
                PackName = packName,
                PlannedCartons = it.PlannedCartons,
                PlannedKg = it.PlannedQtyKg,
                Unit = "كرتون",
                ShiftId = order.ShiftId,
                ShiftName = shiftName ?? "—",
                LineId = order.LineId,
                LineName = lineName ?? "—",
                ScheduledDate = order.ProductionDate?.ToString("yyyy-MM-dd") ?? "—",
                Status = DocStatuses.ToArabic(order.Status),
                IsPending = order.Status == DocStatuses.Draft,
                DayClosed = order.Status == DocStatuses.Closed
            };
        }).ToList();
    }

    private TodayProductionDto BuildProductionSheet(DateTime? onlyDay)
    {
        // قراءة القائمة لا تنشئ أرقاماً ولا أوامر ولا حركات مخزون.
        if (Session == null || !Session.Can("production", "View"))
            throw new PermissionDeniedException("عرض خطط أوامر الإنتاج");
        var businessDay = Db.BusinessNow.Date;
        var entries = ApprovedScheduledEntries(onlyDay);
        var existing = ExistingItems(entries.Select(e => e.Item.Id));
        var orderIds = existing.Select(i => i.OrderId).Distinct().ToList();
        var orders = Db.ProductionOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionary(o => o.Id);
        var allOrderItems = Db.ProductionOrderItems.AsNoTracking().Where(i => orderIds.Contains(i.OrderId)).ToList();
        var validOrderIds = orders.Values.Where(o =>
        {
            var group = entries.Where(e => e.Plan.Id == o.SourcePlanId
                && (o.CustomerId == null || e.Item.CustomerId == o.CustomerId)
                && e.Shift == o.ShiftId && e.Line == o.LineId
                && e.Item.ScheduledDate?.Date == o.ProductionDate?.Date).ToList();
            var children = allOrderItems.Where(i => i.OrderId == o.Id).ToList();
            return group.Count > 0 && children.Count == group.Count
                && children.Select(i => i.PlanItemId).Distinct().Count() == children.Count
                && children.All(i => group.Any(e => Matches(e, o, i)));
        }).Select(o => o.Id).ToHashSet();
        var productIds = entries.Select(e => e.Item.ProductId).Distinct().ToList();
        var products = Db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionary(p => p.Id, p => p.ProductNameAr);
        var customerIds = entries.Select(e => e.Item.CustomerId).Where(i => i != null).Select(i => i.Value).Distinct().ToList();
        var customers = Db.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionary(c => c.Id, c => c.CustomerName);
        var referencedLotIds = entries.SelectMany(e => e.Item.Allocations.Where(a => a.AllocatedQtyKg > 0).Select(a => a.LotId)
                .Concat(e.Item.Allocations.Any(a => a.AllocatedQtyKg > 0) || e.Item.LotId == null
                    ? Array.Empty<int>() : new[] { e.Item.LotId.Value }))
            .Distinct().ToList();
        var lots = Db.Lots.AsNoTracking().Where(l => referencedLotIds.Contains(l.Id)).ToDictionary(l => l.Id);
        var rawIds = lots.Values.Select(l => l.ProductId)
            .Concat(entries.Select(e => e.Item.SelectedRawProductId).Where(id => id != null).Select(id => id!.Value))
            .Distinct().ToList();
        var rawNames = Db.Products.AsNoTracking().Where(p => rawIds.Contains(p.Id)).ToDictionary(p => p.Id, p => p.ProductNameAr);
        var shipmentIds = lots.Values.Where(l => l.ShipmentId != null).Select(l => l.ShipmentId!.Value)
            .Concat(entries.Select(e => e.Item.ShipmentId).Where(id => id != null).Select(id => id!.Value))
            .Distinct().ToList();
        var shipmentNames = Db.Shipments.AsNoTracking().Where(x => shipmentIds.Contains(x.Id)).ToDictionary(x => x.Id, x => x.DocumentNumber);
        var packIds = entries.Select(e => e.Item.PackagingTypeId).Where(id => id != null).Select(id => id!.Value).Distinct().ToList();
        var packNames = Db.PackagingTypes.AsNoTracking().Where(p => packIds.Contains(p.Id)).ToDictionary(p => p.Id, p => p.PackageNameAr);
        var warehouseIds = entries.SelectMany(e => e.Item.Allocations.Select(a => a.WarehouseId)).Distinct().ToList();
        var warehouseNames = Db.Warehouses.AsNoTracking().Where(w => warehouseIds.Contains(w.Id)).ToDictionary(w => w.Id, w => w.WarehouseNameAr);
        var shifts = Db.Shifts.AsNoTracking().ToDictionary(s => s.Id, s => s.ShiftNameAr);
        var lines = Db.ProductionLines.AsNoTracking().ToDictionary(l => l.Id, l => l.LineNameAr);
        var dayClosedOrders = Db.ProductionExecutions.AsNoTracking()
            .Where(e => e.IsDayClosed && ((e.EndDateTime ?? e.StartDateTime) >= businessDay && (e.EndDateTime ?? e.StartDateTime) < businessDay.AddDays(1)))
            .Select(e => e.OrderId).ToHashSet();
        var anyDayClosedOrders = Db.ProductionExecutions.AsNoTracking()
            .Where(e => e.IsDayClosed)
            .Select(e => e.OrderId).ToHashSet();
        var rows = entries.Select((e, index) =>
        {
            var scheduled = e.Item.ScheduledDate?.Date;
            var isToday = scheduled == businessDay;
            var prev = existing.Where(i => i.PlanItemId == e.Item.Id).ToList();
            var oi = prev.Count == 1 ? prev[0] : null;
            var order = oi != null ? orders[oi.OrderId] : null;
            bool matches = order != null && validOrderIds.Contains(order.Id) && Matches(e, order, oi);
            bool closed = e.Plan.IsClosed || e.Plan.Status == DocStatuses.Closed || e.Item.IsClosed;
            bool orderFinished = order != null && (order.IsClosed
                || order.Status == DocStatuses.Completed
                || order.Status == DocStatuses.Closed
                || (oi != null && (oi.IsClosed || oi.ProducedQtyKg >= oi.PlannedQtyKg - 0.001)));
            // الأمر المرحّل جزئياً ليوم تالٍ يبقى ظاهراً في الأيام التالية ما لم يكتمل أو يُقفل يومه الحالي،
            // ولا يختفي للأبد بمجرد إقفال جلسة يوم سابق جزئية!
            bool isRowDayClosed = order != null && (orderFinished
                || dayClosedOrders.Contains(order.Id)
                || (onlyDay.HasValue && scheduled == onlyDay.Value.Date && anyDayClosedOrders.Contains(order.Id) && !order.Items.Any() && orderFinished));
            var rawRequirementError = closed ? null : PlanRawRequirementError(e.Item);
            var rowLotIds = e.Item.Allocations.Where(a => a.AllocatedQtyKg > 0).Select(a => a.LotId).Distinct().ToList();
            if (rowLotIds.Count == 0 && e.Item.LotId is int directLot) rowLotIds.Add(directLot);
            var rowLots = rowLotIds.Where(lots.ContainsKey).Select(id => lots[id]).ToList();
            var rowWarehouseNames = e.Item.Allocations.Select(a => a.WarehouseId).Distinct()
                .Where(warehouseNames.ContainsKey).Select(id => warehouseNames[id]).ToList();
            return new TodayProductionRowDto
            {
                LineNo = index + 1,
                PlanId = e.Plan.Id, PlanItemId = e.Item.Id, PlanNumber = e.Plan.DocumentNumber, PlanTitle = e.Plan.PlanTitle,
                CustomerId = e.Item.CustomerId,
                CustomerName = e.Item.CustomerId is int c && customers.TryGetValue(c, out var cn) ? cn : "غير محدد في الخطة",
                RawName = rowLots.Count > 0
                    ? string.Join(" + ", rowLots.Select(l => rawNames.GetValueOrDefault(l.ProductId, "خام غير محدد")).Distinct())
                    : e.Item.SelectedRawProductId is int rawId ? rawNames.GetValueOrDefault(rawId, "خام غير محدد") : "—",
                LotCode = string.Join(" + ", rowLots.Select(l => l.LotCode).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct()),
                ShipmentNo = string.Join(" + ", rowLots.Where(l => l.ShipmentId != null)
                    .Select(l => shipmentNames.GetValueOrDefault(l.ShipmentId!.Value, ""))
                    .Concat(e.Item.ShipmentId is int shipmentId ? new[] { shipmentNames.GetValueOrDefault(shipmentId, "") } : Array.Empty<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct()),
                WarehouseName = string.Join(" + ", rowWarehouseNames.Distinct()),
                PackName = e.Item.PackagingTypeId is int packId ? packNames.GetValueOrDefault(packId, "عبوة غير محددة") : "—",
                ProductId = e.Item.ProductId, ProductName = products.GetValueOrDefault(e.Item.ProductId, "صنف غير موجود — راجع التخطيط"),
                PlannedCartons = e.Item.PlannedCartons, PlannedKg = e.Item.PlannedQtyKg,
                ScheduledDate = scheduled?.ToString("dd/MM/yyyy"), IsToday = isToday,
                ShiftId = e.Shift, ShiftName = e.Shift is int sh ? shifts.GetValueOrDefault(sh, "وردية غير موجودة") : "غير محددة في الخطة",
                LineId = e.Line, LineName = e.Line is int ln ? lines.GetValueOrDefault(ln, "خط غير موجود") : "غير محدد في الخطة",
                OrderId = matches ? order.Id : null, OrderNumber = matches ? order.DocumentNumber : "—",
                IsPending = prev.Count == 0 && !closed && rawRequirementError == null,
                DayClosed = isRowDayClosed,
                Status = rawRequirementError != null ? rawRequirementError :
                    prev.Count > 0 && !matches ? "تعارض أمر سابق مع الخطة — يلزم مراجعة رسمية" :
                    closed ? "مقفل" : order != null ? DocStatuses.ToArabic(order.Status) : "جاهز للإصدار من الخطة"
            };
        }).ToList();
        string message = rows.Count == 0
            ? onlyDay.HasValue ? NoTodayPlanMessage : "لا توجد بنود في الخطط المعتمدة المجدولة للعرض."
            : onlyDay.HasValue
                ? "ماذا سننتج اليوم؟ وكم سننتج؟ البيانات من الخطة المعتمدة؛ لا إضافة أو تعديل هنا."
                : "الخطط المعتمدة المجدولة — السابقة والحالية والقادمة. الأصناف والكميات والعملاء من الخطة الأصلية.";
        return new TodayProductionDto
        {
            Day = businessDay,
            Rows = rows,
            CanIssue = rows.Any(r => r.IsToday && r.IsPending),
            Message = message
        };
    }

    private OpResult RunTodayWrite(Func<OpResult> work)
    {
        try { return RunOp(work); }
        catch (SqlException ex) when (ex.Number == 1205)
        { return OpResult.Fail("تزامن إصدار أوامر اليوم من جهاز آخر. لم تحفظ العملية جزئياً؛ حدّث الشاشة وأعد المحاولة."); }
    }

    public OpResult IssueTodayOrders()
    {
        Require("production", "Create");
        return RunTodayWrite(() =>
        {
            var day = Db.BusinessNow.Date;
            var entries = TodayEntries(day);
            if (entries.Count == 0) throw new DomainException(NoTodayPlanMessage);
            var existing = ExistingItems(entries.Select(e => e.Item.Id));
            // Never silently top up a legacy partial order or mask a conflicting prior order.
            foreach (var e in entries.Where(e => !e.Item.IsClosed && !e.Plan.IsClosed))
            {
                var prior = existing.Where(i => i.PlanItemId == e.Item.Id).ToList();
                if (prior.Count == 0) continue;
                var order = prior.Count == 1 ? Db.ProductionOrders.AsNoTracking().First(o => o.Id == prior[0].OrderId) : null;
                if (order == null || !Matches(e, order, prior[0]) || order.ProductionDate?.Date != day)
                    throw new DomainException("يوجد أمر سابق جزئي أو مخالف للخطة؛ لا تجزئة أو استكمال تلقائي. راجع جهة التخطيط قبل إصدار اليوم.");
            }
            var pending = entries.Where(e => !e.Item.IsClosed && !e.Plan.IsClosed && e.Plan.Status == DocStatuses.Approved && !existing.Any(i => i.PlanItemId == e.Item.Id)).ToList();
            int created = 0;
            foreach (var g in pending.GroupBy(e => new { e.Plan.Id, e.Shift, e.Line }))
            {
                var result = SaveScheduledGroup("FromPlan", g.Key.Id, null, day.ToString("dd/MM/yyyy"),
                    g.Key.Shift, g.Key.Line, g.Select(e => FromPlan(e.Item)).ToList(), requireToday: true, allowMultipleCustomers: true);
                if (!result.Ok) throw new DomainException(result.Message);
                created++;
            }
            if (Db.BusinessNow.Date != day) throw new DomainException("تغير يوم العمل أثناء الإصدار؛ حدّث الشاشة وأعد المحاولة.");
            return OpResult.Success(created == 0 ? "أوامر بنود اليوم صادرة أو مقفلة بالفعل؛ لم يُنشأ أمر مكرر." :
                $"تم إصدار {created} أمر من جميع بنود اليوم المعتمدة بالكميات الأصلية، دون إعادة اختيار أو تعديل. لم يُصرف مخزون عند الإصدار.");
        });
    }

    private static OrderItemDto FromPlan(ProductionPlanItem p) => new()
    {
        PlanItemId = p.Id, CustomerId = p.CustomerId, ProductId = p.ProductId, LotId = p.LotId,
        ShipmentId = p.ShipmentId, PackagingTypeId = p.PackagingTypeId,
        PlannedCartons = p.PlannedCartons, PlannedQtyKg = p.PlannedQtyKg,
        Allocations = p.Allocations.Select(a => new PlanItemAllocationDto
        {
            WarehouseId = a.WarehouseId, LotId = a.LotId, CustomerId = a.CustomerId, AllocatedQtyKg = a.AllocatedQtyKg
        }).ToList()
    };

    private OpResult SaveTodayGroup(string sourceType, int? planId, int? customerId, string productionDate,
        int? shiftId, int? lineId, List<OrderItemDto> requested)
        => SaveScheduledGroup(sourceType, planId, customerId, productionDate, shiftId, lineId, requested, requireToday: true);

    /// <summary>
    /// ينشئ أمراً من مجموعة بنود خطة معتمدة. مسار اليوم يستدعيه مع requireToday=true؛
    /// ومسار «إضافة من الخطط» يسمح بتاريخ الجدولة السابق أو الحالي أو اللاحق، مع بقاء
    /// الهوية والكميات ومراجع الخطة وحراس الطاقة كما هي.
    /// </summary>
    private OpResult SaveScheduledGroup(string sourceType, int? planId, int? customerId, string productionDate,
        int? shiftId, int? lineId, List<OrderItemDto> requested, bool requireToday, bool allowMultipleCustomers = false)
    {
        if (sourceType != "FromPlan" || planId == null)
            throw new DomainException("أمر الإنتاج لا ينشأ يدوياً؛ يلزم مرجع خطة معتمدة (Approved Production Plan فقط).");
        if (!UiFormat.TryParseDate(productionDate, out var day))
            throw new DomainException("تاريخ أمر الإنتاج غير صالح؛ اختر تاريخ البند من الخطة.");
        day = day.Date;
        var businessDay = Db.BusinessNow.Date;
        if (requireToday && day != businessDay)
            throw new DomainException("أمر إنتاج اليوم يجب أن يطابق يوم العمل الحالي.");
        // الخطة السابقة غير المصدرة تبقى قابلة للترحيل كمتأخرات؛ يُحفظ تاريخها الأصلي.
        // بدء التنفيذ له حارس مستقل يسمح بالأمر المتأخر في يوم العمل الحالي فقط.

        var planCheck = Db.ProductionPlans.AsNoTracking().FirstOrDefault(p => p.Id == planId);
        if (planCheck == null) throw new DomainException("الخطة المرجعية غير موجودة.");
        if (planCheck.Status != DocStatuses.Approved || planCheck.IsClosed
            || (!planCheck.IsApproved && (planCheck.ApprovedBy == null || planCheck.ApprovedDate == null)))
            throw new DomainException($"لا يمكن إنشاء أمر إنتاج من خطة غير معتمدة أو ملغاة أو مغلقة — حالة الخطة: {DocStatuses.ToArabic(planCheck.Status)}", "PLAN_NOT_APPROVED");
        if (requested == null || requested.Count == 0 || requested.Any(i => i.PlanItemId == null) || requested.Select(i => i.PlanItemId).Distinct().Count() != requested.Count)
            throw new DomainException("يلزم نقل بنود الخطة الأصلية كاملة دون بنود يدوية أو مكررة.");

        var entries = TodayEntries(day).Where(e => e.Plan.Id == planId && e.Plan.Status == DocStatuses.Approved && !e.Plan.IsClosed && !e.Item.IsClosed).ToList();
        if (entries.Count == 0)
            throw new DomainException(requireToday ? NoTodayPlanMessage : "لا توجد بنود خطة معتمدة ومجدولة بهذا التاريخ.");
        var existing = ExistingItems(entries.Select(e => e.Item.Id));
        var group = (allowMultipleCustomers
            ? entries.Where(e => e.Shift == shiftId && e.Line == lineId)
            : entries.Where(e => e.Item.CustomerId == customerId && e.Shift == shiftId && e.Line == lineId)).ToList();
        var groupIds = group.Select(e => e.Item.Id).ToHashSet();
        var requestedIds = requested.Select(i => i.PlanItemId!.Value).ToHashSet();
        var existingGroupItems = existing.Where(i => i.PlanItemId is int id && groupIds.Contains(id)).ToList();
        if (existingGroupItems.Count > 0)
            throw new DomainException(existingGroupItems.Count == group.Count
                ? "هذه المجموعة لها أمر إنتاج سابق؛ يمنع إنشاء أمر مكرر لنفس بنود الخطة."
                : "يوجد إصدار جزئي سابق لهذه المجموعة؛ لا يُستكمل الأمر تلقائياً ولا يُنشأ أمر ثانٍ. راجع التخطيط.");
        if (group.Count == 0 || !groupIds.SetEquals(requestedIds))
            throw new DomainException($"بنود الأمر يجب أن تطابق مجموعة الخطة المعتمدة ليوم {day:dd/MM/yyyy} كاملة؛ لا اختيار جزئي أو صنف إضافي أو تكرار أمر سابق.");
        foreach (var e in group)
        {
            var dto = requested.Single(i => i.PlanItemId == e.Item.Id); var p = e.Item;
            if (dto.ProductId != p.ProductId || dto.LotId != p.LotId || dto.PackagingTypeId != p.PackagingTypeId
                || (dto.CustomerId != null && dto.CustomerId != p.CustomerId) || (dto.ShipmentId != null && dto.ShipmentId != p.ShipmentId)
                || dto.PlannedCartons != p.PlannedCartons || dto.PlannedQtyKg != p.PlannedQtyKg)
                throw new DomainException("هوية بند أمر الإنتاج وكميته يجب أن تطابق الخطة المعتمدة تماماً؛ لا زيادة أو تخفيض أو تبديل للصنف/العميل/الدفعة/العبوة.");
            if (p.PlannedCartons <= 0 || !double.IsFinite(p.PlannedQtyKg) || p.PlannedQtyKg <= 0)
                throw new DomainException("كمية الخطة غير صالحة؛ صحح الخطة رسمياً، لا كمية أمر الإنتاج.");
            if ((e.Plan.StartDate != null && day < e.Plan.StartDate.Value.Date) || (e.Plan.EndDate != null && day > e.Plan.EndDate.Value.Date))
                throw new DomainException("تاريخ بند الخطة خارج فترتها المعتمدة؛ راجع التخطيط.");
            var rawRequirementError = PlanRawRequirementError(p);
            if (rawRequirementError != null)
                throw new DomainException(rawRequirementError, "RAW_REQUIREMENT_REQUIRED");
            ProductIdentityGuard.EnsurePlanningLink(Db, p.ProductId, p.LotId, null);
            UnitsPolicy.RequireItemType(Db, p.ProductId, "Finished", "أمر الإنتاج");
            UnitsPolicy.RequireCartonWeight(Db, p.ProductId, p.PackagingTypeId, p.PlannedCartons, "تعريف الخطة عند إصدار أمر الإنتاج");
            _ = UnitsPolicy.RequireCartonKgMatch(Db, p.ProductId, p.PackagingTypeId,
                p.PlannedQtyKg, p.PlannedCartons, "راجع تعريف العبوة والخطة رسمياً؛ لا تصحح كمية الأمر هنا");
            if (p.LotId is int lotId)
            {
                var lot = Db.Lots.AsNoTracking().FirstOrDefault(l => l.Id == lotId);
                if (lot == null || lot.CustomerId != p.CustomerId || (p.ShipmentId != null && lot.ShipmentId != p.ShipmentId))
                    throw new DomainException("هوية العميل/الشحنة في الخطة لا تطابق دفعة الخام؛ راجع جهة التخطيط.");
            }
        }
        var order = new ProductionOrder { SourceType = "FromPlan", SourcePlanId = planId, CustomerId = customerId,
            ProductionDate = day, ShiftId = shiftId, LineId = lineId, Status = DocStatuses.Draft,
            DocumentNumber = Numbering.Next("ORD") };
        foreach (var e in group)
        {
            var p = e.Item;
            var def = UnitsPolicy.PackagingDefinition(Db, p.ProductId, p.PackagingTypeId);
            var orderItem = new ProductionOrderItem { PlanItemId = p.Id, CustomerId = p.CustomerId, ProductId = p.ProductId,
                LotId = p.LotId, ShipmentId = p.ShipmentId, PackagingTypeId = p.PackagingTypeId,
                PlannedCartons = p.PlannedCartons, PlannedQtyKg = p.PlannedQtyKg, Status = DocStatuses.Draft,
                CartonWeightKg = UnitsPolicy.CartonWeight(Db, p.ProductId, p.PackagingTypeId), MoldsCount = def.MoldsCount, MoldWeightKg = (decimal)def.MoldWeightKg };
            foreach (var a in p.Allocations)
                orderItem.Allocations.Add(new ProductionOrderItemAllocation
                {
                    PlanAllocationId = a.Id, WarehouseId = a.WarehouseId, LotId = a.LotId,
                    AllocatedQtyKg = a.AllocatedQtyKg
                });
            order.Items.Add(orderItem);
        }
        ValidateSourceCapacity(order);
        Db.ProductionOrders.Add(order); Db.SaveChanges();
        CalculateMaterials(order);
        RaiseWorkflowTask(new NewTaskRequest
        {
            Stage = WorkflowStage.ProductionOrder,
            TaskType = WorkflowTaskTypes.PlanExecution,
            DocumentType = WorkflowDocTypes.ProductionOrder,
            DocumentId = order.Id,
            DocumentNumber = order.DocumentNumber,
            BusinessDate = day,
            DueDate = day,
            RequiredCapability = WorkflowCapabilities.ProductionExecute,
            FromCapability = WorkflowCapabilities.ProductionOrderIssue,
            Title = $"تنفيذ أمر الإنتاج {order.DocumentNumber} — يوم {day:dd/MM/yyyy}"
        });
        if (order.SourcePlanId is int spId)
            CompleteDocumentTasks(WorkflowDocTypes.ProductionPlan, spId, WorkflowTaskTypes.DailyRun, businessDate: day, actionResult: "Issued", notes: $"صدر الأمر {order.DocumentNumber}");
        Db.SaveChanges();
        return OpResult.Success($"تم نقل بنود الخطة المعتمدة ليوم {day:dd/MM/yyyy} كما هي إلى أمر الإنتاج.", order.Id, order.DocumentNumber);
    }

    private static bool Matches(DayEntry e, ProductionOrder o, ProductionOrderItem i) =>
        o.SourceType == "FromPlan" && o.SourcePlanId == e.Plan.Id
        && (o.CustomerId == null || o.CustomerId == e.Item.CustomerId)
        && o.ShiftId == e.Shift && o.LineId == e.Line && i.PlanItemId == e.Item.Id
        && i.ProductId == e.Item.ProductId && i.CustomerId == e.Item.CustomerId && i.LotId == e.Item.LotId
        && i.ShipmentId == e.Item.ShipmentId && i.PackagingTypeId == e.Item.PackagingTypeId
        && i.PlannedCartons == e.Item.PlannedCartons && i.PlannedQtyKg == e.Item.PlannedQtyKg;

    // ═══ «إضافة أمر» من شاشة الإنتاج: مجموعات خطة معتمدة لم تُصدر بعد ═══
    private List<TodayPendingGroupDto> BuildPendingGroups(IEnumerable<DayEntry> source)
    {
        // لا نخفي الخطة بسبب تاريخها أو نقص تعريف خام قديم؛ اعرض المجموعة للمراجعة.
        // يبقى إنشاء الأمر محمياً بإعادة التحقق في SaveScheduledGroup.
        var entries = source.Where(e => e.Plan.Status == DocStatuses.Approved && !e.Plan.IsClosed && !e.Item.IsClosed).ToList();
        var existingIds = ExistingItems(entries.Select(e => e.Item.Id))
            .Select(i => i.PlanItemId).Where(id => id != null).Select(id => id!.Value).ToHashSet();

        // مجموعة الاختيار أمر واحد يضم كل عملاء الخطة لنفس التاريخ/الوردية/الخط.
        // إذا صدر أي بند من المجموعة سابقاً، تُخفى المجموعة كلها منعاً لإصدار جزئي/مكرر.
        return entries
            .GroupBy(e => new
            {
                e.Plan.Id,
                e.Plan.DocumentNumber,
                ScheduledDate = e.Item.ScheduledDate!.Value.Date,
                e.Shift,
                e.Line
            })
            .Where(g => g.All(e => !existingIds.Contains(e.Item.Id)))
            .Select(g =>
            {
                var customerIds = g.Select(e => e.Item.CustomerId).Where(id => id != null)
                    .Select(id => id!.Value).Distinct().ToList();
                var customerNames = customerIds.Count == 0
                    ? new List<string> { "غير محدد في الخطة" }
                    : Db.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id))
                        .OrderBy(c => c.CustomerName).Select(c => c.CustomerName).ToList();
                var rawError = g.Select(e => PlanRawRequirementError(e.Item)).FirstOrDefault(error => error != null);
                return new TodayPendingGroupDto
                {
                    PlanId = g.Key.Id,
                    PlanNumber = g.Key.DocumentNumber,
                    PlanTitle = g.First().Plan.PlanTitle,
                    ScheduledDate = g.Key.ScheduledDate.ToString("dd/MM/yyyy"),
                    CustomerId = customerIds.Count == 1 ? customerIds[0] : null,
                    CustomerName = string.Join("، ", customerNames),
                    Customers = string.Join("، ", customerNames),
                    CustomerCount = customerIds.Count,
                    ShiftId = g.Key.Shift,
                    ShiftName = g.Key.Shift != null ? Db.Shifts.AsNoTracking().Where(x => x.Id == g.Key.Shift).Select(x => x.ShiftNameAr).FirstOrDefault() ?? $"وردية #{g.Key.Shift}" : "-",
                    LineId = g.Key.Line,
                    LineName = g.Key.Line != null ? Db.ProductionLines.AsNoTracking().Where(x => x.Id == g.Key.Line).Select(x => x.LineNameAr).FirstOrDefault() ?? $"خط #{g.Key.Line}" : "-",
                    ItemsCount = g.Count(),
                    Cartons = g.Sum(x => x.Item.PlannedCartons),
                    PlannedKg = g.Sum(x => x.Item.PlannedQtyKg),
                    CanIssue = rawError == null,
                    BlockReason = rawError
                };
            })
            .OrderBy(x => x.ScheduledDate).ThenBy(x => x.PlanId).ThenBy(x => x.Customers).ToList();
    }

    public List<TodayPendingGroupDto> GetTodayPendingGroups()
        => BuildPendingGroups(TodayEntries(Db.BusinessNow.Date));

    /// <summary>
    /// يعرض كل مجموعات الخطط المعتمدة المجدولة السابقة والحالية والقادمة، لا اليوم الحالي فقط.
    /// شاشة «أمر إنتاج اليوم» تبقى مقيدة بيوم العمل، أما زر «إضافة من الخطة» فيحتاج هذا المسار
    /// حتى لا تختفي الخطط المتأخرة أو المستقبلية ولا يُجبر المستخدم على إعادة إدخال أمر يدوي.
    /// </summary>
    public List<TodayPendingGroupDto> GetPendingPlanGroups()
    {
        if (Session == null || !Session.Can("production", "View"))
            throw new PermissionDeniedException("عرض الخطط المجدولة لأوامر الإنتاج");
        // لا نخفي خطة سابقة لم تُصدر بعد؛ تعرض الواجهة حالتها وتسمح بمراجعة/إصدارها.
        return BuildPendingGroups(ApprovedScheduledEntries());
    }

    public OpResult IssueTodayGroup(int planId, int? customerId, int? shiftId, int? lineId)
    {
        Require("production", "Create");
        return RunTodayWrite(() =>
        {
            var day = Db.BusinessNow.Date;
            var entries = TodayEntries(day)
                .Where(e => e.Plan.Id == planId && e.Plan.Status == DocStatuses.Approved && !e.Plan.IsClosed && !e.Item.IsClosed
                    && e.Item.CustomerId == customerId && e.Shift == shiftId && e.Line == lineId).ToList();
            var existing = ExistingItems(entries.Select(e => e.Item.Id));
            var group = entries.Where(e => !existing.Any(i => i.PlanItemId == e.Item.Id)).ToList();
            if (group.Count == 0) return OpResult.Fail("هذه المجموعة صادرة سابقاً أو لم تعد متاحة — حدّث الشاشة.");
            // المسار الواحد: نفس SaveTodayGroup حرفياً بنفس قواعد الهوية والكمية والطاقة واحتساب المواد.
            var result = SaveTodayGroup("FromPlan", planId, customerId, day.ToString("dd/MM/yyyy"), shiftId, lineId, group.Select(e => FromPlan(e.Item)).ToList());
            if (!result.Ok) return result;
            return OpResult.Success($"تم إصدار أمر الإنتاج {result.DocumentNumber} — {group.Count} بنداً بكميات الخطة الأصلية.", result.Id, result.DocumentNumber);
        });
    }

    /// <summary>إصدار مجموعة من خطة سابقة أو حالية أو مستقبلية مع حفظ تاريخ الجدولة الأصلي.</summary>
    public OpResult IssuePlanGroup(int planId, string scheduledDate, int? customerId, int? shiftId, int? lineId)
    {
        Require("production", "Create");
        return RunTodayWrite(() =>
        {
            if (!UiFormat.TryParseDate(scheduledDate, out var day))
                return OpResult.Fail("تاريخ الخطة غير صالح؛ حدّث الشاشة واختر تاريخاً مجدولاً صحيحاً.");
            day = day.Date;
            var entries = TodayEntries(day)
                .Where(e => e.Plan.Id == planId && e.Plan.Status == DocStatuses.Approved && !e.Plan.IsClosed && !e.Item.IsClosed
                    && e.Item.CustomerId == customerId && e.Shift == shiftId && e.Line == lineId).ToList();
            var existing = ExistingItems(entries.Select(e => e.Item.Id));
            var group = entries.Where(e => !existing.Any(i => i.PlanItemId == e.Item.Id)).ToList();
            if (group.Count == 0) return OpResult.Fail("هذه المجموعة صادرة سابقاً أو لم تعد متاحة — حدّث الشاشة.");
            var result = SaveScheduledGroup("FromPlan", planId, customerId, day.ToString("dd/MM/yyyy"), shiftId, lineId,
                group.Select(e => FromPlan(e.Item)).ToList(), requireToday: false);
            if (!result.Ok) return result;
            return OpResult.Success($"تم إنشاء أمر الإنتاج {result.DocumentNumber} ليوم {day:dd/MM/yyyy} — {group.Count} بنداً بكميات الخطة الأصلية.", result.Id, result.DocumentNumber);
        });
    }

    /// <summary>
    /// إصدار أمر واحد متعدد العملاء من نفس الخطة والتاريخ والوردية والخط.
    /// يبقى العميل في كل بند، بينما يترك CustomerId في رأس الأمر فارغاً ليدل على التعدد.
    /// </summary>
    public OpResult IssuePlanMultiCustomerGroup(int planId, string scheduledDate, int? shiftId, int? lineId)
    {
        Require("production", "Create");
        return RunTodayWrite(() =>
        {
            if (!UiFormat.TryParseDate(scheduledDate, out var day))
                return OpResult.Fail("تاريخ الخطة غير صالح؛ حدّث الشاشة واختر تاريخاً مجدولاً صحيحاً.");
            day = day.Date;
            var entries = TodayEntries(day)
                .Where(e => e.Plan.Id == planId && e.Plan.Status == DocStatuses.Approved && !e.Plan.IsClosed && !e.Item.IsClosed
                    && e.Shift == shiftId && e.Line == lineId).ToList();
            var existing = ExistingItems(entries.Select(e => e.Item.Id));
            if (existing.Count > 0)
                return OpResult.Fail("يوجد إصدار سابق لبند من هذه المجموعة؛ لا يُنشأ أمر متعدد العملاء جزئياً. راجع الأمر السابق أو التخطيط.");
            if (entries.Count == 0)
                return OpResult.Fail("لا توجد بنود خطة معتمدة ومجدولة لهذه المجموعة.");
            var result = SaveScheduledGroup("FromPlan", planId, null, day.ToString("dd/MM/yyyy"), shiftId, lineId,
                entries.Select(e => FromPlan(e.Item)).ToList(), requireToday: false, allowMultipleCustomers: true);
            if (!result.Ok) return result;
            var customers = entries.Select(e => e.Item.CustomerId).Distinct().Count();
            return OpResult.Success($"تم إنشاء أمر إنتاج واحد متعدد العملاء ليوم {day:dd/MM/yyyy} — {customers} عملاء و{entries.Count} بنداً.", result.Id, result.DocumentNumber);
        });
    }

    private void ValidateTodayOrder(ProductionOrder order)
        => ValidateScheduledOrder(order, requireToday: true);

    private void ValidateScheduledOrder(ProductionOrder order, bool requireToday)
    {
        if (order.ProductionDate == null || order.SourcePlanId == null || order.SourceType != "FromPlan")
            throw new DomainException("أمر الإنتاج يجب أن يكون مرتبطاً بخطة إنتاج معتمدة؛ لا أمر يدوي.");
        var day = order.ProductionDate.Value.Date;
        if (requireToday && day > Db.BusinessNow.Date)
            throw new DomainException("لا يبدأ التنفيذ قبل يوم الخطة؛ يمكن تنفيذ الأمر المتأخر اليوم إذا كان تاريخه أقدم.");
        var entries = TodayEntries(day).Where(e => e.Plan.Id == order.SourcePlanId && e.Plan.Status == DocStatuses.Approved && !e.Plan.IsClosed && !e.Item.IsClosed
            && (order.CustomerId == null || e.Item.CustomerId == order.CustomerId)
            && e.Shift == order.ShiftId && e.Line == order.LineId).ToList();
        if (entries.Count == 0 || order.Items.Count == 0 || order.Items.Count > entries.Count
            || order.Items.Any(i => !entries.Any(e => Matches(e, order, i)))
            || order.Items.Select(i => i.PlanItemId).Distinct().Count() != order.Items.Count)
            throw new DomainException("أمر الإنتاج لا يطابق بنود وكميات الخطة المعتمدة؛ أوقف التنفيذ وراجع جهة التخطيط.");
        var prev = ExistingItems(order.Items.Where(i => i.PlanItemId != null).Select(i => i.PlanItemId!.Value));
        if (prev.Any(i => i.OrderId != order.Id)) throw new DomainException("يوجد أمر آخر لنفس بند الخطة؛ يمنع التنفيذ المكرر.");
        foreach (var i in order.Items) ProductIdentityGuard.EnsurePlanningLink(Db, i.ProductId, i.LotId, null);
    }

    private List<string> ValidateSourceCapacity(ProductionOrder order)
    {
        // An order adds no load to its plan. Re-evaluate the source plan exactly once, using the shared evaluator.
        var plan = Db.ProductionPlans.AsNoTracking().Include(p => p.Items).First(p => p.Id == order.SourcePlanId);
        var openPlanItems = plan.Items.Where(i => !i.IsClosed).ToList();
        var items = PlanningService.CapacityItems(plan);
        // Apply the same legacy schedule fallback used by the issue chooser (session date, then a daily plan's header date).
        var resolved = ApprovedScheduledEntries().Where(e => e.Plan.Id == plan.Id).ToDictionary(e => e.Item.Id);
        for (var index = 0; index < Math.Min(items.Count, openPlanItems.Count); index++)
        {
            if (resolved.TryGetValue(openPlanItems[index].Id, out var entry))
            {
                items[index].ScheduledDate = entry.Item.ScheduledDate?.ToString("dd/MM/yyyy");
                items[index].SuggestedShiftId = entry.Shift;
                items[index].SuggestedLineId = entry.Line;
            }
        }
        // A legacy plan may explicitly leave its execution slot unspecified. Never invent a shift or line.
        var specified = items.Where(i => i.SuggestedShiftId != null && i.SuggestedLineId != null).ToList();
        if (specified.Count > 0)
        {
            var result = new PlanningCapacityEvaluator(Db).Evaluate(specified, plan.Id,
                plan.StartDate?.ToString("dd/MM/yyyy"), plan.EndDate?.ToString("dd/MM/yyyy"));
            if (!result.IsValid) throw new DomainException(result.Error + "\nعالج التعريف أو الجدولة في الخطة؛ كمية الأمر لا تُعدّل هنا.");
        }
        return new List<string>();
    }
}
