using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>§7 — تنفيذ وإقفال الإنتاج مع حراس §8 (لا تنفيذ على أمر مكتمل/غير معتمد، لا تجاوز للكميات).</summary>
public class ExecutionService : ServiceBase, IExecutionService
{
    public ExecutionService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering) { }

    // The simplified screen supplies NET actual raw consumption, never a gross issue estimate.
    internal bool RecordingActualDelivery { get; init; }

    // §حُذف StartExecution وCompleteExecution في B40: كانا مساراً موازياً لا تستدعيه أي شاشة.
    // المسار الفعلي: IProductionOrderService.StartOrder ثم IExecutionService.CloseProductionDay.
    // كانت 25 استدعاء اختبار تسلكهما — حُوّلت إلى المسار الحقيقي في B36.

    /// <summary>
    /// §نموذج إقفال الخطة اليومي — بديل جلسة التنفيذ:
    /// إنزال من أمر التشغيل: المفترض إنتاجه × كم خاماً استلمنا (صُرف) × كم أنتجنا × ماذا خرج
    /// (كراتين بوزنها + حشف + نوى + هالك بالكيلو) والمتبقي في صالة الإنتاج يُرحَّل اختيارياً
    /// لخطة اليوم التالي + التوقفات (كم ساعة ولماذا) + الإرسال للجودة (فحص بعد يومَي تبريد)
    /// ثم إقفال اليوم — وإن اكتملت الخطة تُقفل تلقائياً استعداداً لأمر جديد.
    /// </summary>
    public OpResult CloseProductionDay(int orderId, double producedKg, int producedCartons,
        double hashfKg, double nawaKg, double wastageKg, bool carryToNextDay,
        List<DowntimeDto> downtimes, bool sendToQuality, string notes = null,
        List<ByProductQtyDto> byProducts = null, double consumedRawKg = 0,
        List<CloseItemQtyDto> itemQtys = null,
        List<AuxActualDto> actualAux = null, double? emptyCartonsActual = null, int? cartonWarehouseId = null)
    {
        Require("execution", "Edit");
        ExecutionCloseTrace.Write($"CloseProductionDay ENTER OrderId={orderId} ProducedKg={producedKg} ProducedCartons={producedCartons}");
        var order = Db.ProductionOrders.Include(o => o.Items).ThenInclude(i => i.Allocations).FirstOrDefault(o => o.Id == orderId);
        if (order == null) return OpResult.Fail("أمر التشغيل غير موجود.");
        if (!order.IsApproved) return OpResult.Fail("لا يمكن إقفال يوم أمر غير معتمد.");
        if (order.IsClosed) return OpResult.Fail("هذا الأمر مقفل مسبقاً.");
        // إقفال جلسة سابقة لا يمنع استكمال أمر بقيت منه كمية مخططة؛ يمنع الأمر
        // نفسه الإقفال عند اكتماله عبر IsClosed، وتُفحص كل جلسة على المتبقي التراكمي أدناه.
        // §B85/H1: منع الازدواج — بنود أُقفلت عبر مسار «بنود الخطة» القديم (المحذوف في B95) لا يُقفل يوم أمرها أيضاً
        if (order.Items.Any(i => i.IsClosed))
            return OpResult.Fail("بنود هذا الأمر مقفلة مسبقاً عبر مسار قديم — لا يجوز إقفال يومه أيضاً (منعاً لازدواج الإنتاج والخام). راجع إدارة النظام لمعالجة البيانات القديمة.");

        return RunOp(() =>
        {
            // §1.50.66.2/4/7/10 — إعادة التحقق قبل الإقفال: الخطة ما زالت معتمدة، والكميات ضمن المسموح، والفصل بين المراحل
            // §6 — ربط التنفيذ الفعلي بالخطة والأمر والدفعة والمنتج والعميل
            if (order.SourcePlanId != null)
            {
                var planCheck = Db.ProductionPlans.AsNoTracking().FirstOrDefault(p => p.Id == order.SourcePlanId);
                if (planCheck == null) throw new DomainException("الخطة المرجعية غير موجودة — لا يمكن إقفال يوم الإنتاج.", "PLAN_MISSING");
                if (!planCheck.IsApproved || planCheck.Status != DocStatuses.Approved || planCheck.IsClosed)
                    throw new DomainException($"لا يمكن إقفال يوم الإنتاج: الخطة المرجعية {planCheck.DocumentNumber} ليست معتمدة حالياً (حالتها: {DocStatuses.ToArabic(planCheck.Status)}).", "PLAN_NOT_APPROVED");
            }
            // §1.50.66.7 — منع المنتَج > المخطط أو > المتاح
            double plannedTotalCheck = order.Items.Sum(i => i.PlannedQtyKg);
            double producedSoFarCheck = order.Items.Sum(i => i.ProducedQtyKg);
            if (producedSoFarCheck + producedKg > plannedTotalCheck + 0.001)
                throw new DomainException($"⛔ كمية الإنتاج الفعلية ({producedKg:N1} كجم) مع ما سبق ({producedSoFarCheck:N1}) تتجاوز المخطط الكلي ({plannedTotalCheck:N1} كجم).", "OVER_PLAN");
            // §1.50.66 — فصل واضح: Production → Quality → Finished Goods Available مع سجل المستخدم والتاريخ
            // هذه المرحلة Production فقط — لا تمنح رصيد بيع قبل Quality Gate
            // §8 — حراس الكميات
            if (producedKg < 0 || hashfKg < 0 || nawaKg < 0 || wastageKg < 0 || producedCartons < 0)
                throw new DomainException("الكميات لا يمكن أن تكون سالبة.");
            // §B95 — إقفال يوم بلا إنتاج مرفوض: يحمي من الإقفال الفارغ بالخطأ
            // (ورث دور اختبار «قائمة الإقفال الفارغة» من مسار بنود الخطة المحذوف)
            bool noOutput = producedKg <= 0 && producedCartons <= 0 && hashfKg <= 0 && nawaKg <= 0 && wastageKg <= 0
                && (byProducts == null || !byProducts.Any(b => b != null && b.QtyKg > 0))
                && (itemQtys == null || !itemQtys.Any(q => q != null && (q.ProducedKg > 0 || q.ProducedCartons > 0)));
            if (noOutput)
                throw new DomainException("⛔ لا يمكن إقفال يوم بلا إنتاج — أدخل الكمية المنتجة أو المخرجات الفعلية.");
            // §B88/M13: الإقفال متعدد الأصناف — كميات كل بند تُفحص بهوية صنفه وعبوته (كجم + كراتين) وتُكتب مباشرة
            bool perItem = itemQtys != null && itemQtys.Count > 0;
            Dictionary<int, (double kg, int boxes)> itemTake = null;
            if (perItem)
            {
                itemTake = new Dictionary<int, (double kg, int boxes)>();
                foreach (var q in itemQtys)
                {
                    if (q == null) continue;
                    if (q.ProducedKg < 0 || q.ProducedCartons < 0)
                        throw new DomainException("كميات البنود لا يمكن أن تكون سالبة.");
                    var oi0 = order.Items.FirstOrDefault(i => i.Id == q.OrderItemId)
                        ?? throw new DomainException("بند الإقفال غير تابع لهذا الأمر — حدّث الشاشة وأعد الإدخال.", "UNKNOWN_CLOSE_ITEM");
                    var prev = itemTake.TryGetValue(oi0.Id, out var pv) ? pv : (kg: 0.0, boxes: 0);
                    itemTake[oi0.Id] = (prev.kg + q.ProducedKg, prev.boxes + q.ProducedCartons);
                }
                foreach (var kv in itemTake)
                {
                    var oi = order.Items.First(i => i.Id == kv.Key);
                    string prodName = Db.Products.AsNoTracking().Where(x => x.Id == oi.ProductId).Select(x => x.ProductNameAr).FirstOrDefault() ?? $"صنف #{oi.ProductId}";
                    if (oi.ProducedQtyKg + kv.Value.kg > oi.PlannedQtyKg + 0.001)
                        throw new DomainException(
                            $"كمية الإنتاج للبند «{prodName}» أكبر من متبقيه.\nالمخطط: {oi.PlannedQtyKg:N1} كجم | المنتَج حتى الآن: {oi.ProducedQtyKg:N1} | المطلوب تسجيله: {kv.Value.kg:N1}",
                            "OVER_PRODUCTION");
                    if (oi.ProducedCartons + kv.Value.boxes > oi.PlannedCartons)
                        throw new DomainException(
                            $"كراتين الإنتاج للبند «{prodName}» أكبر من متبقيه.\nالمخطط: {oi.PlannedCartons:N0} | المنتَج حتى الآن: {oi.ProducedCartons:N0} | المطلوب تسجيله: {kv.Value.boxes:N0}",
                            "OVER_PRODUCTION");
                    // §لا تناقض بين الكراتين والوزن — لكل بند بهوية صنفه وعبوته (لا إجمالي أعمى على صنف واحد)
                    UnitsPolicy.RequireCartonWeight(Db, oi.ProductId, oi.PackagingTypeId, kv.Value.boxes,
                        $"إقفال يوم الإنتاج — بند «{prodName}»");
                    UnitsPolicy.RequireCartonKgMatch(Db, oi.ProductId, oi.PackagingTypeId,
                        kv.Value.kg, kv.Value.boxes, $"إقفال يوم الإنتاج — بند «{prodName}»");
                }
                producedKg = itemTake.Values.Sum(v => v.kg);
                producedCartons = itemTake.Values.Sum(v => v.boxes);
            }
            else
            {
                double producedSoFar = order.Items.Sum(i => i.ProducedQtyKg);
                double plannedTotal = order.Items.Sum(i => i.PlannedQtyKg);
                if (producedSoFar + producedKg > plannedTotal + 0.001)
                    throw new DomainException(
                        $"كمية الإنتاج أكبر من المسموح بها للأمر.\nالمخطط: {plannedTotal:N1} كجم | المنتَج حتى الآن: {producedSoFar:N1} | المطلوب تسجيله: {producedKg:N1}",
                        "OVER_PRODUCTION");

                // §المسار الإجمالي القديم (بلا تفصيل بنود — للتوافق): يُفحص الإجمالي على الصنف الأول.
                // الشاشة تمرر تفصيل البنود دائماً فيُفحص كل بند بهويته (M13).
                foreach (var oiChk in order.Items)
                {
                    UnitsPolicy.RequireCartonWeight(Db, oiChk.ProductId, oiChk.PackagingTypeId, producedCartons,
                        "إقفال يوم الإنتاج");
                    UnitsPolicy.EnsureCartonKgConsistency(Db, oiChk.ProductId, oiChk.PackagingTypeId,
                        producedKg, producedCartons, "إقفال يوم الإنتاج");
                    break;
                }
            }

            // §قاعدة توازن الإنتاج: الخام المستهلك هو ما يُدخله المستخدم فعلياً لهذه الجلسة،
            // وإن لم يُدخل يُشتق من حصة إنتاج الجلسة الحالية (لا كامل الأمر متعدد الأيام!) مع شمول بنود التخصيص المتعدد (LotId == null).
            double FullItemPlannedRaw(ProductionOrderItem item) => item.Allocations.Count > 0
                ? item.Allocations.Sum(a => a.AllocatedQtyKg)
                : item.PlannedQtyKg;

            var sessionItemKg = new Dictionary<int, double>();
            if (perItem && itemTake != null)
            {
                foreach (var oi in order.Items)
                    sessionItemKg[oi.Id] = itemTake.TryGetValue(oi.Id, out var tk) ? tk.kg : 0;
            }
            else
            {
                double totalRemItems = order.Items.Sum(oi => Math.Max(0, oi.PlannedQtyKg - oi.ProducedQtyKg));
                double remSim = producedKg;
                for (int idx = 0; idx < order.Items.Count; idx++)
                {
                    var oi = order.Items[idx];
                    double remItem = Math.Max(0, oi.PlannedQtyKg - oi.ProducedQtyKg);
                    double tk = totalRemItems > 0.001
                        ? (idx == order.Items.Count - 1 ? remSim : Math.Min(remItem, Math.Round(producedKg * (remItem / totalRemItems), 3)))
                        : 0;
                    tk = Math.Min(remSim, Math.Min(remItem, Math.Max(0, tk)));
                    sessionItemKg[oi.Id] = tk;
                    remSim = Math.Max(0, remSim - tk);
                }
            }

            double secondarySessionKg = (byProducts != null && byProducts.Count > 0
                ? byProducts.Where(b => b.QtyKg > 0).Sum(b => b.QtyKg)
                : Math.Max(0, hashfKg) + Math.Max(0, nawaKg)) + Math.Max(0, wastageKg);
            double totalSessionPlanned = order.Items.Sum(x => Math.Max(0, x.PlannedQtyKg));
            double SessionPlannedRawFor(ProductionOrderItem item)
            {
                double fullRaw = FullItemPlannedRaw(item);
                if (fullRaw <= 0) return 0;
                double sKg = sessionItemKg.TryGetValue(item.Id, out var v) ? v : 0;
                double secShare = totalSessionPlanned > 0.001
                    ? secondarySessionKg * (item.PlannedQtyKg / totalSessionPlanned)
                    : 0;
                if (item.PlannedQtyKg > 0.001)
                {
                    if (producedKg > 0.001 || secondarySessionKg > 0.001)
                        return fullRaw * Math.Min(1.0, (sKg + secShare) / item.PlannedQtyKg);
                    // حالة جلسة بمخرجات ثانوية/فاقد فقط دون كراتين تامة: نوزع نسبياً على البنود المفتوحة
                    double openPlanned = order.Items.Where(x => !x.IsClosed).Sum(x => x.PlannedQtyKg);
                    return openPlanned > 0.001 ? fullRaw * (item.PlannedQtyKg / openPlanned) : 0;
                }
                return 0;
            }

            var rawEligibleItems = order.Items.Where(i => i.LotId != null || i.Allocations.Count > 0).ToList();
            double sessionPlannedRaw = rawEligibleItems.Sum(SessionPlannedRawFor);
            if (sessionPlannedRaw <= 0.001)
                sessionPlannedRaw = rawEligibleItems.Sum(FullItemPlannedRaw);
            double consumed = consumedRawKg > 0 ? consumedRawKg : sessionPlannedRaw;

            // صرف الخام فعلياً من الدفعات والمخازن المخصصة (شاملاً البنود ذات التخصيص المتعدد LotId == null)
            var whRawForClose = WarehouseId("WRM");
            var takeByLot = new Dictionary<int, double>();
            var takeByWarehouseLot = new Dictionary<(int WarehouseId, int LotId), double>();
            var custByLot = new Dictionary<int, int?>();
            foreach (var oi in rawEligibleItems)
            {
                double itemWeight = SessionPlannedRawFor(oi);
                if (itemWeight <= 0.0001 && rawEligibleItems.All(x => SessionPlannedRawFor(x) <= 0.0001))
                    itemWeight = FullItemPlannedRaw(oi);
                double share = sessionPlannedRaw > 0.0001 ? itemWeight / sessionPlannedRaw : 0;
                double take = Math.Round(consumed * share, 3);
                if (take <= 0.0001) continue;

                if (oi.Allocations.Count > 0)
                {
                    // نفضل التخصيصات ذات الرصيد المتبقي أولاً حتى لا نسحب من دفعة استُنفدت في جلسة سابقة
                    var activeAllocs = oi.Allocations.Where(a => a.AllocatedQtyKg > a.ConsumedQtyKg + 0.0001).ToList();
                    if (activeAllocs.Count == 0) activeAllocs = oi.Allocations.Where(a => a.AllocatedQtyKg > 0).ToList();
                    double allocationTotal = activeAllocs.Sum(a =>
                        a.AllocatedQtyKg > a.ConsumedQtyKg + 0.0001 ? (a.AllocatedQtyKg - a.ConsumedQtyKg) : a.AllocatedQtyKg);
                    foreach (var allocation in activeAllocs)
                    {
                        double basis = allocation.AllocatedQtyKg > allocation.ConsumedQtyKg + 0.0001
                            ? (allocation.AllocatedQtyKg - allocation.ConsumedQtyKg)
                            : allocation.AllocatedQtyKg;
                        double allocatedTake = allocationTotal > 0 ? Math.Round(take * basis / allocationTotal, 3) : 0;
                        if (allocatedTake <= 0.0001) continue;
                        takeByLot[allocation.LotId] = (takeByLot.TryGetValue(allocation.LotId, out var tv) ? tv : 0) + allocatedTake;
                        if (!custByLot.ContainsKey(allocation.LotId))
                        {
                            var lotOwner = Db.Lots.AsNoTracking().Where(l => l.Id == allocation.LotId).Select(l => l.CustomerId).FirstOrDefault();
                            custByLot[allocation.LotId] = lotOwner ?? oi.CustomerId ?? order.CustomerId;
                        }
                        var key = (allocation.WarehouseId, allocation.LotId);
                        takeByWarehouseLot[key] = (takeByWarehouseLot.TryGetValue(key, out var av) ? av : 0) + allocatedTake;
                    }
                }
                else if (oi.LotId != null)
                {
                    int lid = oi.LotId.Value;
                    takeByLot[lid] = (takeByLot.TryGetValue(lid, out var tv) ? tv : 0) + take;
                    if (!custByLot.ContainsKey(lid))
                    {
                        var lotOwner = Db.Lots.AsNoTracking().Where(l => l.Id == lid).Select(l => l.CustomerId).FirstOrDefault();
                        custByLot[lid] = lotOwner ?? oi.CustomerId ?? order.CustomerId;
                    }
                    var sourceLot = Db.Lots.AsNoTracking().Single(l => l.Id == lid);
                    var sourceWarehouse = Db.Shipments.AsNoTracking()
                        .Where(s => s.Id == sourceLot.ShipmentId)
                        .Select(s => s.ReceivingWarehouseId).FirstOrDefault() ?? whRawForClose;
                    var key = (sourceWarehouse, lid);
                    takeByWarehouseLot[key] = (takeByWarehouseLot.TryGetValue(key, out var av) ? av : 0) + take;
                }
            }
            if (takeByLot.Count > 0 && consumed > 0)
            {
                int lastLot = takeByLot.Keys.Last();
                double totalRounded = takeByLot.Values.Sum();
                if (Math.Abs(consumed - totalRounded) > 0.0001)
                    takeByLot[lastLot] = Math.Max(0, Math.Round(takeByLot[lastLot] + (consumed - totalRounded), 3));
            }
            foreach (var lotGroup in takeByWarehouseLot.GroupBy(x => x.Key.LotId).ToList())
            {
                var target = takeByLot.TryGetValue(lotGroup.Key, out var t) ? t : 0;
                var diff = target - lotGroup.Sum(x => x.Value);
                if (Math.Abs(diff) > 0.0001)
                {
                    var lastKey = lotGroup.Last().Key;
                    takeByWarehouseLot[lastKey] = Math.Max(0, Math.Round(takeByWarehouseLot[lastKey] + diff, 3));
                }
            }
            // جلسة الإقفال: نحدد التنفيذ المفتوح المرتبط بهذا الأمر قبل ربط حركة صرف الخام به.
            // لا نعتمد على Status وحده؛ فقد بقيت بعض السجلات القديمة بحالة Completed
            // مع IsDayClosed=false، وكان ذلك ينشئ سجلاً ثانياً بدلاً من إكمال السجل الصحيح.
            var openExecutions = Db.ProductionExecutions
                .Include(x => x.Downtimes).Include(x => x.ByProducts)
                .Where(e => e.OrderId == orderId && !e.IsDayClosed)
                .OrderByDescending(e => e.Id).ToList();
            ExecutionCloseTrace.Write($"CloseProductionDay BEFORE_SELECT OrderId={orderId} OpenExecutions=[{string.Join(" | ", openExecutions.Select(e => $"Id={e.Id},OrderId={e.OrderId},IsDayClosed={e.IsDayClosed},Status={e.Status},EndDateTime={e.EndDateTime:O}"))}]");
            if (openExecutions.Count > 1)
                throw new DomainException("يوجد أكثر من سجل تنفيذ مفتوح لنفس أمر الإنتاج — لا يمكن تحديد جلسة واحدة للإقفال. راجع سجل التنفيذ قبل المتابعة.", "MULTIPLE_OPEN_EXECUTIONS");
            var exe = openExecutions.SingleOrDefault();
            bool isNew = exe == null;
            if (isNew)
            {
                exe = new ProductionExecution
                {
                    DocumentNumber = Numbering.Next("EXE"),
                    OrderId = orderId,
                    LineId = order.LineId,
                    ShiftId = order.ShiftId,
                    StartDateTime = DateTime.Now,
                    CreatedBy = Session?.UserId,
                    CreatedDate = DateTime.Now
                };
            }
            // §B88: حركة صرف واحدة لكل دفعة — بنود الدفعة الواحدة كانت تنشر حركات مكررة بنفس المرجع (DUPLICATE)
            // إذا لم يرسل النموذج وزناً يدوياً، يستعمل النظام تقدير الخام من التخصيصات ونسبة مخرجات الجلسة.
            string rawTag = consumedRawKg > 0 ? "" : " (تقدير آلي من تخصيصات الخام ومخرجات الجلسة)";
            foreach (var kv in takeByWarehouseLot.Where(x => x.Value > 0.001))
            {
                var sourceLot = Db.Lots.FirstOrDefault(l => l.Id == kv.Key.LotId)
                    ?? throw new DomainException("دفعة الخام غير موجودة.");
                ConsumeLot(sourceLot.Id, kv.Value, "إقفال يوم الإنتاج");
                PostStockMovement(kv.Key.WarehouseId, MovementType.Outbound, kv.Value, 0,
                    ReferenceDocType.ProductionExecution, exe.DocumentNumber,
                    productId: sourceLot.ProductId,
                    lotId: sourceLot.Id, customerId: custByLot.TryGetValue(sourceLot.Id, out var cc) ? cc : order.CustomerId,
                    orderId: order.Id, packagingTypeId: sourceLot.PackagingTypeId,
                    notes: $"صرف خام فعلي عند إقفال يوم الإنتاج من المخزن {kv.Key.WarehouseId}" + rawTag);
            }
            // تسجيل الاستهلاك على تخصيص الخطة نفسه حتى يتحرر المتبقي من نفس المخزن/الدفعة.
            var allocationTakeRemaining = takeByWarehouseLot.ToDictionary(x => x.Key, x => x.Value);
            foreach (var oi in order.Items)
                foreach (var allocation in oi.Allocations.Where(a => a.AllocatedQtyKg > 0))
                {
                    var key = (allocation.WarehouseId, allocation.LotId);
                    if (!allocationTakeRemaining.TryGetValue(key, out var taken) || taken <= 0) continue;
                    var planAllocation = allocation.PlanAllocationId is int aid
                        ? Db.ProductionPlanItemAllocations.FirstOrDefault(a => a.Id == aid)
                        : null;
                    if (planAllocation == null) continue;
                    var remainingAllocation = Math.Max(0, planAllocation.AllocatedQtyKg - planAllocation.ConsumedQtyKg - planAllocation.ReleasedQtyKg);
                    var consumedAllocation = Math.Min(remainingAllocation, taken);
                    allocationTakeRemaining[key] = Math.Max(0, taken - consumedAllocation);
                    allocation.ConsumedQtyKg += consumedAllocation;
                    planAllocation.ConsumedQtyKg += consumedAllocation;
                    planAllocation.Status = planAllocation.ConsumedQtyKg + planAllocation.ReleasedQtyKg >= planAllocation.AllocatedQtyKg - 0.001
                        ? "Consumed" : "PartiallyConsumed";
                }
            // §المخرجات الثانوية: القائمة الديناميكية هي المرجع إن وُجدت. وإن غابت يُعتمد على
            // العمودين القديمين — ولا يُجمعان معاً أبداً، وإلا عُدّ المخرج مرتين
            // (CloseDayDialog يملأ الاثنين معاً مطابقةً بالاسم للبيانات السابقة).
            double byTotal = byProducts?.Where(b => b != null && b.QtyKg > 0).Sum(b => b.QtyKg) ?? 0;
            if (RecordingActualDelivery)
            {
                // Non-weight units remain recorded in their configured unit; never count pieces/litres as kg.
                var kgDefinitions = Db.ByProducts.AsNoTracking().ToList().Where(b =>
                    (b.UnitOfMeasure ?? "").Trim().ToLowerInvariant() is "كجم" or "كغ" or "kg" or "كيلوجرام").Select(b => b.Id).ToHashSet();
                byTotal = byProducts?.Where(b => b != null && kgDefinitions.Contains(b.ByProductId)).Sum(b => b.QtyKg) ?? 0;
            }
            double secondary = byTotal > 0 ? byTotal : hashfKg + nawaKg;
            double outputs = producedKg + secondary + wastageKg;
            // §قاعدة توازن الإنتاج: لا معادلة ثابتة ولا رفض.
            // في تصنيع التمور يزيد وزن الخارج عن الداخل لإضافة الماء أثناء التشغيل،
            // والماء لا يُسجَّل صنفاً ولا مدخلاً مستقلاً. فالنظام يقبل الكميات الفعلية
            // ويحسب الفرق ويعرضه في «تقرير توازن الإنتاج» إجراءً رقابياً — لا يمنع العملية.
            double remainingInHall = (RecordingActualDelivery || carryToNextDay) ? 0
                : Math.Round(consumed > outputs ? consumed - outputs : 0, 3);
            // §1.50.66.9 — لا يُمنح رصيد بيع (WFG) في هذه المرحلة؛ الإنتاج غير صالح للبيع عند SaveActualProduction
            // المسار: Production → Quality Gate (IsApproved) → Finished Goods Available — فصل صارم

            // احتساب التصافي الفعلي للجلسة مباشرة من المخرجات الفعلية والخام المستهلك دون معامل بطاقة الصنف
            string yieldMsg = "";
            if (consumed > 0.001 && producedKg > 0.001)
            {
                double actualYieldPct = Math.Round(producedKg / consumed * 100.0, 1);
                double balanceDiff = outputs - consumed;
                string diffNote = Math.Abs(balanceDiff) <= 0.05
                    ? "متوازن تماماً"
                    : balanceDiff > 0
                        ? $"زيادة مخرجات +{balanceDiff:N1} كجم (رطوبة/غسيل التشغيل)"
                        : $"فرق تشغيل {balanceDiff:N1} كجم";
                yieldMsg = $"\n📊 التصافي الفعلي للجلسة: {actualYieldPct:0.0}% (إنتاج تام {producedKg:N1} كجم من خام مستهلك {consumed:N1} كجم — {diffNote}).";
            }
            else if (outputs > consumed + 0.001)
            {
                yieldMsg = $"\n💧 المخرجات تزيد عن الخام المستهلك بـ {outputs - consumed:N1} كجم (رطوبة/غسيل التشغيل).";
            }

            // §1.50.66.6 — ربط التنفيذ بالخطة والأمر والدفعة والمنتج والعميل + سجل المستخدم والتاريخ
            // OrderId يربط بـ SourcePlanId و CustomerId و Items (LotId/ProductId) — لا حاجة لحقول إضافية، لكن نوثق المستخدم والتاريخ
            exe.EndDateTime = DateTime.Now;
            exe.ModifiedBy = Session?.UserId;
            exe.ModifiedDate = DateTime.Now;
            exe.Status = DocStatuses.Completed;
            exe.ActualQtyKg = producedKg;
            exe.ActualCartons = producedCartons;
            exe.WastageQtyKg = wastageKg;
            exe.HashfKg = hashfKg;
            exe.NawaKg = nawaKg;
            exe.ConsumedRawKg = consumed;
            exe.RemainingInHallKg = remainingInHall;
            exe.CarryToNextDay = carryToNextDay && remainingInHall > 0;
            exe.QualitySent = sendToQuality;
            exe.ExpectedQualityDate = sendToQuality ? DateTime.Today.AddDays(2) : null;
            exe.IsDayClosed = true;
            exe.ClosingNotes = notes;
            ExecutionCloseTrace.Write($"CloseProductionDay MARKED OrderId={orderId} ExecutionId={exe.Id} IsDayClosed={exe.IsDayClosed} Status={exe.Status} EndDateTime={exe.EndDateTime:O}");

            // §المخرجات الثانوية بأصنافها المعرَّفة (لا «حشف/نوى» مفروضة)
            if (byProducts != null)
                foreach (var bp in byProducts)
                {
                    if (bp == null || bp.QtyKg <= 0) continue;
                    if (!Db.ByProducts.Any(b => b.Id == bp.ByProductId && b.IsActive))
                        throw new DomainException("المخرج الثانوي غير موجود في بطاقته أو موقوف — عرّفه من إعدادات الأصناف.");
                    exe.ByProducts.Add(new ExecutionByProduct
                    {
                        ByProductId = bp.ByProductId, Qty = (decimal)bp.QtyKg,
                        SourceOrderItemId = bp.SourceOrderItemId, RawProductId = bp.RawProductId,
                        RawLotId = bp.RawLotId, CustomerId = bp.CustomerId
                    });
                }

            // §التوقفات: كم ساعة ولماذا
            if (downtimes != null)
                foreach (var dt in downtimes)
                    if (dt != null && dt.Hours > 0 && !string.IsNullOrWhiteSpace(dt.ReasonAr))
                        exe.Downtimes.Add(new ExecutionDowntime { Hours = dt.Hours, ReasonAr = dt.ReasonAr.Trim(), StartTime = dt.StartTime, EndTime = dt.EndTime });

            if (perItem)
            {
                // §B88/M13: كتابة مباشرة لكل بند بكميته المفحوصة — لا توزيع إجمالي أعمى بالترتيب
                foreach (var kv in itemTake!)
                {
                    var item = order.Items.First(i => i.Id == kv.Key);
                    item.ProducedQtyKg += kv.Value.kg;
                    item.ProducedCartons += kv.Value.boxes;
                }
            }
            else
            {
                // توزيع المنتَج على بنود الأمر بالترتيب (المسار الإجمالي القديم)
                double remaining = producedKg;
                foreach (var item in order.Items.Where(i => i.ProducedQtyKg < i.PlannedQtyKg))
                {
                    double take = Math.Min(remaining, item.PlannedQtyKg - item.ProducedQtyKg);
                    if (take <= 0) continue;
                    item.ProducedQtyKg += take;
                    remaining -= take;
                    if (remaining <= 0.001) break;
                }
                // §B86/H7: توزيع الكراتين على البنود بالترتيب وبحدود مخطط كل بند — كانت لا تُكتب أبداً فانكسر تتبع الكراتين
                int remainingBoxes = producedCartons;
                foreach (var item in order.Items.Where(i => i.ProducedCartons < i.PlannedCartons))
                {
                    int boxTake = Math.Min(remainingBoxes, item.PlannedCartons - item.ProducedCartons);
                    if (boxTake <= 0) continue;
                    item.ProducedCartons += boxTake;
                    remainingBoxes -= boxTake;
                    if (remainingBoxes <= 0) break;
                }
            }
            // §حالة الأمر: يُغلق الأمر فقط بعد اكتمال جميع بنوده؛ إقفال يوم جزئي يبقى قابلاً للاستكمال.
            if (order.Items.All(i => i.IsClosed || i.ProducedQtyKg + 0.001 >= i.PlannedQtyKg))
            {
                order.Status = DocStatuses.Completed;
                order.IsClosed = true;
                order.ClosedDate = DateTime.Now;
                foreach (var item in order.Items)
                {
                    item.IsClosed = true;
                    item.Status = DocStatuses.Completed;
                }
            }

            if (isNew) Db.ProductionExecutions.Add(exe);
            // حفظ مبكر مقصود: يثبت سجل التنفيذ قبل متابعة الترحيلات اللاحقة،
            // مع بقائه داخل نفس المعاملة الذرية حتى يُلغى كله إذا فشل أي حارس لاحق.
            Db.SaveChanges();
            var persistedExecution = Db.ProductionExecutions.AsNoTracking()
                .FirstOrDefault(e => e.Id == exe.Id && e.OrderId == orderId);
            ExecutionCloseTrace.Write($"CloseProductionDay AFTER_SAVE OrderId={orderId} ExecutionId={persistedExecution?.Id.ToString() ?? "<null>"} IsDayClosed={persistedExecution?.IsDayClosed.ToString() ?? "<null>"} Status={persistedExecution?.Status ?? "<null>"} EndDateTime={persistedExecution?.EndDateTime?.ToString("O") ?? "<null>"}");
            if (persistedExecution == null || !persistedExecution.IsDayClosed
                || persistedExecution.Status != DocStatuses.Completed || persistedExecution.EndDateTime == null)
                throw new DomainException(
                    "تعذر تثبيت إقفال يوم الإنتاج في سجل التنفيذ — لم تُحفظ العملية.",
                    "EXECUTION_CLOSE_NOT_PERSISTED");

            // §المسار الموحد يرجع المتبقي من الخام إلى نفس المخزن والدفعة التي سُحب منها (وليس WRM دائماً!)
            // بحركة مرتجع موثقة عند عدم اختيار الترحيل لليوم التالي (carryToNextDay == false).
            if (remainingInHall > 0.001)
            {
                double totalTake = takeByWarehouseLot.Values.Sum();
                double backAssigned = 0;
                var backEntries = takeByWarehouseLot.Where(x => x.Value > 0.001).ToList();
                for (int li = 0; li < backEntries.Count; li++)
                {
                    var entry = backEntries[li];
                    var lotBack = Db.Lots.FirstOrDefault(l => l.Id == entry.Key.LotId);
                    if (lotBack == null) continue;
                    bool lastBack = li == backEntries.Count - 1;
                    double back = lastBack
                        ? Math.Round(remainingInHall - backAssigned, 3)
                        : Math.Round(remainingInHall * (totalTake > 0 ? entry.Value / totalTake : 0), 3);
                    if (back <= 0.001) continue;
                    backAssigned += back;
                    lotBack.InStockQtyKg += back;
                    if (lotBack.ProducedQtyKg >= back) lotBack.ProducedQtyKg -= back;
                    PostStockMovement(entry.Key.WarehouseId, MovementType.Inbound, back, 0,
                        ReferenceDocType.Return, $"{exe.DocumentNumber}#RET-{li + 1}",
                        productId: lotBack.ProductId, lotId: lotBack.Id,
                        customerId: custByLot.TryGetValue(lotBack.Id, out var cLot) ? cLot : lotBack.CustomerId,
                        orderId: order.Id, packagingTypeId: lotBack.PackagingTypeId,
                        notes: $"مرتجع متبقي إقفال يوم الإنتاج إلى المخزن {entry.Key.WarehouseId} — الدفعة ({lotBack.LotCode})");

                    // إعادة الجزء غير المستهلك إلى تخصيص الخطة/الأمر المرتبط بنفس المخزن والدفعة
                    double restoreRem = back;
                    foreach (var oi in order.Items)
                    foreach (var alloc in oi.Allocations.Where(a => a.WarehouseId == entry.Key.WarehouseId && a.LotId == entry.Key.LotId && a.ConsumedQtyKg > 0.001))
                    {
                        if (restoreRem <= 0.001) break;
                        double giveBack = Math.Min(alloc.ConsumedQtyKg, restoreRem);
                        alloc.ConsumedQtyKg -= giveBack;
                        if (alloc.PlanAllocationId is int paid)
                        {
                            var pa = Db.ProductionPlanItemAllocations.FirstOrDefault(x => x.Id == paid);
                            if (pa != null)
                            {
                                pa.ConsumedQtyKg = Math.Max(0, pa.ConsumedQtyKg - giveBack);
                                pa.Status = pa.ConsumedQtyKg > 0.001 ? "PartiallyConsumed" : "Reserved";
                            }
                        }
                        restoreRem -= giveBack;
                    }
                }
            }

            // ═══════════════════════════════════════════════════════════════════
            // §B95 — تسوية المواد المساعدة مقابل الفعلي (النظام القديم والجديد معاً وبوحدة القياس الصحيحة):
            // - لا نُنشئ أبداً StockBalance شبحي بـ MaterialId=0, ProductId=null!
            // - عند الإقفال الجزئي (الأمر ما زال مفتوحاً لم يكتمل)، نُسجل الاستهلاك التراكمي فقط ولا نُرجع
            //   مواد الأيام القادمة قسراً إلى المخزن إلا بعد اكتمال الأمر أو إدخال استهلاك نهائي صريح!
            // ═══════════════════════════════════════════════════════════════════
            Db.Entry(order).Collection(o => o.Materials).Load();
            var ordSvc = new ProductionOrderService(Db, Session, Numbering);
            var whAux = WarehouseId("WAUX");
            int auxTxnSeq = 0;
            foreach (var mat in order.Materials.Where(m => m.ActualIssuedQty > 0).ToList())
            {
                bool modernAux = mat.AuxiliaryProductId is int auxId && auxId > 0;
                if (!modernAux && mat.MaterialId <= 0) continue;
                int? stockProductId = modernAux ? mat.AuxiliaryProductId : null;
                int? stockMaterialId = modernAux ? null : mat.MaterialId;
                bool isKgUnit = (mat.UnitOfMeasure ?? "").Contains("كجم")
                    || (mat.UnitOfMeasure ?? "").Equals("kg", StringComparison.OrdinalIgnoreCase);

                double consumedAux;
                bool manualCompleted = mat.Status == DocStatuses.Completed && mat.ConsumedQty > 0;
                if (manualCompleted)
                {
                    consumedAux = mat.ConsumedQty;
                }
                else
                {
                    consumedAux = 0;
                    foreach (var oi in order.Items)
                    {
                        if (oi.ProducedCartons <= 0 && oi.ProducedQtyKg <= 0.001) continue;
                        int? itemCust = oi.CustomerId ?? order.CustomerId;
                        var bomReqs = Db.ProductAuxiliaryRequirements.AsNoTracking()
                            .Where(r => r.FinishedProductId == oi.ProductId && r.IsActive).ToList();
                        if (modernAux && bomReqs.Count > 0)
                        {
                            foreach (var r in bomReqs)
                            {
                                int effId = ordSvc.ResolveAuxProductForCustomer(r.AuxiliaryProductId, itemCust, oi.ProductId, oi.PackagingTypeId);
                                if (effId != mat.AuxiliaryProductId!.Value) continue;
                                double q = r.CalculationMethod == "PerKg"
                                    ? oi.ProducedQtyKg * (double)r.QtyPerCarton
                                    : oi.ProducedCartons * (double)r.QtyPerCarton;
                                consumedAux += q;
                            }
                        }
                        else if (!modernAux && bomReqs.Count == 0)
                        {
                            var fms = Db.ConsumptionFormulas.AsNoTracking()
                                .Where(f => f.ProductId == oi.ProductId && f.IsActive
                                    && f.Mode == "PerCarton" && (f.CustomerId == null || f.CustomerId == itemCust)).ToList();
                            foreach (var f in fms)
                                if (ordSvc.ResolveAuxMaterial(f, itemCust) == mat.MaterialId)
                                    consumedAux += f.QtyPerUnit * oi.ProducedCartons;
                        }
                    }
                    if (actualAux != null)
                        consumedAux += actualAux.Where(a => a.OrderId == order.Id
                            && ((modernAux && a.MaterialId == mat.AuxiliaryProductId) || (!modernAux && a.MaterialId == mat.MaterialId)))
                            .Sum(a => a.Qty);
                    mat.ConsumedQty = Math.Round(consumedAux, 3);
                }

                double diff = mat.ActualIssuedQty - mat.ReturnedQty - consumedAux - mat.WastedQty;
                if (diff > 0.001)
                {
                    var bal = Db.StockBalances.Local.FirstOrDefault(s => s.WarehouseId == whAux && s.ProductId == stockProductId && s.MaterialId == stockMaterialId && s.LotId == null && s.CustomerId == null && s.PackagingTypeId == null)
                        ?? Db.StockBalances.FirstOrDefault(s => s.WarehouseId == whAux && s.ProductId == stockProductId && s.MaterialId == stockMaterialId && s.LotId == null && s.CustomerId == null && s.PackagingTypeId == null);
                    bool usePkg = modernAux && !isKgUnit && !(bal != null && bal.PackageCount == 0 && bal.QtyKg > 0.001);
                    double retKg = usePkg ? 0 : diff;
                    int retPkg = usePkg ? (int)Math.Round(diff) : 0;
                    if (retKg > 0.001 || retPkg > 0)
                    {
                        auxTxnSeq++;
                        PostStockMovement(whAux, MovementType.Inbound, retKg, retPkg,
                            ReferenceDocType.MaterialReturn, $"{exe.DocumentNumber}#AUXRET{auxTxnSeq}",
                            productId: stockProductId, materialId: stockMaterialId, orderId: order.Id,
                            notes: $"مرتجع آلي عند إقفال يوم الإنتاج: مصروف {mat.ActualIssuedQty:N1} − مستهلك {consumedAux:N1}");
                        mat.ReturnedQty += usePkg ? retPkg : diff;
                    }
                }
                else if (diff < -0.001)
                {
                    double extraNeed = -diff;
                    var bal2 = Db.StockBalances.Local.FirstOrDefault(s => s.WarehouseId == whAux && s.ProductId == stockProductId && s.MaterialId == stockMaterialId && s.LotId == null && s.CustomerId == null && s.PackagingTypeId == null)
                        ?? Db.StockBalances.FirstOrDefault(s => s.WarehouseId == whAux && s.ProductId == stockProductId && s.MaterialId == stockMaterialId && s.LotId == null && s.CustomerId == null && s.PackagingTypeId == null);
                    bool usePkg = modernAux && !isKgUnit && !(bal2 != null && bal2.PackageCount == 0 && bal2.QtyKg > 0.001);
                    double bal2Before = bal2 == null ? 0 : (usePkg ? bal2.PackageCount : bal2.QtyKg);
                    double reqCheck = usePkg ? Math.Round(extraNeed) : extraNeed;
                    if (bal2Before < reqCheck - 0.001)
                    {
                        string matName2 = modernAux
                            ? (Db.Products.AsNoTracking().Where(p => p.Id == stockProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? $"#{stockProductId}")
                            : (Db.AuxiliaryMaterials.AsNoTracking().Where(m => m.Id == mat.MaterialId).Select(m => m.MaterialNameAr).FirstOrDefault() ?? $"#{mat.MaterialId}");
                        throw new DomainException(
                            $"⛔ تعذر إقفال اليوم: رصيد الصنف المساعد «{matName2}» في مخزن المساعدة ({bal2Before:N1}) لا يغطي الفارق التكميلي ({reqCheck:N1}).\nالمستهلك {consumedAux:N1} أكبر من المصروف {mat.ActualIssuedQty:N1} — قيّد صرفاً/تسوية تغطي الفرق أولاً ثم أقفل اليوم.",
                            "AUX_NEGATIVE_BALANCE");
                    }
                    double outKg = usePkg ? 0 : extraNeed;
                    int outPkg = usePkg ? (int)Math.Round(extraNeed) : 0;
                    if (outKg > 0.001 || outPkg > 0)
                    {
                        auxTxnSeq++;
                        PostStockMovement(whAux, MovementType.Outbound, outKg, outPkg,
                            ReferenceDocType.MaterialIssue, $"{exe.DocumentNumber}#AUXISS{auxTxnSeq}",
                            productId: stockProductId, materialId: stockMaterialId, orderId: order.Id,
                            notes: $"صرف تكميلي آلي عند إقفال يوم الإنتاج: مستهلك {consumedAux:N1} − مصروف {mat.ActualIssuedQty:N1}");
                        mat.ActualIssuedQty += usePkg ? outPkg : extraNeed;
                    }
                }
            }
            // §مواد الإدخال الفعلي غير المصروفة عند الاعتماد (ديزل/وقود): تُخصم عبر PostStockMovement بهوية صالحة فقط
            if (actualAux != null)
                foreach (var aa in actualAux.Where(a => a.OrderId == order.Id && a.Qty > 0 && a.MaterialId > 0
                    && !order.Materials.Any(m => m.MaterialId == a.MaterialId || m.AuxiliaryProductId == a.MaterialId)))
                {
                    bool isLegacyMat = Db.AuxiliaryMaterials.AsNoTracking().Any(m => m.Id == aa.MaterialId);
                    int? aaProdId = isLegacyMat ? null : aa.MaterialId;
                    int? aaMatId = isLegacyMat ? aa.MaterialId : null;
                    var bb = Db.StockBalances.Local.FirstOrDefault(s => s.WarehouseId == whAux && s.ProductId == aaProdId && s.MaterialId == aaMatId && s.LotId == null && s.CustomerId == null && s.PackagingTypeId == null)
                        ?? Db.StockBalances.FirstOrDefault(s => s.WarehouseId == whAux && s.ProductId == aaProdId && s.MaterialId == aaMatId && s.LotId == null && s.CustomerId == null && s.PackagingTypeId == null);
                    double bbBefore = bb?.QtyKg ?? 0;
                    if (bbBefore - aa.Qty < -0.001)
                    {
                        string aaName = isLegacyMat
                            ? (Db.AuxiliaryMaterials.AsNoTracking().Where(m => m.Id == aa.MaterialId).Select(m => m.MaterialNameAr).FirstOrDefault() ?? $"#{aa.MaterialId}")
                            : (Db.Products.AsNoTracking().Where(p => p.Id == aa.MaterialId).Select(p => p.ProductNameAr).FirstOrDefault() ?? $"#{aa.MaterialId}");
                        throw new DomainException(
                            $"⛔ تعذر إقفال اليوم: رصيد الصنف المساعد «{aaName}» في مخزن المساعدة ({bbBefore:N1}) لا يغطي الإدخال الفعلي ({aa.Qty:N1}).\nقيّد صرفه مسبقاً (أو تسوية) ثم أقفل اليوم.",
                            "AUX_NEGATIVE_BALANCE");
                    }
                    auxTxnSeq++;
                    PostStockMovement(whAux, MovementType.Outbound, aa.Qty, 0,
                        ReferenceDocType.MaterialIssue, $"{exe.DocumentNumber}#AUXACT{auxTxnSeq}",
                        productId: aaProdId, materialId: aaMatId, orderId: order.Id,
                        notes: "صرف فعلي غير مصروف مسبقاً عند إقفال يوم الإنتاج");
                }
            Db.SaveChanges();

            // ═══════════════════════════════════════════════════════════════════
            // §B95 — توريد الكرتون الفارغ الناتج عن تفريغ الخام (مُرحَّل من المسار المحذوف):
            // الفعلي المؤكد إن أُدخل وإلا فتقدير النظام من الخام المصروف لكل نوع تعبئة.
            // ═══════════════════════════════════════════════════════════════════
            var cartonSvc = new CartonService(Db, Session, Numbering);
            const int NoPack = int.MinValue;
            var estByPack = new Dictionary<int, double>();
            foreach (var kvLot in takeByLot)
            {
                int packKey = Db.Lots.AsNoTracking().Where(l => l.Id == kvLot.Key).Select(l => l.PackagingTypeId).FirstOrDefault() ?? NoPack;
                int lotProd = Db.Lots.AsNoTracking().Where(l => l.Id == kvLot.Key).Select(l => l.ProductId).FirstOrDefault();
                double w = CartonService.RawCartonWeight(Db, kvLot.Key, lotProd);
                estByPack[packKey] = (estByPack.TryGetValue(packKey, out var pv) ? pv : 0) + (w > 0 ? kvLot.Value / w : 0);
            }
            if (emptyCartonsActual != null)
            {
                int domKey = estByPack.Count > 0
                    ? estByPack.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault()
                    : NoPack;
                cartonSvc.PostEmptyCartons(emptyCartonsActual.Value, exe.DocumentNumber, cartonWarehouseId, domKey == NoPack ? null : domKey);
            }
            else
                foreach (var kv in estByPack)
                    cartonSvc.PostEmptyCartons(Math.Round(kv.Value), exe.DocumentNumber, cartonWarehouseId, kv.Key == NoPack ? null : kv.Key);

            PlanSync.SyncProduced(Db, order.Id);
            // §B103 — علة مُصلَحة: إعادة احتساب الحجز أدناه تقرأ من القاعدة باستعلام SQL،
            // وقبل هذا الحفظ كانت ترى المنتَج صفراً فيبقى الحجز كاملاً بعد الإقفال الجزئي.
            Db.SaveChanges();

            // §B85/H1: تحديث الحجز المخزن بعد الإنتاج — كان يبقى مرتفعاً حتى الإقفال النهائي فيُخفي المتاح عن الخطط
            if (order.SourcePlanId is int srcPlanId)
            {
                var srcPlan = Db.ProductionPlans.Include(p => p.Items).FirstOrDefault(p => p.Id == srcPlanId);
                if (srcPlan != null) ApplyReservationsViaPlanning(srcPlan);
            }
            else
            {
                RefreshLotReservations(order.Items.Where(i => i.LotId != null).Select(i => i.LotId.Value));
            }

            // §جودة التمور: الإرسال للفحص — النتيجة متوقعة بعد يومَي تبريد (العيب لا يظهر إلا بعد أن يبرد المنتج)
            if (sendToQuality)
            {
                Db.QualityChecks.Add(new QualityCheck
                {
                    DocumentNumber = Numbering.Next("QC"),
                    OrderId = order.Id,
                    ExecutionId = exe.Id,
                    CheckDate = DateTime.Now,
                    CheckType = "نهائي — بعد التبريد (يومان)",
                    TotalCheckedKg = producedKg,
                    ExpectedCheckDate = DateTime.Today.AddDays(2),
                    Status = DocStatuses.Submitted
                });
            }
            Db.SaveChanges();
            var finalExecution = Db.ProductionExecutions.AsNoTracking()
                .FirstOrDefault(e => e.Id == exe.Id && e.OrderId == orderId);
            ExecutionCloseTrace.Write($"CloseProductionDay FINAL OrderId={orderId} ExecutionId={finalExecution?.Id.ToString() ?? "<null>"} IsDayClosed={finalExecution?.IsDayClosed.ToString() ?? "<null>"} Status={finalExecution?.Status ?? "<null>"} EndDateTime={finalExecution?.EndDateTime?.ToString("O") ?? "<null>"}");
            if (finalExecution == null || !finalExecution.IsDayClosed
                || finalExecution.Status != DocStatuses.Completed || finalExecution.EndDateTime == null)
                throw new DomainException(
                    "تم إيقاف العملية: سجل التنفيذ لم يثبت كإقفال مكتمل في قاعدة البيانات.",
                    "EXECUTION_CLOSE_NOT_PERSISTED");

            // لا تُقفل الخطة عند تسجيل الفعلي. إقفالها يحدث فقط عند تحرير
            // أمر تسليم الإنتاج من إدارة الإنتاج (ProductionDeliveryService.IssueDelivery).
            string planMsg = "";

            // §B88/M13: تفصيل البنود في رسالة النجاح عند الإقفال متعدد البنود
            string itemsMsg = "";
            if (perItem && itemTake!.Count > 1)
            {
                var parts = new List<string>();
                foreach (var kv in itemTake)
                {
                    var oi = order.Items.First(i => i.Id == kv.Key);
                    string pName = Db.Products.AsNoTracking().Where(x => x.Id == oi.ProductId).Select(x => x.ProductNameAr).FirstOrDefault() ?? $"صنف #{oi.ProductId}";
                    parts.Add($"{pName}: {kv.Value.kg:N1} كجم ({kv.Value.boxes:N0} كرتون)");
                }
                itemsMsg = "\n📦 تفصيل البنود: " + string.Join("؛ ", parts) + ".";
            }

            // §B86/L4: صياغة صادقة — المتبقي يعود لخام دفعته ولا يرتبط تلقائياً بخطة الغد
            string carryMsg = exe.CarryToNextDay
                ? $"\n⏪ المتبقي في الصالة {remainingInHall:N1} كجم أُعيد لخام دفعته — أعد تخطيطه يدوياً في خطة اليوم التالي."
                : (remainingInHall > 0 ? $"\nالمتبقي في الصالة: {remainingInHall:N1} كجم (أُعيد لخام الدفعة)." : "");
            // §1.50.72 P3-6: تنبيه صريح في نتيجة الإقفال عندما أُسقط الخام «بالمخطط»
            string rawMsg = consumedRawKg > 0 ? "" : "\n⚠ خام المستهلك رُحِّل بالقيمة المخططة (لم يُدخل الفعلي) — راجع دفتر الخام.";
            string qMsg = sendToQuality
                ? $"\n🔬 أُرسل للجودة — الفحص متوقع {DateTime.Today.AddDays(2):dd/MM/yyyy} (فترة تبريد يومان)." +
                  "\nيُسمح بالتسليم لمخزن التام الآن؛ تسليم العميل بانتظار اعتماد الفحص."
                : "";
            return OpResult.Success(
                $"🔒 أُقفل يوم الإنتاج للأمر {order.DocumentNumber}: المنتَج {producedKg:N1} كجم ({producedCartons:N0} كرتون)" +
                $" | حشف {hashfKg:N1} | نوى {nawaKg:N1} | هالك {wastageKg:N1} | خام مستهلك {consumed:N1}." +
                rawMsg + carryMsg + qMsg + planMsg + itemsMsg + yieldMsg, exe.Id, exe.DocumentNumber);
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // §B95 — حُذف ClosePlanItems نهائياً (كان ~320 سطراً): مسار إقفال موازٍ ميت —
    // لا تستدعيه أي شاشة — يكرر منطق الإقفال اليومي برياضيات مختلفة. المسار الرسمي
    // الوحيد هو CloseProductionDay عبر أمر الإنتاج، وقد استوعب منه القيمتين
    // الوحيدتين: تسوية المواد المساعدة (SettleAuxMaterials) وتوريد الكرتون الفارغ
    // (PostEmptyCartonsForClose). جداول PlanClosing* باقية للقراءة التاريخية فقط.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>إعادة احتساب الحجوزات عبر خدمة التخطيط (تفادياً لازدواج المنطق).</summary>
    private void ApplyReservationsViaPlanning(ProductionPlan plan)
        => RefreshLotReservations(plan.Items.Where(i => i.LotId != null).Select(i => i.LotId.Value));

    /// <summary>§B85/H1: إعادة احتساب الحجز المخزن للدفعات — تُستدعى بعد كل إقفال (يوم/بنود).</summary>
    private void RefreshLotReservations(IEnumerable<int> lotIds)
    {
        // الحجز = مجموع (المخطط − المنتَج) في الخطط النشطة
        foreach (var lid in lotIds.Distinct())
        {
            var lot = Db.Lots.FirstOrDefault(l => l.Id == lid);
            if (lot == null) continue;
            double reserved = Db.ProductionPlanItems
                .Where(i => i.LotId == lid)
                .Join(Db.ProductionPlans, i => i.PlanId, p => p.Id, (i, p) => new { i, p })
                .Where(x => x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && !x.p.IsClosed)
                .Where(x => !x.i.IsClosed)
                .Sum(x => x.i.PlannedQtyKg - x.i.ProducedQtyKg);
            lot.ReservedQtyKg = reserved > 0 ? reserved : 0;
        }
    }
}

/// <summary>§7 — فحص الجودة مع منع تكرار الفحص لنفس الجلسة (§8).</summary>
public class QualityService : ServiceBase, IQualityService
{
    private readonly IAuditService _audit;

    public QualityService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering, IAuditService audit)
        : base(db, session, numbering)
    {
        _audit = audit;
    }

    public OpResult SaveCheck(int? orderId, int? executionId, string checkDate, string checkType, List<QualityItemDto> items,
        List<(int byProductId, double qtyKg)> byProducts = null, QualityLabDto lab = null)
    {
        if (items == null || items.Count == 0) return OpResult.Fail("أدخل بنداً واحداً على الأقل في الفحص.");
        if (executionId != null && Db.QualityChecks.Any(c => c.ExecutionId == executionId && c.IsApproved))
            return OpResult.Fail("يوجد فحص معتمد مسبقاً لجلسة التنفيذ هذه — لا يسمح بتكرار الفحص.");

        // §مسار الإقفال: إن وُجد فحص غير معتمد وغير ملغى تُستكمل نتيجته (ولا ينشأ فحص مكرر).
        var check = executionId != null
            ? Db.QualityChecks.Include(c => c.Items).FirstOrDefault(c => c.ExecutionId == executionId
                && !c.IsApproved && c.Status != DocStatuses.Cancelled)
            : null;
        // قشرة محضر الجودة التي ينشئها إقفال الإنتاج تلقائياً لا تمثل فحصاً محفوظاً؛
        // السماح لمن يملك Create بإكمالها، مع إبقاء تعديل النتائج المسجلة لصلاحية Edit.
        Require("quality", QualityInspectionState.HasRecordedResults(check, Db) ? "Edit" : "Create");
        // §تتبع الصنف: الأمر وبنوده — الفحص لا يقبل أصنافاً خارج الأمر
        var order = Db.ProductionOrders.AsNoTracking().Include(o => o.Items).FirstOrDefault(o => o.Id == orderId);

        // §B95 — التحقق الإجباري: أمر التشغيل موجود + إنتاج مسجل (الفحص اليدوي بلا أمر مستثنى)
        if (orderId != null && order == null)
            return OpResult.Fail("أمر التشغيل غير موجود — تحقق من رقم الأمر.");
        if (order != null && order.Items.Sum(i => i.ProducedQtyKg) <= 0)
            return OpResult.Fail($"لا يوجد إنتاج مسجل لأمر التشغيل {order.DocumentNumber} — لا يمكن الفحص قبل تسجيل الإنتاج.");
        if (order != null)
        {
            // إذا تكرر (الصنف/الدفعة) على أكثر من بند في الأمر مع وجود كمية مرفوضة جزئية بلا هوية بند، يجب تمرير هوية البند (OrderItemId أو العميل/العبوة)
            var ambiguousOrderItems = order.Items
                .Where(i => i.ProducedCartons > 0 || i.ProducedQtyKg > 0.001)
                .GroupBy(i => new { i.ProductId, i.LotId })
                .FirstOrDefault(g => g.Count() > 1
                    && items.Any(it => it.ProductId == g.Key.ProductId && it.LotId == g.Key.LotId
                        && (it.RejectedQtyKg > 0.001 || it.RejectedCartons > 0)
                        && it.OrderItemId == null && it.PackagingTypeId == null && it.CustomerId == null));
            if (ambiguousOrderItems != null)
            {
                string ambiguousName = Db.Products.AsNoTracking()
                    .Where(p => p.Id == ambiguousOrderItems.Key.ProductId)
                    .Select(p => p.ProductNameAr).FirstOrDefault() ?? $"#{ambiguousOrderItems.Key.ProductId}";
                return OpResult.Fail(
                    $"لا يمكن حفظ الفحص: الصنف/الدفعة «{ambiguousName}» موزع على أكثر من بند (عميل/عبوة). " +
                    "حدّد هوية البند (OrderItemId/العميل/العبوة) قبل الحفظ حتى لا تختلط النتائج.");
            }
        }

        return RunOp(() =>
        {
            if (check != null && check.IsApproved)
                throw new DomainException("الفحص معتمد — لا يسمح بالتعديل إلا عبر «تصحيح معتمد» بسبب مسجل.");
            if (check == null)
            {
                check = new QualityCheck
                {
                    DocumentNumber = Numbering.Next("QC"),
                    OrderId = orderId,
                    ExecutionId = executionId,
                    Status = DocStatuses.Draft
                };
                Db.QualityChecks.Add(check);
            }
            else
            {
                if (check.Items.Count > 0) Db.QualityCheckItems.RemoveRange(check.Items);
                check.Items.Clear();
                check.OrderId = orderId;
            }
            check.CheckDate = UiFormat.TryParseDate(checkDate, out var d) ? d : DateTime.Now;
            check.CheckType = checkType ?? "نهائي";
            if (check.ExpectedCheckDate == null)
                check.ExpectedCheckDate = (UiFormat.TryParseDate(checkDate, out var cd2) ? cd2 : DateTime.Today).AddDays(2);
            foreach (var it in items)
            {
                if (!double.IsFinite(it.CheckedQtyKg) || !double.IsFinite(it.AcceptedQtyKg) || !double.IsFinite(it.RejectedQtyKg)
                    || !double.IsFinite(it.ByProductQtyKg) || !double.IsFinite(it.ScrapQtyKg)
                    || !double.IsFinite(it.CheckedCartons) || !double.IsFinite(it.AcceptedCartons) || !double.IsFinite(it.RejectedCartons)
                    || !double.IsFinite(it.ByProductCartons) || !double.IsFinite(it.ScrapCartons))
                    throw new DomainException("كميات الفحص يجب أن تكون أرقاماً صالحة.");
                if (it.AcceptedQtyKg < 0 || it.RejectedQtyKg < 0 || it.ByProductQtyKg < 0 || it.ScrapQtyKg < 0)
                    throw new DomainException("الكميات لا يمكن أن تكون سالبة.");
                if (it.CheckedCartons < 0 || it.AcceptedCartons < 0 || it.RejectedCartons < 0 || it.ByProductCartons < 0 || it.ScrapCartons < 0)
                    throw new DomainException("عدد الكراتين لا يمكن أن تكون سالباً.");

                string eqName = Db.Products.AsNoTracking().Where(p => p.Id == it.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? $"#{it.ProductId}";
                double partsKg = it.AcceptedQtyKg + it.RejectedQtyKg + it.ByProductQtyKg + it.ScrapQtyKg;
                double partsCtn = it.AcceptedCartons + it.RejectedCartons + it.ByProductCartons + it.ScrapCartons;
                if (it.CheckedQtyKg <= 0) it.CheckedQtyKg = partsKg;
                else if (Math.Abs(it.CheckedQtyKg - partsKg) > 0.01)
                    throw new DomainException($"⛔ معادلة الفحص مختلة للصنف «{eqName}»: المفحوص ({it.CheckedQtyKg:N1} كجم) ≠ مقبول ({it.AcceptedQtyKg:N1}) + مرفوض ({it.RejectedQtyKg:N1}) + ثانوي/فاقد ({it.ByProductQtyKg + it.ScrapQtyKg:N1}).");
                if (it.CheckedCartons <= 0) it.CheckedCartons = partsCtn;
                else if (Math.Abs(it.CheckedCartons - partsCtn) > 0.001)
                    throw new DomainException($"⛔ معادلة الفحص مختلة للصنف «{eqName}»: المفحوص ({it.CheckedCartons:N0} كرتون) ≠ مقبول ({it.AcceptedCartons:N0}) + مرفوض ({it.RejectedCartons:N0}) + ثانوي/فاقد ({it.ByProductCartons + it.ScrapCartons:N0}).");

                UnitsPolicy.RequireItemType(Db, it.ProductId, "Finished", "بند فحص الجودة");

                var matchingOrderItems = order?.Items.Where(oi => oi.ProductId == it.ProductId
                        && (it.LotId == null || oi.LotId == it.LotId)
                        && (it.PackagingTypeId == null || oi.PackagingTypeId == it.PackagingTypeId)
                        && (it.CustomerId == null || (oi.CustomerId ?? order.CustomerId) == it.CustomerId)).ToList()
                    ?? new List<ProductionOrderItem>();
                ProductionOrderItem matchedOrderItem = null;
                if (it.OrderItemId is int explicitOrderItemId)
                {
                    matchedOrderItem = order?.Items.FirstOrDefault(oi => oi.Id == explicitOrderItemId && oi.ProductId == it.ProductId);
                    if (matchedOrderItem == null)
                        throw new DomainException("بند أمر الإنتاج المحدد لا يطابق الصنف/الدفعة في الفحص.", "QC_ITEM_MISMATCH");
                }
                else if (matchingOrderItems.Count == 1)
                    matchedOrderItem = matchingOrderItems[0];
                else if (matchingOrderItems.Count > 1 && (it.RejectedQtyKg > 0.001 || it.RejectedCartons > 0))
                    throw new DomainException("هوية بند الفحص غير مكتملة: حدّد بند أمر الإنتاج عند تكرار الصنف والدفعة.", "QC_ITEM_REQUIRED");
                if (order != null && matchedOrderItem == null && matchingOrderItems.Count == 0)
                    throw new DomainException("بند الفحص لا يطابق بنداً فعلياً في أمر الإنتاج.", "QC_ITEM_MISMATCH");
                int? packOfItem = matchedOrderItem?.PackagingTypeId ?? it.PackagingTypeId;
                double ctnW = matchedOrderItem?.CartonWeightKg > 0
                    ? matchedOrderItem.CartonWeightKg
                    : UnitsPolicy.CartonWeight(Db, it.ProductId, packOfItem);
                if (ctnW > 0)
                {
                    bool linkedToOrder = order != null || executionId != null;
                    if (linkedToOrder)
                    {
                        it.AcceptedQtyKg = UnitsPolicy.RequireCartonKgMatch(Db, it.ProductId, packOfItem,
                            it.AcceptedQtyKg, (int)Math.Round(it.AcceptedCartons), "بند فحص الجودة — المقبول");
                        it.RejectedQtyKg = UnitsPolicy.RequireCartonKgMatch(Db, it.ProductId, packOfItem,
                            it.RejectedQtyKg, (int)Math.Round(it.RejectedCartons), "بند فحص الجودة — المرفوض");
                        if (it.ByProductCartons > 0 || it.ByProductQtyKg > 0)
                            it.ByProductQtyKg = UnitsPolicy.RequireCartonKgMatch(Db, it.ProductId, packOfItem,
                                it.ByProductQtyKg, (int)Math.Round(it.ByProductCartons), "بند فحص الجودة — الثانوي");
                        if (it.ScrapCartons > 0 || it.ScrapQtyKg > 0)
                            it.ScrapQtyKg = UnitsPolicy.RequireCartonKgMatch(Db, it.ProductId, packOfItem,
                                it.ScrapQtyKg, (int)Math.Round(it.ScrapCartons), "بند فحص الجودة — الفاقد");
                        it.CheckedQtyKg = UnitsPolicy.RequireCartonKgMatch(Db, it.ProductId, packOfItem,
                            it.CheckedQtyKg, (int)Math.Round(it.CheckedCartons), "بند فحص الجودة — المفحوص");
                    }
                    else
                    {
                        if (it.AcceptedQtyKg <= 0 && it.AcceptedCartons > 0) it.AcceptedQtyKg = Math.Round(it.AcceptedCartons * ctnW, 1);
                        if (it.RejectedQtyKg <= 0 && it.RejectedCartons > 0) it.RejectedQtyKg = Math.Round(it.RejectedCartons * ctnW, 1);
                        if (it.ByProductQtyKg <= 0 && it.ByProductCartons > 0) it.ByProductQtyKg = Math.Round(it.ByProductCartons * ctnW, 1);
                        if (it.ScrapQtyKg <= 0 && it.ScrapCartons > 0) it.ScrapQtyKg = Math.Round(it.ScrapCartons * ctnW, 1);
                        if (it.CheckedQtyKg <= 0 && it.CheckedCartons > 0) it.CheckedQtyKg = Math.Round(it.CheckedCartons * ctnW, 1);
                    }
                    if (it.AcceptedCartons <= 0 && it.AcceptedQtyKg > 0) it.AcceptedCartons = Math.Round(it.AcceptedQtyKg / ctnW, 2);
                    if (it.RejectedCartons <= 0 && it.RejectedQtyKg > 0) it.RejectedCartons = Math.Round(it.RejectedQtyKg / ctnW, 2);
                    if (it.ByProductCartons <= 0 && it.ByProductQtyKg > 0) it.ByProductCartons = Math.Round(it.ByProductQtyKg / ctnW, 2);
                    if (it.ScrapCartons <= 0 && it.ScrapQtyKg > 0) it.ScrapCartons = Math.Round(it.ScrapQtyKg / ctnW, 2);
                    if (it.CheckedCartons <= 0 && it.CheckedQtyKg > 0) it.CheckedCartons = Math.Round(it.CheckedQtyKg / ctnW, 2);
                }

                if (order != null && order.Items.Count > 0 && !order.Items.Any(i => i.ProductId == it.ProductId))
                {
                    string name = Db.Products.AsNoTracking().Where(p => p.Id == it.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? $"#{it.ProductId}";
                    throw new DomainException(
                        $"⛔ الصنف «{name}» ليس من بنود أمر الإنتاج {order.DocumentNumber}.\n" +
                        "الفحص يستقبل منتجات الأمر بهويتها الفعلية فقط — لا يمكن فحص صنف لم يُنتج في هذا الأمر.",
                        "FOREIGN_PRODUCT");
                }
                ProductIdentityGuard.EnsureConversionAllowed(Db, it.ProductId, it.LotId);

                check.Items.Add(new QualityCheckItem
                {
                    OrderItemId = matchedOrderItem?.Id ?? it.OrderItemId,
                    CustomerId = matchedOrderItem?.CustomerId ?? it.CustomerId ?? order?.CustomerId,
                    PackagingTypeId = packOfItem,
                    ProductId = it.ProductId,
                    LotId = it.LotId ?? matchedOrderItem?.LotId,
                    CheckedQtyKg = it.CheckedQtyKg > 0 ? it.CheckedQtyKg : (it.AcceptedQtyKg + it.RejectedQtyKg + it.ByProductQtyKg + it.ScrapQtyKg),
                    AcceptedQtyKg = it.AcceptedQtyKg,
                    RejectedQtyKg = it.RejectedQtyKg,
                    ByProductQtyKg = (decimal)Math.Round(it.ByProductQtyKg, 4),
                    ScrapQtyKg = (decimal)Math.Round(it.ScrapQtyKg, 4),
                    CheckedCartons = it.CheckedCartons > 0 ? it.CheckedCartons : (it.AcceptedCartons + it.RejectedCartons + it.ByProductCartons + it.ScrapCartons),
                    AcceptedCartons = it.AcceptedCartons,
                    RejectedCartons = it.RejectedCartons,
                    ByProductCartons = (decimal)Math.Round(it.ByProductCartons, 4),
                    ScrapCartons = (decimal)Math.Round(it.ScrapCartons, 4),
                    Notes = it.Notes
                });
            }

            // §B95 — سقف المنتَج لكل صنف (كراتين إن سُجلت + كيلو) ثم تحديد حالة المحضر
            if (order != null)
            {
                foreach (var g in check.Items.GroupBy(i => new { i.ProductId, i.LotId }))
                {
                    double producedKg = order.Items.Where(i => i.ProductId == g.Key.ProductId && i.LotId == g.Key.LotId).Sum(i => i.ProducedQtyKg);
                    int producedCtn = order.Items.Where(i => i.ProductId == g.Key.ProductId && i.LotId == g.Key.LotId).Sum(i => i.ProducedCartons);
                    double checkedKg = g.Sum(i => i.CheckedQtyKg);
                    double checkedCtn = g.Sum(i => i.CheckedCartons);
                    string pname = Db.Products.AsNoTracking().Where(p => p.Id == g.Key.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? $"#{g.Key.ProductId}";
                    if (checkedCtn > 0 && producedCtn > 0 && checkedCtn > producedCtn + 1.001)   // §سحب: تسامح ±1 كرتون — تدوير اشتقاق الكراتين من الكيلو
                        throw new DomainException($"⛔ نتيجة الفحص للصنف «{pname}» ({checkedCtn:N0} كرتون) تتجاوز الكمية المنتجة ({producedCtn:N0} كرتون).");
                    if (checkedKg > producedKg + 0.01)
                        throw new DomainException($"⛔ نتيجة الفحص للصنف «{pname}» ({checkedKg:N1} كجم) تتجاوز الكمية المنتجة ({producedKg:N1} كجم).");
                    // §B95 — منع التغطية المزدوجة: مجموع فحوصات الأمر للصنف لا يتجاوز إنتاجه (الحالي مستثنى لأنه يُستبدل)
                    double otherCheckedKg = Db.QualityCheckItems.AsNoTracking()
                        .Where(i => i.CheckId != check.Id && i.ProductId == g.Key.ProductId && i.LotId == g.Key.LotId)
                        .Join(Db.QualityChecks.AsNoTracking(), i => i.CheckId, c => c.Id, (i, c) => new { i, c })
                        .Where(x => x.c.OrderId == order.Id && x.c.Status != DocStatuses.Cancelled)
                        .Sum(x => x.i.CheckedQtyKg);
                    if (otherCheckedKg > 0.01 && otherCheckedKg + checkedKg > producedKg + 0.01)
                        throw new DomainException($"⛔ الصنف «{pname}» مغطى بفحص سابق ({otherCheckedKg:N1} كجم) — مجموع الفحوصات ({otherCheckedKg + checkedKg:N1} كجم) يتجاوز الإنتاج ({producedKg:N1} كجم). أكمل الفحص السابق بدل إنشاء فحص مكرر.");
                }
                double totCheckedKg = check.Items.Sum(i => i.CheckedQtyKg);
                double totProducedKg = order.Items.Sum(i => i.ProducedQtyKg);
                double totCheckedCtn = check.Items.Sum(i => i.CheckedCartons);
                int totProducedCtn = order.Items.Sum(i => i.ProducedCartons);
                // §B95 — الاكتمال ذاتي (المحضر يغطي الإنتاج وحده) أو تراكمي (فحوصات الأمر مجتمعة تغطيه):
                // بلا التراكمي يستحيل اعتماد الفحص الجزئي الثاني أبداً فيجمُد الأمر — والتغطية المزدوجة ممنوعة أصلاً أعلاه
                var otherCov = Db.QualityCheckItems.AsNoTracking()
                    .Where(i => i.CheckId != check.Id)
                    .Join(Db.QualityChecks.AsNoTracking(), i => i.CheckId, c => c.Id, (i, c) => new { i, c })
                    .Where(x => x.c.OrderId == order.Id && x.c.Status != DocStatuses.Cancelled)
                    .GroupBy(x => 1)
                    .Select(g => new { Kg = g.Sum(x => x.i.CheckedQtyKg), Ctn = g.Sum(x => x.i.CheckedCartons) })
                    .FirstOrDefault();
                double otherKg = otherCov?.Kg ?? 0, otherCtn = otherCov?.Ctn ?? 0;
                double relativeTolKg = totProducedKg * 0.0001;
                double tolKg = relativeTolKg > 0.01 ? relativeTolKg : 0.01;
                bool kgComplete = Math.Abs(totCheckedKg - totProducedKg) <= tolKg
                    || (otherKg > 0.01 && Math.Abs(otherKg + totCheckedKg - totProducedKg) <= tolKg);
                bool ctnComplete = totProducedCtn > 0 && totCheckedCtn > 0
                    && (Math.Abs(totCheckedCtn - totProducedCtn) <= 0.001
                        || (otherCtn > 0.001 && Math.Abs(otherCtn + totCheckedCtn - totProducedCtn) <= 0.001));
                check.Status = (kgComplete || ctnComplete) ? DocStatuses.Completed : DocStatuses.InProgress;
            }
            else
            {
                check.Status = DocStatuses.Completed; // يدوي بلا أمر: الاكتمال = وجود نتائج مسجلة
            }
            if (!string.IsNullOrWhiteSpace(Session?.UserName))
                check.InspectorName = Session.UserName;

            // §قرار الجودة ومعايير الفحص المخبري والحسي (المواصفة القياسية المعتمدة للتمور)
            if (lab != null)
            {
                check.Decision = lab.Decision is "Passed" or "Quarantine" or "Rejected" ? lab.Decision : "Passed";
                check.MoisturePct = lab.MoisturePct;
                check.BrixDeg = lab.BrixDeg;
                check.SkinSeparationPct = lab.SkinSeparationPct;
                check.ImpuritiesPct = lab.ImpuritiesPct;
                check.SampleCartons = lab.SampleCartons;
                check.InspectorNotes = lab.InspectorNotes;
            }
            check.TotalCheckedKg = check.Items.Sum(i => i.CheckedQtyKg);
            check.AcceptedKg = check.Items.Sum(i => i.AcceptedQtyKg);
            check.RejectedKg = check.Items.Sum(i => i.RejectedQtyKg);
            check.TotalCheckedCartons = check.Items.Sum(i => i.CheckedCartons);
            check.AcceptedCartons = check.Items.Sum(i => i.AcceptedCartons);
            check.RejectedCartons = check.Items.Sum(i => i.RejectedCartons);
            Db.SaveChanges();

            if (byProducts != null)
                foreach (var (bpId, qty) in byProducts)
                {
                    // §نظام الوحدات: المخرجات الثانوية (003) أصناف ثانوية معرفة وبالكيوجرام فقط — لا كراتين
                    if (!Db.ByProducts.Any(b => b.Id == bpId))
                        throw new DomainException("الصنف الثانوي غير موجود في بطاقة الأصناف الثانوية.");
                    if (qty < 0) throw new DomainException("كمية المخرج الثانوي لا يمكن أن تكون سالبة.");
                    Db.QualityByProductRecords.Add(new QualityByProductRecord { CheckId = check.Id, ByProductId = bpId, QtyKg = qty });
                }

            Db.SaveChanges();
            return OpResult.Success($"تم حفظ فحص الجودة {check.DocumentNumber} — مقبول {check.AcceptedKg:N1} كجم — الحالة: {QualityCheckStatuses.ToArabic(check.Status)}.", check.Id, check.DocumentNumber);
        });
    }

    /// <summary>§B95 — التغطية التراكمية لأمر: مجموع مفحوص كل فحوصاته يغطي إنتاجه (كيلو، أو كراتين إن سُجلت).</summary>
    private bool OrderFullyChecked(int orderId)
    {
        var o = Db.ProductionOrders.AsNoTracking().Include(x => x.Items).FirstOrDefault(x => x.Id == orderId);
        if (o == null) return false;
        double producedKg = o.Items.Sum(i => i.ProducedQtyKg);
        int producedCtn = o.Items.Sum(i => i.ProducedCartons);
        var cids = Db.QualityChecks.AsNoTracking()
            .Where(c => c.OrderId == orderId && c.Status != DocStatuses.Cancelled).Select(c => c.Id).ToList();
        if (cids.Count == 0) return false;
        double checkedKg = Db.QualityCheckItems.AsNoTracking().Where(i => cids.Contains(i.CheckId)).Sum(i => i.CheckedQtyKg);
        double checkedCtn = Db.QualityCheckItems.AsNoTracking().Where(i => cids.Contains(i.CheckId)).Sum(i => i.CheckedCartons);
        double relativeTolKg = producedKg * 0.0001;
        double tolKg = relativeTolKg > 0.01 ? relativeTolKg : 0.01;
        bool kgOk = Math.Abs(checkedKg - producedKg) <= tolKg;
        bool ctnOk = producedCtn > 0 && checkedCtn > 0 && Math.Abs(checkedCtn - producedCtn) <= 0.001;
        return kgOk || ctnOk;
    }

    public OpResult ApproveCheck(int checkId)
    {
        Require("quality", "Approve");
        var check = Db.QualityChecks.FirstOrDefault(c => c.Id == checkId);
        if (check == null) return OpResult.Fail("الفحص غير موجود.");
        if (check.Status == DocStatuses.Cancelled) return OpResult.Fail("الفحص ملغي.");
        if (check.IsApproved) return OpResult.Fail("الفحص معتمد مسبقاً.");
        var resultTypeIds = Db.InspectionResults.AsNoTracking().Where(r => r.CheckId == check.Id)
            .Select(r => r.ResultTypeId).Distinct().ToList();
        if (Db.InspectionResultTypes.AsNoTracking().Any(t => resultTypeIds.Contains(t.Id)
                && (t.Code == "RT-OK" || t.NameAr == "خرج تام مطابق")))
            return OpResult.Fail("لا يمكن اعتماد هذا المحضر التاريخي لأنه يتضمن «خرج تام مطابق» كإجمالي متداخل؛ اعرضه أو اطبعه، أو ألغِه بسبب موثق وأنشئ فحصاً بتصنيفات فعلية.");
        // QC-01: «مطابق» بلا كمية مقبولة ليس إفراجاً صالحاً. تقرير مرفوض/محجوز
        // يمكن اعتماده كقرار جودة (لأغراض العزل والتدقيق)، لكنه لا يفتح أي تسليم.
        double acceptedForApproval = Db.QualityCheckItems.AsNoTracking()
            .Where(i => i.CheckId == check.Id).Sum(i => i.AcceptedQtyKg);
        if (check.Decision == QualityGate.Passed && acceptedForApproval <= 0.001)
            return OpResult.Fail("⛔ لا يمكن اعتماد فحص مطابق بلا كمية مقبولة أكبر من صفر.");
        bool coolingRequired = Db.SystemSettings.AsNoTracking()
            .Any(s => s.SettingKey == "Quality_CoolingRequired" && s.SettingValue == "1");
        if (coolingRequired && check.ExpectedCheckDate.HasValue
            && check.ExpectedCheckDate.Value.Date > Db.BusinessNow.Date)
            return OpResult.Fail($"⛔ لا يمكن اعتماد الفحص قبل انتهاء التبريد في {check.ExpectedCheckDate.Value:dd/MM/yyyy}.");
        if (check.OrderId is int checkOrderId)
        {
            var checkItems = Db.QualityCheckItems.AsNoTracking().Where(i => i.CheckId == check.Id).ToList();
            var unscopedKeys = checkItems.Where(i => i.OrderItemId == null && i.PackagingTypeId == null && i.CustomerId == null)
                .Select(i => new { i.ProductId, i.LotId }).ToList();
            if (unscopedKeys.Count > 0)
            {
                var checkedProducts = unscopedKeys.Select(k => k.ProductId).Distinct().ToList();
                var checkedLots = unscopedKeys.Select(k => k.LotId).Distinct().ToList();
                var orderIdentity = Db.ProductionOrderItems.AsNoTracking()
                    .Where(i => i.OrderId == checkOrderId && checkedProducts.Contains(i.ProductId)
                        && checkedLots.Contains(i.LotId))
                    .ToList()
                    .GroupBy(i => new { i.ProductId, i.LotId })
                    .FirstOrDefault(g => g.Count() > 1);
                if (orderIdentity != null)
                    return OpResult.Fail("⛔ لا يمكن اعتماد الفحص: هوية المقبول موزعة على أكثر من بند متشابه للصنف/الدفعة دون ربط بند الأمر. افصل البند/العبوة قبل الإفراج.");
            }
        }
        // §B95 — لا اعتماد لمحضر غير مكتمل: نتائج الفحص يجب أن تغطي كامل الإنتاج
        if (check.Status != DocStatuses.Completed)
        {
            if (check.OrderId.HasValue && !OrderFullyChecked(check.OrderId.Value))
                return OpResult.Fail($"⛔ لا يمكن اعتماد المحضر — حالته «{QualityCheckStatuses.ToArabic(check.Status)}» وليس «مكتملاً».\nالاعتماد يتطلب تغطية كامل الكمية المنتجة بنتائج الفحص (مطابق + غير مطابق + مرفوض = المنتَج).");
            if (!check.OrderId.HasValue && !Db.QualityCheckItems.Any(i => i.CheckId == check.Id && (i.CheckedQtyKg > 0 || i.CheckedCartons > 0)))
                return OpResult.Fail("⛔ لا يمكن اعتماد المحضر بلا بنود أو كميات مفحوصة.");
            check.Status = DocStatuses.Completed;
        }

        return RunOp(() =>
        {
            check.IsApproved = true;
            check.Status = DocStatuses.Approved;
            check.ApprovedBy = Session?.UserId;
            check.ApprovedDate = DateTime.Now;
            CompleteDocumentTasks(WorkflowDocTypes.QualityCheck, check.Id, WorkflowTaskTypes.QualityCheck, actionResult: check.Decision ?? "Approved", notes: "اعتُمد فحص الجودة");
            Db.SaveChanges();
            if (check.OrderId != null) PlanSync.SyncAccepted(Db, check.OrderId.Value);
            Db.SaveChanges();
            string decAr = check.Decision switch { "Quarantine" => " (قرار: حجز وتحريز مؤقت)", "Rejected" => " (قرار: مرفوض/عوادم)", _ => "" };
            return OpResult.Success($"تم اعتماد فحص الجودة{decAr}.", check.Id, check.DocumentNumber);
        });
    }

    /// <summary>
    /// §B95 — تصحيح معتمد على فحص معتمد: يتطلب صلاحية (الجودة/تعديل بعد الاعتماد)
    /// وسبباً مكتوباً يُسجَّل في التدقيق مع المستخدم والوقت — ثم يُعاد الفحص «قيد الفحص» للتعديل.
    /// </summary>
    public OpResult RequestCorrection(int checkId, string reason)
    {
        Require("quality", "EditAfterApproval");
        if (string.IsNullOrWhiteSpace(reason))
            return OpResult.Fail("التصحيح المعتمد يتطلب سبباً مكتوباً يُسجَّل في التدقيق.");
        var check = Db.QualityChecks.FirstOrDefault(c => c.Id == checkId);
        if (check == null) return OpResult.Fail("الفحص غير موجود.");
        if (!check.IsApproved) return OpResult.Fail("الفحص غير معتمد — عدّله بالحفظ العادي.");
        if (Db.FinishedGoodsReceipts.Any(r => r.QualityCheckId == checkId && r.IsApproved))
            return OpResult.Fail("لا يمكن تصحيح الفحص بعد ترحيل سند التام — اعكس السند/الحجز أولاً ثم افتح التصحيح.");
        if (check.OrderId != null && Db.CustomerDeliveries.Any(d => d.OrderId == check.OrderId && d.IsApproved))
            return OpResult.Fail("لا يمكن تصحيح الفحص بعد تسليم عميل معتمد — نفّذ التسوية والعكس أولاً.");
        var checkedLots = Db.QualityCheckItems.AsNoTracking()
            .Where(i => i.CheckId == checkId && i.LotId != null).Select(i => i.LotId!.Value).ToList();
        if (checkedLots.Count > 0 && (from ci in Db.CustomerDeliveryItems.AsNoTracking()
                                      join cd in Db.CustomerDeliveries.AsNoTracking() on ci.DeliveryId equals cd.Id
                                      where cd.IsApproved && ci.LotId != null && checkedLots.Contains(ci.LotId.Value)
                                      select ci.Id).Any())
            return OpResult.Fail("لا يمكن تصحيح الفحص بعد تسليم دفعاته لعميل — نفّذ التسوية والعكس أولاً.");

        return RunOp(() =>
        {
            check.IsApproved = false;
            check.Status = DocStatuses.InProgress;
            Db.QualityCorrections.Add(new QualityCorrection
            {
                CheckId = check.Id,
                Reason = reason.Trim(),
                CorrectedBy = Session?.UserId,
                CorrectedByName = Session?.UserName,
                CorrectedDate = DateTime.Now
            });
            Db.SaveChanges();
            if (check.OrderId != null)
            {
                PlanSync.SyncAccepted(Db, check.OrderId.Value);
                Db.SaveChanges();
            }
            _audit.Log("الفحص والجودة", "تصحيح معتمد", "QualityCheck", check.DocumentNumber, check.Id,
                new { الحالة_السابقة = "معتمد" }, new { الحالة_الجديدة = "قيد الفحص", السبب = reason.Trim() });
            return OpResult.Success($"فُتح المحضر {check.DocumentNumber} للتصحيح المعتمد — سُجل السبب في التدقيق.");
        });
    }
}


/// <summary>مزامنة تقدم بنود الخطة من أوامر الإنتاج والتسليمات.</summary>
public static class PlanSync
{
    /// <summary>المنتَج: يوزع إنتاج بنود الأمر على بنود الخطة المرتبطة ويحدّث حالة التنفيذ.</summary>
    public static void SyncProduced(DatesErp.Infrastructure.Persistence.DatesErpDbContext db, int orderId)
    {
        var orderItems = db.ProductionOrderItems.Where(i => i.OrderId == orderId && i.PlanItemId != null).ToList();
        foreach (var oi in orderItems)
        {
            var pi = db.ProductionPlanItems.FirstOrDefault(x => x.Id == oi.PlanItemId);
            if (pi == null) continue;
            pi.ProducedQtyKg = db.ProductionOrderItems
                .Join(db.ProductionOrders, x => x.OrderId, o => o.Id, (x, o) => new { x, o })
                .Where(z => z.x.PlanItemId == pi.Id && z.o.Status != DocStatuses.Cancelled)
                .Sum(z => z.x.ProducedQtyKg);
            UpdateStatus(pi);
        }
    }

    /// <summary>المقبول: من فحوصات الجودة المعتمدة عبر جميع أوامر بند الخطة وباحترام الهوية الكاملة (OrderItemId / العميل / العبوة / الدفعة).</summary>
    public static void SyncAccepted(DatesErp.Infrastructure.Persistence.DatesErpDbContext db, int orderId)
    {
        var touchedPlanItemIds = db.ProductionOrderItems.Where(i => i.OrderId == orderId && i.PlanItemId != null)
            .Select(i => i.PlanItemId!.Value).Distinct().ToList();
        foreach (var planItemId in touchedPlanItemIds)
        {
            var pi = db.ProductionPlanItems.FirstOrDefault(x => x.Id == planItemId);
            if (pi == null) continue;
            var allLinkedOrderItems = (from oi in db.ProductionOrderItems.AsNoTracking()
                                       join o in db.ProductionOrders.AsNoTracking() on oi.OrderId equals o.Id
                                       where oi.PlanItemId == pi.Id && o.Status != DocStatuses.Cancelled
                                       select oi).ToList();
            double totalAccepted = 0;
            foreach (var linkedOi in allLinkedOrderItems)
            {
                var approvedRows = (from q in db.QualityCheckItems.AsNoTracking()
                                    join c in db.QualityChecks.AsNoTracking() on q.CheckId equals c.Id
                                    where c.OrderId == linkedOi.OrderId && c.IsApproved && c.Decision == QualityGate.Passed
                                        && q.ProductId == linkedOi.ProductId
                                    select q).ToList();
                var explicitRows = approvedRows.Where(q => q.OrderItemId == linkedOi.Id).ToList();
                if (explicitRows.Count > 0)
                {
                    totalAccepted += explicitRows.Sum(q => q.AcceptedQtyKg);
                }
                else
                {
                    var fallbackRows = approvedRows.Where(q => q.OrderItemId == null
                        && (linkedOi.LotId == null || q.LotId == linkedOi.LotId)
                        && (q.PackagingTypeId == null || q.PackagingTypeId == linkedOi.PackagingTypeId)
                        && (q.CustomerId == null || q.CustomerId == linkedOi.CustomerId)).ToList();
                    totalAccepted += fallbackRows.Sum(q => q.AcceptedQtyKg);
                }
            }
            pi.AcceptedQtyKg = totalAccepted;
            UpdateStatus(pi);
        }
    }

    /// <summary>§CD-FIX: المسلَّم يوزع على بنود خطة السند فقط (PlanId) — لا على كل خطط العميل+الصنف.
    /// أُثبت بالتشغيل أن التوزيع الواسع كان يلوث خططاً أخرى (خطط تبريد/حجز/رفض ظهرت «مكتملة»)
    /// ويُسقط الفائض عن السعة بصمت. الفائض الآن يرفض برسالة صريحة.</summary>
    public static void SyncDeliveredForPlan(DatesErp.Infrastructure.Persistence.DatesErpDbContext db, int planId, int customerId, int productId, int? packagingTypeId, double qtyKg, string docNo)
    {
        var remaining = qtyKg;
        var items = db.ProductionPlanItems
            .Where(i => i.PlanId == planId && i.CustomerId == customerId && i.ProductId == productId && i.ScheduledDate != null
                && (packagingTypeId == null || i.PackagingTypeId == packagingTypeId))
            .OrderBy(i => i.ScheduledDate).ThenBy(i => i.PriorityNo)
            .ToList();
        foreach (var pi in items)
        {
            if (remaining <= 0) break;
            double slot = pi.PlannedQtyKg - pi.DeliveredQtyKg;
            if (slot <= 0) continue;
            double take = Math.Min(slot, remaining);
            pi.DeliveredQtyKg += take;
            remaining -= take;
            UpdateStatus(pi);
        }
        if (remaining > 0.001)
            throw new DomainException(
                $"⛔ الكمية المسلَّمة في السند {docNo} تتجاوز سعة الخطة بـ {remaining:N1} كجم — لم يُوزَّع الفائض على أي خطة.\n" +
                "راجع سعة الخطة (أو عدّلها رسمياً) قبل الاعتماد.",
                "PLAN_OVERFLOW");
    }

    /// <summary>§CD-FIX: عكس توزيع المسلَّم عند إلغاء الاعتماد (LIFO على نفس نطاق الخطة).
    /// العكس بأثر ما هو موجود (حد أدنى صفر) حتى لا يعلق الإلغاء أبداً.</summary>
    public static void UnsyncDeliveredForPlan(DatesErp.Infrastructure.Persistence.DatesErpDbContext db, int planId, int customerId, int productId, int? packagingTypeId, double qtyKg)
    {
        var remaining = qtyKg;
        var items = db.ProductionPlanItems
            .Where(i => i.PlanId == planId && i.CustomerId == customerId && i.ProductId == productId && i.ScheduledDate != null
                && (packagingTypeId == null || i.PackagingTypeId == packagingTypeId))
            .OrderByDescending(i => i.ScheduledDate).ThenByDescending(i => i.Id)
            .ToList();
        foreach (var pi in items)
        {
            if (remaining <= 0) break;
            double take = Math.Min(Math.Max(pi.DeliveredQtyKg, 0), remaining);
            if (take <= 0) continue;
            pi.DeliveredQtyKg -= take;
            remaining -= take;
            UpdateStatus(pi);
        }
    }

    public static void UpdateStatus(DatesErp.Core.Domain.Entities.ProductionPlanItem pi)
    {
        if (pi.ProducedQtyKg <= 0 && pi.DeliveredQtyKg <= 0) pi.ExecutionStatus = "NotStarted";
        else if (pi.ProducedQtyKg + 0.001 >= pi.PlannedQtyKg && pi.DeliveredQtyKg + 0.001 >= pi.PlannedQtyKg) pi.ExecutionStatus = "Completed";
        else if (pi.DeliveredQtyKg > 0) pi.ExecutionStatus = "Partial";
        else pi.ExecutionStatus = "InProgress";
    }
}
