using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §B96 — أوامر تسليم الإنتاج (إدارة الإنتاج — يحررها مدير الإنتاج):
/// مسودة ← مُصدَرة ← مستلمة (عبر سندات الاستلام المخزنية).
/// المصدر التشغيلي الجديد: جلسة الإنتاج الفعلي المكتملة فقط؛ وتبقى مصادر الفحص/الخطة للقراءة التاريخية والتوافق مع المستندات القديمة.
/// البند = أمر + صنف + دفعة + عميل + كمية — عميل أو عدة عملاء، صنف أو أكثر.
/// سقفان: سقف المصدر (المقبول/المنتَج) + سقف فيزيائي موحد (كل المصادر ≤ المنتَج).
/// </summary>
public partial class ProductionDeliveryService : ServiceBase, IProductionDeliveryService
{
    private readonly IAuditService _audit;

    public ProductionDeliveryService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering, IAuditService audit)
        : base(db, session, numbering)
    {
        _audit = audit;
    }

    private ExecutionService CreateActualExecutionService()
        => new(Db, Session, Numbering) { JoinParentTransaction = true, RecordingActualDelivery = true };

    public OpResult SaveDelivery(string sourceType, int sourceId, string deliveryDate, List<ProductionDeliveryItemDto> items,
        string bypassReason = null, string notes = null)
    {
        Require("production", "Create");
        if (sourceType != DeliverySources.FromActual && sourceType != DeliverySources.FromCheck
            && sourceType != DeliverySources.FromPlan && sourceType != DeliverySources.FromClosing)
            return OpResult.Fail("مصدر التسليم غير معروف — اختر جلسة إنتاج فعلي مكتملة.");
        if (items == null || items.Count == 0)
            return OpResult.Fail("أدخل بنداً واحداً على الأقل في أمر التسليم.");

        // §B96 — التجاوز بصلاحية وسبب مكتوب (لا تجاوز صامت إطلاقاً)
        bool bypass = DeliverySources.IsBypass(sourceType);
        if (bypass)
        {
            Require("production", "BypassInspection");
            if (string.IsNullOrWhiteSpace(bypassReason))
                return OpResult.Fail("التسليم من الخطة/الإقفال يتجاوز الفحص — أدخل سبب التجاوز مكتوباً ليُحفظ موثقاً في الأمر.");
        }

        // §التحقق من المصدر وحالته
        QualityCheck check = null;
        ProductionPlan plan = null;
        ProductionOrder checkOrder = null;
        ProductionExecution execution = null;
        if (sourceType == DeliverySources.FromActual)
        {
            execution = Db.ProductionExecutions.AsNoTracking().FirstOrDefault(e => e.Id == sourceId);
            if (execution == null) return OpResult.Fail("جلسة الإنتاج الفعلي غير موجودة — احفظ الفعلي أولاً.");
            if (!execution.IsDayClosed || execution.Status != DocStatuses.Completed)
                return OpResult.Fail("لا يمكن إنشاء أمر تسليم قبل إقفال جلسة الإنتاج الفعلي.");
            checkOrder = Db.ProductionOrders.Include(o => o.Items)
                .FirstOrDefault(o => o.Id == execution.OrderId && o.IsApproved && o.Status != DocStatuses.Cancelled);
            if (checkOrder == null)
                return OpResult.Fail("أمر الإنتاج المرتبط بالفعلي غير موجود أو غير معتمد.");
        }
        else if (sourceType == DeliverySources.FromCheck)
        {
            check = Db.QualityChecks.Include(c => c.Items).FirstOrDefault(c => c.Id == sourceId);
            if (check == null) return OpResult.Fail("محضر الفحص غير موجود — تحقق من الرقم.");
            // §B102 — الهوية قبل الحالة: الفحص اليدوي بلا أمر لا يُسلَّم منه إطلاقاً (حتى لو اعتُمِد)
            if (check.OrderId == null) return OpResult.Fail("الفحص اليدوي بلا أمر لا يُسلَّم منه — التسليم من إنتاج أمر تشغيل فقط.");
            if (!check.IsApproved) return OpResult.Fail($"محضر الفحص {check.DocumentNumber} غير معتمد — لا يُسلَّم إلا من محضر معتمد.");
            checkOrder = Db.ProductionOrders.Include(o => o.Items).FirstOrDefault(o => o.Id == check.OrderId.Value);
            if (checkOrder == null) return OpResult.Fail("أمر المحضر غير موجود.");
        }
        else
        {
            plan = Db.ProductionPlans.Include(p => p.Items).FirstOrDefault(p => p.Id == sourceId);
            if (plan == null) return OpResult.Fail("الخطة غير موجودة — تحقق من الرقم.");
            if (!plan.IsApproved) return OpResult.Fail("الخطة غير معتمدة — اعتمدها أولاً.");
            if (plan.Status == DocStatuses.Cancelled) return OpResult.Fail("الخطة ملغاة — لا يُسلَّم منها.");
            if (sourceType == DeliverySources.FromClosing && !plan.IsClosed)
                return OpResult.Fail("الخطة غير مقفلة — التسليم من الإقفال يتطلب خطة مقفلة (أو سلِّم من الخطة مباشرة).");
        }

        var srcLines = BuildSourceLines(sourceType, sourceId, check, checkOrder, plan, execution);

        return RunOp(() =>
        {
            // PRD-03: أعد تحميل المصدر داخل المعاملة؛ لا تنشئ سنداً من تنفيذ/أمر أُلغي
            // بعد فحص الشاشة.
            if (sourceType == DeliverySources.FromActual)
            {
                var currentExecution = Db.ProductionExecutions.AsNoTracking().FirstOrDefault(e => e.Id == sourceId);
                if (currentExecution == null || !currentExecution.IsDayClosed || currentExecution.Status != DocStatuses.Completed)
                    throw new DomainException("جلسة الإنتاج الفعلي غير موجودة أو لم تعد مكتملة.", "ACTUAL_STATE_CHANGED");
                var currentOrder = Db.ProductionOrders.AsNoTracking().FirstOrDefault(o => o.Id == currentExecution.OrderId);
                if (currentOrder == null || currentOrder.Status == DocStatuses.Cancelled || !currentOrder.IsApproved)
                    throw new DomainException("أمر الإنتاج المرتبط غير موجود أو ملغى.", "ORDER_CANCELLED");
            }
            var delivery = new ProductionDelivery
            {
                DocumentNumber = Numbering.Next("PDL"),
                DeliveryDate = UiFormat.TryParseDate(deliveryDate, out var d) ? d : DateTime.Now,
                SourceType = sourceType,
                SourceId = sourceId,
                BypassReason = bypass ? bypassReason.Trim() : null,
                Status = DocStatuses.Draft,
                ReceiptStatus = "None",
                Notes = notes
            };

            PopulateDeliveryItems(delivery, sourceType, srcLines, items);

            Db.ProductionDeliveries.Add(delivery);
            Db.SaveChanges();
            if (bypass)
                _audit.Log("الإنتاج", "تسليم بتجاوز الفحص", "ProductionDelivery", delivery.DocumentNumber, delivery.Id,
                    new { المصدر = DeliverySources.ToArabic(sourceType) }, new { السبب = delivery.BypassReason });
            double total = delivery.Items.Sum(i => i.QtyKg);
            return OpResult.Success(
                $"تم إنشاء أمر تسليم الإنتاج {delivery.DocumentNumber} من {DeliverySources.ToArabic(sourceType)} — {delivery.Items.Count} بنود ({total:N1} كجم)." +
                (bypass ? " (بتجاوز موثق للفحص)" : "") + " — بانتظار تحرير مدير الإنتاج.",
                delivery.Id, delivery.DocumentNumber);
        });
    }

    private void PopulateDeliveryItems(ProductionDelivery delivery, string sourceType,
        List<DeliverySourceLine> srcLines, List<ProductionDeliveryItemDto> items)
    {
        var takenPerLine = new Dictionary<DeliverySourceLine, double>();
        var takenPackagesPerLine = new Dictionary<DeliverySourceLine, int>();
        foreach (var it in items)
        {
            if (it.ProductId <= 0)
                throw new DomainException("لا يمكن تسليم مخرج ثانوي إلى المخزون دون هوية صنف مخزون قائمة ومصرّح بها.", "BYPRODUCT_STOCK_UNMAPPED");
            UnitsPolicy.RequireItemType(Db, it.ProductId, "Finished", "بند أمر تسليم الإنتاج");

            if (!double.IsFinite(it.QtyKg) || it.QtyKg <= 0)
                throw new DomainException($"كمية البند للصنف «{ProdName(it.ProductId)}» يجب أن تكون أكبر من صفر.");
            if (it.WarehouseId is int warehouseId
                && !Db.Warehouses.AsNoTracking().Any(w => w.Id == warehouseId && w.IsActive))
                throw new DomainException("مخزن الاستلام المختار غير موجود أو موقوف.", "WAREHOUSE_INVALID");

            // لا يُستنتج ربط المخرج الثانوي بصنف المخزون؛ التسليم هنا للمنتجات التامة فقط.
            bool strictPackagingIdentity = sourceType == DeliverySources.FromActual;
            var candidates = srcLines.Where(l => l.ByProductId == null
                && l.ProductId == it.ProductId && l.LotId == it.LotId
                && (!strictPackagingIdentity || l.PackagingTypeId == it.PackagingTypeId)).ToList();
            DeliverySourceLine line;
            if (it.CustomerId != null)
                line = candidates.FirstOrDefault(l => l.CustomerId == it.CustomerId);
            else if (candidates.Count > 1)
                throw new DomainException(
                    $"⛔ الصنف «{ProdName(it.ProductId)}»" +
                    (it.LotId != null ? $" من الدفعة «{LotCode(it.LotId)}»" : "") +
                    $" مشترك بين {candidates.Count} عملاء في {DeliverySources.ToArabic(sourceType)} — حدد عميل البند صراحةً.\n" +
                    "العملاء: " + string.Join(" · ", candidates.Select(c => c.CustomerName ?? "بلا عميل")),
                    "AMBIGUOUS_CUSTOMER");
            else
                line = candidates.FirstOrDefault();
            if (line == null)
                throw new DomainException(
                    $"لا يوجد سطر مطابق في {DeliverySources.ToArabic(sourceType)} للصنف «{ProdName(it.ProductId)}»" +
                    (it.LotId != null ? $" والدفعة «{LotCode(it.LotId)}»" : "") + " — راجع الكمية الفعلية وربط المخرج.",
                    "NO_SOURCE_LINE");

            int? effectivePackagingTypeId = it.PackagingTypeId ?? line.PackagingTypeId;
            if (it.PackagingTypeId != null && it.PackagingTypeId != line.PackagingTypeId)
                throw new DomainException("عبوة بند أمر التسليم لا تطابق العبوة في المصدر التشغيلي.", "PACKAGING_MISMATCH");

            // سقف الإنتاج الفعلي: يحسب الكمية المحجوزة غير الملغاة، لا المخطط النظري.
            takenPerLine.TryGetValue(line, out double takenBefore);
            if (takenBefore + it.QtyKg > line.RemainingQtyKg + 0.01)
                throw new DomainException(
                    $"⛔ كمية البند ({it.QtyKg:N1} كجم) تتجاوز المتبقي القابل للتسليم ({line.RemainingQtyKg:N1} كجم) " +
                    $"للصنف «{line.ProductName}» في {DeliverySources.ToArabic(sourceType)}." +
                    (takenBefore > 0 ? $"\nمأخوذ في بنود أخرى من هذا المستند: {takenBefore:N1} كجم." : ""),
                    "OVER_SOURCE");
            takenPerLine[line] = takenBefore + it.QtyKg;

            int effectivePackageCount = it.PackageCount;
            if (effectivePackageCount <= 0 && it.QtyKg > 0)
            {
                double cw = UnitsPolicy.CartonWeight(Db, it.ProductId, effectivePackagingTypeId);
                if (cw > 0) effectivePackageCount = (int)Math.Round(it.QtyKg / cw);
                else if (line.PackageCount > 0 && line.AvailableQtyKg > 0)
                    effectivePackageCount = (int)Math.Round(line.PackageCount * (it.QtyKg / line.AvailableQtyKg));
            }
            if (strictPackagingIdentity)
            {
                if (effectivePackageCount <= 0 || line.PackageCount <= 0)
                    throw new DomainException("لا يمكن إنشاء أمر تسليم بلا عدد كراتين صحيح للمصدر والبند.", "PACKAGE_COUNT_REQUIRED");
                takenPackagesPerLine.TryGetValue(line, out var takenPackages);
                if (line.DeliveredPackageCount + takenPackages + effectivePackageCount > line.PackageCount)
                    throw new DomainException("مجموع كراتين أوامر التسليم يتجاوز المتبقي من عدد كراتين سطر المصدر.", "PACKAGE_COUNT_OVER");
                takenPackagesPerLine[line] = takenPackages + effectivePackageCount;
            }
            else
            {
                if (effectivePackageCount <= 0) effectivePackageCount = line.PackageCount;
                if (line.PackageCount > 0 && effectivePackageCount > line.PackageCount)
                    throw new DomainException("عدد كراتين أمر التسليم يتجاوز عدد كراتين المصدر التاريخي.", "PACKAGE_COUNT_OVER");
            }

            // سقف فيزيائي موحّد بين المصادر يحمي كمية المنتج التام الفعلية.
            if (line.OrderId is int lineOrderId)
            {
                double produced = Db.ProductionOrderItems.AsNoTracking()
                    .Where(o => o.OrderId == lineOrderId && o.ProductId == it.ProductId).Sum(o => o.ProducedQtyKg);
                double deliveredAll = Db.ProductionDeliveryItems.AsNoTracking()
                    .Join(Db.ProductionDeliveries.AsNoTracking(), i => i.DeliveryId, d => d.Id, (i, d) => new { i, d })
                    .Where(x => x.d.Status != DocStatuses.Cancelled && x.d.Id != delivery.Id && x.i.OrderId == lineOrderId
                        && x.i.ProductId == it.ProductId)
                    .Sum(x => x.i.QtyKg);
                double directReceived = Db.FinishedGoodsReceiptItems.AsNoTracking()
                    .Join(Db.FinishedGoodsReceipts.AsNoTracking(), i => i.ReceiptId, r => r.Id, (i, r) => new { i, r })
                    .Where(x => x.r.Status != DocStatuses.Cancelled && x.r.DeliveryId == null
                        && x.r.OrderId == lineOrderId && x.i.ProductId == it.ProductId)
                    .Sum(x => x.i.ReceivedQtyKg);
                double thisDoc = delivery.Items.Where(i => i.OrderId == lineOrderId
                    && i.ProductId == it.ProductId).Sum(i => i.QtyKg);
                if (deliveredAll + directReceived + thisDoc + it.QtyKg > produced + 0.01)
                    throw new DomainException(
                        $"⛔ التسليم يتجاوز الإنتاج الفعلي للصنف «{line.ProductName}».\n" +
                        $"المنتَج: {produced:N1} كجم | المُسلَّم بأوامر أخرى: {deliveredAll:N1} | المستلَم مباشرةً: {directReceived:N1} | هذا الأمر بعد الإضافة: {thisDoc + it.QtyKg:N1}",
                        "OVER_PRODUCED");
            }

            // تتبع الدفعة محفوظ للمنتج التام؛ لا ننسب مخرجاً ثانوياً إلى دفعة خام بلا علاقة صريحة.
            if (it.LotId is int lineLot && line.OrderId is int lineOrd)
            {
                ProductIdentityGuard.EnsureConversionAllowed(Db, it.ProductId, lineLot);
                bool lotOk = Db.ProductionOrderItems.AsNoTracking()
                    .Any(o => o.OrderId == lineOrd && o.ProductId == it.ProductId && o.LotId == lineLot);
                if (!lotOk)
                    throw new DomainException($"⛔ الدفعة «{LotCode(lineLot)}» ليست دفعة هذا الصنف في الأمر المصدر.", "LOT_MISMATCH");
            }

            delivery.Items.Add(new ProductionDeliveryItem
            {
                OrderId = line.OrderId,
                ProductId = it.ProductId,
                LotId = it.LotId,
                CustomerId = line.CustomerId,
                PackagingTypeId = effectivePackagingTypeId,
                WarehouseId = it.WarehouseId,
                PackageCount = effectivePackageCount,
                QtyKg = it.QtyKg
            });
        }
    }

    public OpResult CreateDeliveryFromActual(int executionId, string deliveryDate = null, string notes = null)
    {
        Require("production", "Create");
        var execution = Db.ProductionExecutions.AsNoTracking().FirstOrDefault(e => e.Id == executionId);
        if (execution == null) return OpResult.Fail("جلسة الإنتاج الفعلي غير موجودة.");
        if (!execution.IsDayClosed || execution.Status != DocStatuses.Completed)
            return OpResult.Fail("لا يمكن إنشاء أمر تسليم قبل حفظ الإنتاج الفعلي وإقفاله.");
        // يسمح التنفيذ الواحد بأكثر من أمر تسليم عند التسليم الجزئي أو التوزيع على أكثر من مخزن.
        // السقف المشترك يحسب كل الأوامر غير الملغاة داخل GetSourceContext.
        var ctx = GetSourceContext(DeliverySources.FromActual, executionId);
        var items = ctx.Lines.Where(l => l.ProductId > 0 && l.RemainingQtyKg > 0.001)
            .Select(l => new ProductionDeliveryItemDto
            {
                OrderId = l.OrderId,
                ProductId = l.ProductId,
                LotId = l.LotId,
                CustomerId = l.CustomerId,
                PackagingTypeId = l.PackagingTypeId,
                PackageCount = l.RemainingPackageCount,
                QtyKg = Math.Round(l.RemainingQtyKg, 3)
            }).ToList();
        if (items.Count == 0) return OpResult.Fail("لا توجد كمية فعلية قابلة للتسليم في جلسة الإنتاج.");
        return SaveDelivery(DeliverySources.FromActual, executionId,
            deliveryDate ?? DateTime.Now.ToString("dd/MM/yyyy"), items, null, notes);
    }

    public OpResult UpdateDelivery(int deliveryId, string deliveryDate, List<ProductionDeliveryItemDto> items, string notes = null)
    {
        Require("production", "Edit");
        if (items == null || items.Count == 0) return OpResult.Fail("أدخل بنداً واحداً على الأقل في أمر التسليم.");
        var delivery = Db.ProductionDeliveries.AsNoTracking().FirstOrDefault(d => d.Id == deliveryId);
        if (delivery == null) return OpResult.Fail("أمر التسليم غير موجود.");
        if (delivery.SourceType != DeliverySources.FromActual)
            return OpResult.Fail("لا يمكن تعديل أمر تسليم قديم غير نازل من الإنتاج الفعلي.");
        if (delivery.Status != DocStatuses.Draft)
            return OpResult.Fail("لا يمكن تعديل أمر التسليم بعد تحريره للمخزن.");

        return RunOp(() =>
        {
            // أعد تحميل الحالة والمصدر بعد بدء المعاملة؛ لا تعتمد على كيان أو سياق قديم
            // قرأته الشاشة قبل نقرة الحفظ. مستوى العزل التسلسلي في هذه الخدمة يمنع
            // تجاوز سقف المصدر إذا حاول مستخدمان تعديل/إنشاء مستندين بالتزامن.
            Db.ChangeTracker.Clear();
            var current = Db.ProductionDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == deliveryId)
                ?? throw new DomainException("أمر التسليم غير موجود.", "DELIVERY_MISSING");
            if (current.SourceType != DeliverySources.FromActual)
                throw new DomainException("لا يمكن تعديل أمر تسليم قديم غير نازل من الإنتاج الفعلي.", "DELIVERY_SOURCE_INVALID");
            if (current.Status != DocStatuses.Draft)
                throw new DomainException("تغيرت حالة أمر التسليم — لا يمكن تعديله بعد تحريره للمخزن.", "DELIVERY_NOT_DRAFT");
            var currentItemIds = current.Items.Select(x => x.Id).ToList();
            if (current.Items.Any(i => i.ReceivedQtyKg > 0.001)
                || (currentItemIds.Count > 0 && Db.FinishedGoodsReceiptItems.Any(i => i.DeliveryItemId != null
                    && currentItemIds.Contains(i.DeliveryItemId.Value) && i.ReceivedQtyKg > 0.001)))
                throw new DomainException("بدأ استلام هذا الأمر — لا يمكن تعديله.", "DELIVERY_RECEIPT_STARTED");

            var ctx = GetSourceContext(current.SourceType, current.SourceId);
            // مسودة الأمر الحالي لا تحجب نفسها عند إعادة حفظها.
            foreach (var line in ctx.Lines)
            {
                var oldRows = current.Items.Where(i => i.ProductId == line.ProductId
                    && i.LotId == line.LotId && i.CustomerId == line.CustomerId
                    && i.PackagingTypeId == line.PackagingTypeId).ToList();
                double old = oldRows.Sum(i => i.QtyKg);
                int oldPackages = oldRows.Sum(i => i.PackageCount);
                line.DeliveredQtyKg = line.DeliveredQtyKg > old ? line.DeliveredQtyKg - old : 0;
                line.DeliveredPackageCount = line.DeliveredPackageCount > oldPackages
                    ? line.DeliveredPackageCount - oldPackages : 0;
                line.RemainingQtyKg = line.AvailableQtyKg > line.DeliveredQtyKg
                    ? line.AvailableQtyKg - line.DeliveredQtyKg : 0;
            }

            Db.ProductionDeliveryItems.RemoveRange(current.Items);
            current.Items.Clear();
            Db.SaveChanges(); // لا تحتسب حواجز السقف بنود المسودة القديمة أثناء إعادة البناء
            PopulateDeliveryItems(current, DeliverySources.FromActual, ctx.Lines, items);
            current.DeliveryDate = UiFormat.TryParseDate(deliveryDate, out var d) ? d : current.DeliveryDate;
            current.Notes = notes;
            current.ModifiedBy = Session?.UserId;
            current.ModifiedDate = DateTime.Now;
            Db.SaveChanges();
            return OpResult.Success($"تم تعديل أمر تسليم الإنتاج {current.DocumentNumber} — ما زال مسودة بانتظار تحرير مدير الإنتاج.", current.Id, current.DocumentNumber);
        });
    }

    public OpResult IssueDelivery(int deliveryId)
    {
        Require("production", "Approve");
        var delivery = Db.ProductionDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == deliveryId);
        if (delivery == null) return OpResult.Fail("أمر التسليم غير موجود.");
        if (delivery.Status == DocStatuses.Issued) return OpResult.Fail("أمر التسليم مُحرَّر مسبقاً.");
        if (delivery.Status == DocStatuses.Cancelled) return OpResult.Fail("أمر التسليم ملغى.");
        if (delivery.Status == DocStatuses.Completed) return OpResult.Fail("أمر التسليم مستلم بالكامل.");

        return RunOp(() =>
        {
            // PRD-03: حالة أمر التسليم قد تكون تغيرت بعد فحص ما قبل المعاملة.
            Db.ChangeTracker.Clear();
            delivery = Db.ProductionDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == deliveryId)
                ?? throw new DomainException("أمر التسليم غير موجود.", "DELIVERY_MISSING");
            if (delivery.Status == DocStatuses.Cancelled)
                throw new DomainException("أمر التسليم ملغى.", "DELIVERY_CANCELLED");
            if (delivery.Status == DocStatuses.Issued)
                throw new DomainException("أمر التسليم مُحرَّر مسبقاً.", "DELIVERY_ISSUED");
            if (delivery.Status == DocStatuses.Completed)
                throw new DomainException("أمر التسليم مستلم بالكامل.", "DELIVERY_COMPLETED");
            if (delivery.Items.Count == 0)
                throw new DomainException("لا يمكن تحرير أمر بلا بنود.");
            delivery.Status = DocStatuses.Issued;
            delivery.IsApproved = true;
            delivery.ApprovedBy = Session?.UserId;
            delivery.ApprovedDate = DateTime.Now;
            Db.SaveChanges(); // اجعل أمر التسليم الحالي مرئياً لمزامنة دفتر الخطة داخل نفس المعاملة.
            string planMessage = SyncLinkedPlanAfterDeliveryChange(delivery);
            RaiseWorkflowTask(new NewTaskRequest
            {
                Stage = WorkflowStage.FinishedGoodsReceipt,
                TaskType = WorkflowTaskTypes.WarehouseReceipt,
                DocumentType = WorkflowDocTypes.ProductionDelivery,
                DocumentId = delivery.Id,
                DocumentNumber = delivery.DocumentNumber,
                BusinessDate = delivery.DeliveryDate ?? Db.BusinessNow.Date,
                DueDate = delivery.DeliveryDate ?? Db.BusinessNow.Date,
                RequiredCapability = WorkflowCapabilities.WarehouseReceive,
                FromCapability = WorkflowCapabilities.ProductionDayClose,
                Title = $"استلام إنتاج من الصالة — أمر التسليم {delivery.DocumentNumber}"
            });
            Db.SaveChanges();
            return OpResult.Success($"تم تحرير أمر التسليم {delivery.DocumentNumber} إلى المخازن — بانتظار أمر استلام أمين مخزن التام.{planMessage}");
        });
    }

    /// <summary>
    /// إقفال الخطة هو أثر تحرير أمر تسليم الإنتاج، لا أثر اعتماد أمر الإنتاج
    /// ولا أثر تسجيل الفعلي ولا أثر استلام المخزن. الصلاحية حُكمت في IssueDelivery
    /// عبر production/Approve، ولا تُستخدم بوابة بديلة.
    /// </summary>
    /// <summary>
    /// PRD-04 — مزامنة الخطة من دفتر أوامر التسليم الفعلية.
    /// لا تُقفل الخطة عند أول أمر ولا عند تسليم جلسة جزئية؛ تُقفل بعد اكتمال إنتاج
    /// الكميات المخططة وتغطية الناتج بأوامر تسليم غير ملغاة. وكل انتقال (إقفال/إعادة فتح) يعيد احتساب
    /// حجوزات الدفعات داخل نفس المعاملة.
    /// </summary>
    private string SyncLinkedPlanAfterDeliveryChange(ProductionDelivery triggeringDelivery)
    {
        if (triggeringDelivery.SourceType != DeliverySources.FromActual) return "";
        var execution = Db.ProductionExecutions.AsNoTracking().FirstOrDefault(e => e.Id == triggeringDelivery.SourceId);
        if (execution == null) throw new DomainException("جلسة الإنتاج الفعلي المرتبطة بأمر التسليم غير موجودة.", "ACTUAL_MISSING");
        var sourceOrder = Db.ProductionOrders.AsNoTracking().FirstOrDefault(o => o.Id == execution.OrderId);
        if (sourceOrder?.SourcePlanId == null) return "";

        var plan = Db.ProductionPlans.Include(p => p.Items).FirstOrDefault(p => p.Id == sourceOrder.SourcePlanId.Value);
        if (plan == null) throw new DomainException("الخطة المرتبطة بالإنتاج الفعلي غير موجودة.", "PLAN_MISSING");
        if (plan.Status == DocStatuses.Cancelled)
            throw new DomainException("الخطة المرتبطة ملغاة — لا يمكن تحرير أمر التسليم.", "PLAN_CANCELLED");
        if (!plan.IsApproved)
            throw new DomainException("الخطة المرتبطة غير معتمدة — لا يمكن تحرير أمر التسليم.", "PLAN_NOT_APPROVED");

        var orderIds = Db.ProductionOrders.AsNoTracking()
            .Where(o => o.SourcePlanId == plan.Id && o.SourceType == "FromPlan" && o.Status != DocStatuses.Cancelled)
            .Select(o => o.Id).ToList();
        var orderItems = Db.ProductionOrderItems.AsNoTracking()
            .Where(i => orderIds.Contains(i.OrderId) && i.PlanItemId != null)
            .ToList();
        // تحتسب فقط أوامر تسليم الإنتاج المحررة فعلياً (لا المسودات ولا الملغاة!)
        // وتُكتب في WarehouseHandedQtyKg حتى لا تلوث DeliveredQtyKg الخاصة بتسليم العميل النهائي!
        var deliveries = (from di in Db.ProductionDeliveryItems.AsNoTracking()
                          join d in Db.ProductionDeliveries.AsNoTracking() on di.DeliveryId equals d.Id
                          join e in Db.ProductionExecutions.AsNoTracking() on d.SourceId equals e.Id
                          join o in Db.ProductionOrders.AsNoTracking() on e.OrderId equals o.Id
                          where d.SourceType == DeliverySources.FromActual
                              && d.Status != DocStatuses.Cancelled && d.Status != DocStatuses.Draft
                              && o.SourcePlanId == plan.Id
                          select new { di, OrderId = o.Id }).ToList();

        foreach (var pi in plan.Items)
        {
            var matchingOrderIds = orderItems.Where(i => i.PlanItemId == pi.Id).Select(i => i.OrderId).ToHashSet();
            double handedToWarehouse = deliveries
                .Where(x => matchingOrderIds.Contains(x.di.OrderId ?? x.OrderId)
                    && x.di.ProductId == pi.ProductId
                    && (pi.LotId == null || x.di.LotId == pi.LotId)
                    && x.di.CustomerId == pi.CustomerId
                    && x.di.PackagingTypeId == pi.PackagingTypeId)
                .Sum(x => x.di.QtyKg);
            pi.WarehouseHandedQtyKg = (decimal)Math.Round(handedToWarehouse, 4);
        }

        // لا تُغلق الخطة بمجرد تسليم إنتاج جلسة جزئية ولا بوجود مسودات؛
        // تُغلق تشغيلياً فقط بعد اكتمال الإنتاج المخطط لكل البنود وتحرير تسليمه للمخازن وبلا أوامر معلقة غير مكتملة.
        bool hasPendingOrders = Db.ProductionOrders.AsNoTracking()
            .Any(o => o.SourcePlanId == plan.Id && o.SourceType == "FromPlan"
                && o.Status != DocStatuses.Cancelled && !o.IsClosed
                && o.Status != DocStatuses.Completed && o.Status != DocStatuses.PendingDelivery);
        bool allProducedItemsHandedOver = !hasPendingOrders && plan.Items.Count > 0 && plan.Items.All(pi =>
            pi.ProducedQtyKg + 0.001 >= pi.PlannedQtyKg
            && (double)pi.WarehouseHandedQtyKg + 0.001 >= pi.ProducedQtyKg);
        string message;
        bool wasClosed = plan.IsClosed;
        var planItemIds = plan.Items.Select(i => i.Id).ToList();
        var planAllocs = Db.ProductionPlanItemAllocations.Where(a => planItemIds.Contains(a.PlanItemId)).ToList();
        var affectedLots = plan.Items.Where(i => i.LotId != null).Select(i => i.LotId!.Value)
            .Concat(planAllocs.Select(a => a.LotId))
            .Distinct().ToList();
        if (allProducedItemsHandedOver)
        {
            if (!plan.IsClosed)
            {
                plan.IsClosed = true;
                plan.ClosedDate = DateTime.Now;
                plan.ClosedBy = null; // الإقفال التصنيعي التلقائي ليس أرشفة إدارية نهائية (ClosedBy يُختم عبر PlanClosureService)
                plan.Status = DocStatuses.Closed;
                foreach (var a in planAllocs.Where(a => a.Status != "Released" && a.Status != "Consumed"))
                {
                    a.ReleasedQtyKg = Math.Max(0, a.AllocatedQtyKg - a.ConsumedQtyKg);
                    a.Status = a.ConsumedQtyKg > 0.001 ? "Consumed" : "Released";
                }
                message = $" أُقفلت الخطة {plan.DocumentNumber} بعد اكتمال تسليم جميع بنودها للمخازن.";
            }
            else message = $" الخطة {plan.DocumentNumber} مقفلة بعد اكتمال جميع بنودها.";
        }
        else
        {
            if (plan.IsClosed)
            {
                plan.IsClosed = false;
                plan.ClosedDate = null;
                plan.ClosedBy = null;
                plan.Status = DocStatuses.Approved;
                foreach (var a in planAllocs.Where(a => a.ReleasedQtyKg > 0.001))
                {
                    a.ReleasedQtyKg = 0;
                    a.Status = a.ConsumedQtyKg > 0.001 ? "PartiallyConsumed" : "Reserved";
                }
                message = $" أُعيد فتح الخطة {plan.DocumentNumber} لأن التسليمات المحررة لا تغطي كل بنودها.";
            }
            else message = $" الخطة {plan.DocumentNumber} ما زالت مفتوحة — لم تكتمل كل بنودها.";
        }
        if (!wasClosed && plan.IsClosed)
            _audit.Log("إدارة الإنتاج", "إقفال الخطة بعد اكتمال كل بنود التسليم", "Plan", plan.DocumentNumber, plan.Id,
                new { السبب = "ProductionDelivery.Issue", أمر_التسليم = triggeringDelivery.DocumentNumber },
                new { الحالة_الجديدة = "مقفلة", المستخدم = Session?.UserId });
        else if (wasClosed && !plan.IsClosed)
            _audit.Log("إدارة الإنتاج", "إعادة فتح الخطة بعد إلغاء أمر التسليم", "Plan", plan.DocumentNumber, plan.Id,
                new { السبب = "ProductionDelivery.Cancel", أمر_التسليم = triggeringDelivery.DocumentNumber },
                new { الحالة_الجديدة = "معتمدة", المستخدم = Session?.UserId });
        // الحجز يُقرأ من SQL لا من ChangeTracker؛ احفظ انتقال الحالة داخل نفس
        // المعاملة أولاً حتى لا يُبنى الرقم على حالة Closed/Approved القديمة.
        Db.SaveChanges();
        RecalculatePlanLotReservations(affectedLots);
        return message;
    }

    private void RecalculatePlanLotReservations(IEnumerable<int> lotIds)
    {
        foreach (var lotId in lotIds.Distinct())
        {
            var lot = Db.Lots.FirstOrDefault(l => l.Id == lotId);
            if (lot == null) continue;
            var amounts = (from i in Db.ProductionPlanItems
                           join p in Db.ProductionPlans on i.PlanId equals p.Id
                           where i.LotId == lotId && p.Status != DocStatuses.Closed
                               && p.Status != DocStatuses.Cancelled && !p.IsClosed && !i.IsClosed
                           select new { i.PlannedQtyKg, i.ProducedQtyKg }).ToList();
            var reserved = amounts.Sum(x => x.PlannedQtyKg - x.ProducedQtyKg > 0
                ? x.PlannedQtyKg - x.ProducedQtyKg : 0);
            lot.ReservedQtyKg = reserved > 0 ? reserved : 0;
        }
    }

    public OpResult CancelDelivery(int deliveryId)
    {
        Require("production", "Cancel");
        var delivery = Db.ProductionDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == deliveryId);
        if (delivery == null) return OpResult.Fail("أمر التسليم غير موجود.");
        if (delivery.Status == DocStatuses.Cancelled) return OpResult.Fail("أمر التسليم ملغى مسبقاً.");
        if (delivery.Status == DocStatuses.Completed) return OpResult.Fail("الأمر مستلم بالكامل — ألغِ سندات الاستلام أولاً.");

        return RunOp(() =>
        {
            Db.ChangeTracker.Clear();
            delivery = Db.ProductionDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == deliveryId)
                ?? throw new DomainException("أمر التسليم غير موجود.", "DELIVERY_MISSING");
            if (delivery.Status == DocStatuses.Cancelled)
                throw new DomainException("أمر التسليم ملغى مسبقاً.", "DELIVERY_CANCELLED");
            if (delivery.Status == DocStatuses.Completed)
                throw new DomainException("الأمر مستلم بالكامل — ألغِ سندات الاستلام أولاً.", "DELIVERY_COMPLETED");
            var activeReceipt = Db.FinishedGoodsReceipts.AsNoTracking()
                .Where(r => r.DeliveryId == deliveryId && r.Status != DocStatuses.Cancelled)
                .OrderBy(r => r.Id).Select(r => r.DocumentNumber).FirstOrDefault();
            if (activeReceipt != null)
                throw new DomainException($"لا يمكن إلغاء أمر التسليم لوجود سند استلام مرتبط غير ملغى ({activeReceipt}). ألغِ/اعكس سند الاستلام أولاً.", "HAS_ACTIVE_RECEIPT");
            if (delivery.Items.Any(i => i.ReceivedQtyKg > 0.001))
                throw new DomainException("بدأ استلام هذا الأمر — ألغِ سندات الاستلام أولاً ثم ألغِ الأمر.", "HAS_RECEIPTS");
            delivery.Status = DocStatuses.Cancelled;
            CancelDocumentTasks(WorkflowDocTypes.ProductionDelivery, delivery.Id, "أُلغي أمر التسليم");
            Db.SaveChanges(); // حتى تستبعد المزامنة سند الإلغاء من التسليمات الفعالة داخل نفس المعاملة.
            string planMessage = SyncLinkedPlanAfterDeliveryChange(delivery);
            Db.SaveChanges();
            return OpResult.Success($"تم إلغاء أمر التسليم {delivery.DocumentNumber}." + planMessage);
        });
    }

    public DeliverySourceContext GetSourceContext(string sourceType, int sourceId)
    {
        var ctx = new DeliverySourceContext { SourceType = sourceType, SourceId = sourceId };
        if (sourceType == DeliverySources.FromActual)
        {
            var execution = Db.ProductionExecutions.AsNoTracking().FirstOrDefault(e => e.Id == sourceId);
            if (execution == null) return ctx;
            ctx.SourceNumber = execution.DocumentNumber;
            ctx.SourceDate = UiFormat.D(execution.EndDateTime);
            var order = Db.ProductionOrders.Include(o => o.Items).AsNoTracking().FirstOrDefault(o => o.Id == execution.OrderId);
            ctx.Lines = BuildSourceLines(sourceType, sourceId, null, order, null, execution);
        }
        else if (sourceType == DeliverySources.FromCheck)
        {
            var check = Db.QualityChecks.Include(c => c.Items).AsNoTracking().FirstOrDefault(c => c.Id == sourceId);
            if (check == null) return ctx;
            ctx.SourceNumber = check.DocumentNumber;
            ctx.SourceDate = UiFormat.D(check.CheckDate);
            var order = check.OrderId != null
                ? Db.ProductionOrders.Include(o => o.Items).AsNoTracking().FirstOrDefault(o => o.Id == check.OrderId.Value)
                : null;
            ctx.Lines = BuildSourceLines(sourceType, sourceId, check, order, null, null);
        }
        else if (sourceType == DeliverySources.FromPlan || sourceType == DeliverySources.FromClosing)
        {
            var plan = Db.ProductionPlans.Include(p => p.Items).AsNoTracking().FirstOrDefault(p => p.Id == sourceId);
            if (plan == null) return ctx;
            ctx.SourceNumber = plan.DocumentNumber;
            ctx.SourceDate = UiFormat.D(plan.StartDate);
            ctx.Lines = BuildSourceLines(sourceType, sourceId, null, null, plan, null);
        }
        return ctx;
    }

    public List<(int Id, string Label)> GetSourceDocs(string sourceType)
    {
        if (sourceType == DeliverySources.FromActual)
            return Db.ProductionExecutions.AsNoTracking()
                .Where(e => e.IsDayClosed && e.Status == DocStatuses.Completed)
                .OrderByDescending(e => e.Id).Take(200).ToList()
                .Select(e => (e.Id, $"{e.DocumentNumber} — {UiFormat.D(e.EndDateTime)} — {e.ActualCartons:N0} كرتون"))
                .ToList();
        if (sourceType == DeliverySources.FromCheck)
            return Db.QualityChecks.AsNoTracking().Include(c => c.Items)
                .Where(c => c.IsApproved && c.OrderId != null)
                .OrderByDescending(c => c.Id).Take(200).ToList()
                .Select(c => (c.Id, $"{c.DocumentNumber} — {UiFormat.D(c.CheckDate)} — مقبول {c.Items.Sum(i => i.AcceptedQtyKg):N1} كجم"))
                .ToList();
        if (sourceType == DeliverySources.FromPlan)
            return Db.ProductionPlans.AsNoTracking()
                .Where(p => p.IsApproved && p.Status != DocStatuses.Cancelled && !p.IsClosed)
                .OrderByDescending(p => p.Id).Take(200).ToList()
                .Select(p => (p.Id, $"{p.DocumentNumber} — {UiFormat.D(p.StartDate)}"))
                .ToList();
        if (sourceType == DeliverySources.FromClosing)
            return Db.ProductionPlans.AsNoTracking()
                .Where(p => p.IsApproved && p.IsClosed)
                .OrderByDescending(p => p.Id).Take(200).ToList()
                .Select(p => (p.Id, $"{p.DocumentNumber} — {UiFormat.D(p.StartDate)} (مقفلة)"))
                .ToList();
        return new List<(int, string)>();
    }

    public ProductionDeliveryCard GetDelivery(int deliveryId)
    {
        var d = Db.ProductionDeliveries.Include(x => x.Items).AsNoTracking().FirstOrDefault(x => x.Id == deliveryId);
        if (d == null) return null;
        var sourceContext = GetSourceContext(d.SourceType, d.SourceId);
        var card = new ProductionDeliveryCard
        {
            Id = d.Id,
            DocumentNumber = d.DocumentNumber,
            DeliveryDate = UiFormat.D(d.DeliveryDate),
            SourceType = d.SourceType,
            SourceTypeAr = DeliverySources.ToArabic(d.SourceType),
            SourceId = d.SourceId,
            SourceNumber = SourceNumber(d.SourceType, d.SourceId),
            BypassReason = d.BypassReason,
            Status = d.Status,
            StatusAr = DocStatuses.ToArabic(d.Status),
            ReceiptStatus = d.ReceiptStatus,
            Notes = d.Notes
        };

        if (d.SourceType == DeliverySources.FromActual)
        {
            var execution = Db.ProductionExecutions.AsNoTracking().FirstOrDefault(e => e.Id == d.SourceId);
            if (execution != null)
            {
                var order = Db.ProductionOrders.AsNoTracking().Include(o => o.Items)
                    .FirstOrDefault(o => o.Id == execution.OrderId);
                if (order != null)
                {
                    card.OrderId = order.Id;
                    card.OrderNumber = order.DocumentNumber;
                    card.PlannedQtyKg = order.Items.Sum(i => i.PlannedQtyKg);
                    card.ActualQtyKg = order.Items.Sum(i => i.ProducedQtyKg);
                    card.WastageQtyKg = execution.WastageQtyKg;
                    card.ProductionDate = order.ProductionDate?.ToString("dd/MM/yyyy");
                    card.PlanNumber = order.SourcePlanId is int planId
                        ? Db.ProductionPlans.AsNoTracking().Where(p => p.Id == planId).Select(p => p.DocumentNumber).FirstOrDefault()
                        : null;
                    card.ShiftName = order.ShiftId is int shiftId
                        ? Db.Shifts.AsNoTracking().Where(s => s.Id == shiftId).Select(s => s.ShiftNameAr).FirstOrDefault()
                        : null;
                    card.LineName = order.LineId is int lineId
                        ? Db.ProductionLines.AsNoTracking().Where(l => l.Id == lineId).Select(l => l.LineNameAr).FirstOrDefault()
                        : null;
                }
            }
        }

        var customerNames = d.Items.Where(i => i.CustomerId != null).Select(i => CustName(i.CustomerId)).Distinct().ToList();
        card.CustomerSummary = customerNames.Count switch
        {
            0 => "عام / مشترك",
            1 => customerNames[0],
            _ => $"عدة عملاء ({customerNames.Count}): {string.Join("، ", customerNames)}"
        };
        card.StockReceivedQtyKg = d.Items.Sum(i => i.ReceivedQtyKg);

        foreach (var i in d.Items)
        {
            var sourceLine = sourceContext.Lines.FirstOrDefault(l => l.ByProductId == null
                && l.ProductId == i.ProductId && l.LotId == i.LotId
                && l.CustomerId == i.CustomerId && l.PackagingTypeId == i.PackagingTypeId);
            var warehouseName = i.WarehouseId is int whId
                ? Db.Warehouses.AsNoTracking().Where(w => w.Id == whId).Select(w => w.WarehouseNameAr).FirstOrDefault()
                : null;
            card.Lines.Add(new ProductionDeliveryLineRow
            {
                Id = i.Id,
                OrderId = i.OrderId,
                OrderNumber = i.OrderId != null ? Db.ProductionOrders.AsNoTracking().Where(o => o.Id == i.OrderId.Value).Select(o => o.DocumentNumber).FirstOrDefault() : null,
                ProductId = i.ProductId,
                ProductName = ProdName(i.ProductId),
                LotId = i.LotId,
                LotCode = i.LotId != null ? LotCode(i.LotId) : null,
                CustomerId = i.CustomerId,
                CustomerName = i.CustomerId != null ? CustName(i.CustomerId) : null,
                PackagingTypeId = i.PackagingTypeId,
                PackagingName = i.PackagingTypeId != null
                    ? Db.PackagingTypes.AsNoTracking().Where(p => p.Id == i.PackagingTypeId.Value).Select(p => p.PackageNameAr).FirstOrDefault()
                    : null,
                Unit = "كجم",
                WarehouseId = i.WarehouseId,
                WarehouseName = warehouseName,
                CartonWeightKg = UnitsPolicy.CartonWeight(Db, i.ProductId, i.PackagingTypeId),
                PackageCount = i.PackageCount,
                PlannedQtyKg = sourceLine?.PlannedQtyKg ?? 0,
                ActualQtyKg = sourceLine?.AvailableQtyKg ?? 0,
                PreviouslyReceivedQtyKg = (sourceLine?.StockReceivedQtyKg ?? i.ReceivedQtyKg) > i.ReceivedQtyKg
                    ? (sourceLine?.StockReceivedQtyKg ?? i.ReceivedQtyKg) - i.ReceivedQtyKg : 0,
                QtyKg = i.QtyKg,
                ReceivedQtyKg = i.ReceivedQtyKg,
                RemainingQtyKg = i.QtyKg > i.ReceivedQtyKg ? i.QtyKg - i.ReceivedQtyKg : 0
            });
        }
        return card;
    }

    public List<ProductionDeliveryCard> GetDeliveries(string statusFilter = null)
    {
        var ids = Db.ProductionDeliveries.AsNoTracking()
            .Where(d => statusFilter == null || d.Status == statusFilter)
            .OrderByDescending(d => d.Id).Take(300).Select(d => d.Id).ToList();
        return ids.Select(GetDelivery).Where(c => c != null).ToList();
    }

    // ── سطور المصدر: المتاح (مقبول/منتَج) ناقص ما سُلِّم بأوامر غير ملغاة ──
    private List<DeliverySourceLine> BuildSourceLines(string sourceType, int sourceId,
        QualityCheck check, ProductionOrder order, ProductionPlan plan, ProductionExecution execution)
    {
        var lines = new List<DeliverySourceLine>();
        if (sourceType == DeliverySources.FromActual)
        {
            if (order == null) return lines;
            var executionIds = Db.ProductionExecutions.AsNoTracking()
                .Where(e => e.OrderId == order.Id && e.IsDayClosed)
                .Select(e => e.Id).ToList();
            var priorDeliveryItems = (from item in Db.ProductionDeliveryItems.AsNoTracking()
                                      join delivery in Db.ProductionDeliveries.AsNoTracking() on item.DeliveryId equals delivery.Id
                                      where delivery.SourceType == DeliverySources.FromActual
                                          && delivery.Status != DocStatuses.Cancelled
                                          && executionIds.Contains(delivery.SourceId)
                                      select new { item.ProductId, item.LotId, item.CustomerId, item.PackagingTypeId,
                                          item.QtyKg, item.PackageCount, item.ReceivedQtyKg }).ToList();
            var deliveredByIdentity = priorDeliveryItems
                .GroupBy(i => new { i.ProductId, i.LotId, i.CustomerId, i.PackagingTypeId })
                .ToDictionary(g => (g.Key.ProductId, g.Key.LotId, g.Key.CustomerId, g.Key.PackagingTypeId),
                    g => (Qty: g.Sum(i => i.QtyKg), Packages: g.Sum(i => i.PackageCount),
                        Received: g.Sum(i => i.ReceivedQtyKg)));
            var actualItems = order.Items
                .Where(i => i.ProducedQtyKg > 0.001 || i.ProducedCartons > 0)
                .GroupBy(i => new { i.ProductId, i.LotId, CustomerId = i.CustomerId ?? order.CustomerId, i.PackagingTypeId });
            foreach (var g in actualItems)
            {
                double produced = g.Sum(i => i.ProducedQtyKg);
                int packages = g.Sum(i => i.ProducedCartons);
                var identity = (g.Key.ProductId, g.Key.LotId, g.Key.CustomerId, g.Key.PackagingTypeId);
                var prior = deliveredByIdentity.GetValueOrDefault(identity);
                double delivered = prior.Qty;
                int deliveredPackages = prior.Packages;
                double stockReceived = prior.Received;
                int plannedCartons = g.Sum(i => i.PlannedCartons);
                double plannedKg = g.Sum(i => i.PlannedQtyKg);
                lines.Add(new DeliverySourceLine
                {
                    OrderId = order.Id,
                    OrderNumber = order.DocumentNumber,
                    ProductId = g.Key.ProductId,
                    ProductName = ProdName(g.Key.ProductId),
                    LotId = g.Key.LotId,
                    LotCode = g.Key.LotId != null ? LotCode(g.Key.LotId) : null,
                    CustomerId = g.Key.CustomerId,
                    CustomerName = g.Key.CustomerId != null ? CustName(g.Key.CustomerId) : null,
                    PackagingTypeId = g.Key.PackagingTypeId,
                    PackagingName = g.Key.PackagingTypeId is int packagingId ? Db.PackagingTypes.AsNoTracking().Where(p => p.Id == packagingId).Select(p => p.PackageNameAr).FirstOrDefault() : null,
                    CartonWeightKg = UnitsPolicy.CartonWeight(Db, g.Key.ProductId, g.Key.PackagingTypeId),
                    Unit = "كجم",
                    PackageCount = packages,
                    PlannedCartons = plannedCartons,
                    ActualCartons = packages,
                    DeliveredPackageCount = deliveredPackages,
                    PlannedQtyKg = plannedKg,
                    AvailableQtyKg = produced,
                    DeliveredQtyKg = delivered,
                    StockReceivedQtyKg = stockReceived,
                    RemainingQtyKg = produced > delivered ? produced - delivered : 0
                });
            }

            // المخرجات الثانوية تجمع جلسات الأمر للعرض فقط. لا يوجد في النموذج الحالي رابط
            // صريح بين تعريف ByProduct وصنف المخزون؛ لا نخمنه من الاسم ولا نرحّله.
            if (execution != null)
            {
                var actualByProducts = Db.ExecutionByProducts.AsNoTracking()
                    .Where(b => executionIds.Contains(b.ExecutionId) && b.Qty > 0)
                    .GroupBy(b => b.ByProductId)
                    .Select(g => new { ByProductId = g.Key, Qty = g.Sum(b => b.Qty) })
                    .OrderBy(b => b.ByProductId).ToList();
                var definitionIds = actualByProducts.Select(b => b.ByProductId).ToList();
                var definitions = Db.ByProducts.AsNoTracking().Where(b => definitionIds.Contains(b.Id))
                    .ToDictionary(b => b.Id);
                foreach (var actual in actualByProducts)
                {
                    if (!definitions.TryGetValue(actual.ByProductId, out var definition)) continue;
                    double produced = (double)actual.Qty;
                    lines.Add(new DeliverySourceLine
                    {
                        OrderId = order.Id,
                        OrderNumber = order.DocumentNumber,
                        ProductId = 0,
                        ProductName = definition.ByProductNameAr,
                        CustomerName = "مشترك في أمر الإنتاج",
                        ByProductId = actual.ByProductId,
                        Unit = definition.UnitOfMeasure,
                        PackageCount = 0,
                        AvailableQtyKg = produced,
                        DeliveredQtyKg = 0,
                        StockReceivedQtyKg = 0,
                        RemainingQtyKg = produced
                    });
                }
            }
        }
        else if (sourceType == DeliverySources.FromCheck)
        {
            if (check == null || order == null) return lines;
            foreach (var g in check.Items.GroupBy(i => new { i.ProductId, i.LotId }))
            {
                double accepted = g.Sum(i => i.AcceptedQtyKg);
                if (accepted <= 0.001) continue;
                // §عميل السطر: بند الأمر المطابق (صنف + دفعة) ثم أول بند للصنف ثم عميل الأمر
                int? cust = order.Items.FirstOrDefault(i => i.ProductId == g.Key.ProductId && i.LotId == g.Key.LotId)?.CustomerId
                    ?? order.Items.FirstOrDefault(i => i.ProductId == g.Key.ProductId)?.CustomerId
                    ?? order.CustomerId;
                double delivered = Db.ProductionDeliveryItems.AsNoTracking()
                    .Join(Db.ProductionDeliveries.AsNoTracking(), i => i.DeliveryId, d => d.Id, (i, d) => new { i, d })
                    .Where(x => x.d.Status != DocStatuses.Cancelled && x.d.SourceType == DeliverySources.FromCheck
                        && x.d.SourceId == sourceId && x.i.ProductId == g.Key.ProductId && x.i.LotId == g.Key.LotId)
                    .Sum(x => x.i.QtyKg);
                lines.Add(new DeliverySourceLine
                {
                    OrderId = order.Id,
                    OrderNumber = order.DocumentNumber,
                    ProductId = g.Key.ProductId,
                    ProductName = ProdName(g.Key.ProductId),
                    LotId = g.Key.LotId,
                    LotCode = g.Key.LotId != null ? LotCode(g.Key.LotId) : null,
                    CustomerId = cust,
                    CustomerName = cust != null ? CustName(cust) : null,
                    AvailableQtyKg = accepted,
                    DeliveredQtyKg = delivered,
                    RemainingQtyKg = accepted > delivered ? accepted - delivered : 0
                });
            }
        }
        else
        {
            if (plan == null) return lines;
            // §التجاوز يسلِّم المنتَج فقط (لا المخطط النظري): المنتَج المزامَن من الأوامر
            var planTypes = new[] { DeliverySources.FromPlan, DeliverySources.FromClosing };
            foreach (var pi in plan.Items)
            {
                if (pi.ProducedQtyKg <= 0.001) continue;
                int? ordId = Db.ProductionOrderItems.AsNoTracking()
                    .Where(o => o.PlanItemId == pi.Id).OrderBy(o => o.Id)
                    .Select(o => (int?)o.OrderId).FirstOrDefault();
                double delivered = Db.ProductionDeliveryItems.AsNoTracking()
                    .Join(Db.ProductionDeliveries.AsNoTracking(), i => i.DeliveryId, d => d.Id, (i, d) => new { i, d })
                    .Where(x => x.d.Status != DocStatuses.Cancelled && planTypes.Contains(x.d.SourceType)
                        && x.d.SourceId == sourceId && x.i.ProductId == pi.ProductId
                        && x.i.LotId == pi.LotId && x.i.CustomerId == pi.CustomerId)
                    .Sum(x => x.i.QtyKg);
                lines.Add(new DeliverySourceLine
                {
                    OrderId = ordId,
                    OrderNumber = ordId != null ? Db.ProductionOrders.AsNoTracking().Where(o => o.Id == ordId.Value).Select(o => o.DocumentNumber).FirstOrDefault() : null,
                    ProductId = pi.ProductId,
                    ProductName = ProdName(pi.ProductId),
                    LotId = pi.LotId,
                    LotCode = pi.LotId != null ? LotCode(pi.LotId) : null,
                    CustomerId = pi.CustomerId,
                    CustomerName = pi.CustomerId != null ? CustName(pi.CustomerId) : null,
                    AvailableQtyKg = pi.ProducedQtyKg,
                    DeliveredQtyKg = delivered,
                    RemainingQtyKg = pi.ProducedQtyKg > delivered ? pi.ProducedQtyKg - delivered : 0
                });
            }
        }
        return lines;
    }

    private string SourceNumber(string sourceType, int sourceId)
    {
        if (sourceType == DeliverySources.FromActual)
            return Db.ProductionExecutions.AsNoTracking().Where(e => e.Id == sourceId).Select(e => e.DocumentNumber).FirstOrDefault() ?? $"#{sourceId}";
        if (sourceType == DeliverySources.FromCheck)
            return Db.QualityChecks.AsNoTracking().Where(c => c.Id == sourceId).Select(c => c.DocumentNumber).FirstOrDefault() ?? $"#{sourceId}";
        return Db.ProductionPlans.AsNoTracking().Where(p => p.Id == sourceId).Select(p => p.DocumentNumber).FirstOrDefault() ?? $"#{sourceId}";
    }

    private string ProdName(int id)
        => Db.Products.AsNoTracking().Where(p => p.Id == id).Select(p => p.ProductNameAr).FirstOrDefault() ?? $"صنف #{id}";

    private string LotCode(int? id)
        => id == null ? null : Db.Lots.AsNoTracking().Where(l => l.Id == id.Value).Select(l => l.LotCode).FirstOrDefault() ?? $"دفعة #{id}";

    private string CustName(int? id)
        => id == null ? null : Db.Customers.AsNoTracking().Where(c => c.Id == id.Value).Select(c => c.CustomerName).FirstOrDefault() ?? $"عميل #{id}";
}
