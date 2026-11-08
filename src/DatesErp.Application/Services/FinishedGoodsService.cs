using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// دورة استلام الإنتاج التام: اختيار مسار الإقفال (مباشر أو جودة معتمدة)، ثم سند مخزني
/// يحفظ ويرحّل مباشرة في معاملة ذرية. تبقى أوامر التسليم القديمة للقراءة والتوافق فقط.
/// </summary>
public class FinishedGoodsService : ServiceBase, IFinishedGoodsService
{
    public OpResult ReceiveCartons(int receiptId, Dictionary<int, int> receivedCartonsByItemId)
    {
        var rcpt = Db.FinishedGoodsReceipts.Include(r => r.Items).FirstOrDefault(r => r.Id == receiptId);
        if (rcpt == null) return OpResult.Fail("أمر الاستلام غير موجود.");
        var kgMap = new Dictionary<int, double>();
        foreach (var kv in receivedCartonsByItemId ?? new())
        {
            var it = rcpt.Items.FirstOrDefault(i => i.Id == kv.Key);
            if (it != null)
            {
                double cw = it.CartonWeightKg > 0 ? it.CartonWeightKg : UnitsPolicy.CartonWeight(Db, it.ProductId, it.PackagingTypeId);
                kgMap[kv.Key] = kv.Value * cw;
            }
        }
        return Receive(receiptId, kgMap);
    }

    // الاستلامات المتوازية على نفس بند أمر التسليم يجب أن ترى حالة المصدر نفسها
    // قبل تعديل ReceivedQtyKg؛ العزل التسلسلي يمنع تجاوز السقف بسبب سباق مستخدمين.
    protected override System.Data.IsolationLevel TransactionIsolation => System.Data.IsolationLevel.Serializable;
    protected override bool RetryTransactionDeadlocks => true;

    public FinishedGoodsService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering) { }

    public OpResult SaveReceipt(int orderId, int? qualityCheckId, string deliveryDate, List<FinishedGoodsItemDto> items, int? deliveryId = null)
    {
        Require("finishedgoods", "Create");
        if (items == null || items.Count == 0) return OpResult.Fail("أدخل بنداً واحداً على الأقل.");
        if (deliveryId == null)
            return OpResult.Fail("لا يمكن إنشاء أمر استلام الإنتاج مباشرة من أمر الإنتاج أو الفعلي. اختر أمر تسليم إنتاج محرراً من مدير الإنتاج.");
        var order = Db.ProductionOrders.Include(o => o.Items).FirstOrDefault(o => o.Id == orderId);
        if (order == null) return OpResult.Fail("أمر الإنتاج غير موجود.");
        // المسار الرسمي الوحيد: أمين مخزن التام ينشئ أمر الاستلام من أمر تسليم فعلي محرر.
        var delivery = Db.ProductionDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == deliveryId.Value);
        if (delivery == null) return OpResult.Fail("أمر تسليم الإنتاج غير موجود — تحقق من الرقم.");
        if (delivery.SourceType != DeliverySources.FromActual)
            return OpResult.Fail("أمر الاستلام لا يُنشأ إلا من أمر تسليم نازل من الإنتاج الفعلي.");

        var sourceExecution = Db.ProductionExecutions.AsNoTracking().FirstOrDefault(e => e.Id == delivery.SourceId);
        if (sourceExecution == null || sourceExecution.OrderId != orderId)
            return OpResult.Fail("أمر التسليم لا يرتبط بالتنفيذ الفعلي لأمر الإنتاج المحدد.");
        if (delivery.Status == DocStatuses.Draft) return OpResult.Fail("أمر التسليم مسودة — يجب تحريره من مدير الإنتاج أولاً.");
        if (delivery.Status == DocStatuses.Cancelled) return OpResult.Fail("أمر التسليم ملغى.");
        if (delivery.Status == DocStatuses.Completed) return OpResult.Fail("أمر التسليم مستلم بالكامل مسبقاً.");
        var delOrders = delivery.Items.Where(i => i.OrderId != null).Select(i => i.OrderId.Value).Distinct().ToList();
        if (!delOrders.Contains(orderId)) return OpResult.Fail("الأمر المحدد ليس من أوامر أمر التسليم المحدد.");
        if (delivery.Status != DocStatuses.Issued)
            return OpResult.Fail("أمر التسليم ليس محرراً للمخزن.");
        if (Db.FinishedGoodsReceipts.AsNoTracking().Any(r => r.DeliveryId == deliveryId.Value
            && (r.Status == DocStatuses.Draft || r.Status == DocStatuses.Issued)))
            return OpResult.Fail("يوجد سند استلام مسودة أو مُصدر لهذا الأمر بالفعل — افتحه وأكمله أو ألغِه قبل إنشاء سند آخر.");

        return RunOp(() =>
        {
            // لا تعتمد المعاملة على نسخة الرأس التي فُحصت قبل فتحها: أعد قراءة
            // أمر التسليم وحالته وبنوده داخل المعاملة حتى لا يمر الإلغاء المتزامن.
            Db.ChangeTracker.Clear();
            var currentDelivery = Db.ProductionDeliveries.AsNoTracking().Include(d => d.Items)
                .FirstOrDefault(d => d.Id == deliveryId.Value)
                ?? throw new DomainException("أمر تسليم الإنتاج غير موجود.", "DELIVERY_MISSING");
            if (currentDelivery.SourceType != DeliverySources.FromActual)
                throw new DomainException("أمر الاستلام لا يُنشأ إلا من أمر تسليم نازل من الإنتاج الفعلي.", "DELIVERY_SOURCE");
            if (currentDelivery.Status == DocStatuses.Cancelled)
                throw new DomainException("أمر التسليم ملغى.", "DELIVERY_CANCELLED");
            if (currentDelivery.Status == DocStatuses.Completed)
                throw new DomainException("أمر التسليم مستلم بالكامل مسبقاً.", "DELIVERY_COMPLETED");
            if (currentDelivery.Status != DocStatuses.Issued)
                throw new DomainException("أمر التسليم ليس محرراً للمخزن.", "DELIVERY_NOT_ISSUED");
            if (!currentDelivery.Items.Any(i => i.OrderId == orderId))
                throw new DomainException("الأمر المحدد ليس من أوامر أمر التسليم المحدد.", "ORDER_MISMATCH");
            if (Db.FinishedGoodsReceipts.AsNoTracking().Any(r => r.DeliveryId == deliveryId.Value
                && (r.Status == DocStatuses.Draft || r.Status == DocStatuses.Issued)))
                throw new DomainException("يوجد سند استلام مسودة أو مُصدر لهذا الأمر بالفعل — افتحه وأكمله أو ألغِه قبل إنشاء سند آخر.", "DUPLICATE_OPEN_RECEIPT");
            delivery = currentDelivery;

            var rcpt = new FinishedGoodsReceipt
            {
                DocumentNumber = Numbering.Next("FGR"),
                OrderId = orderId,
                QualityCheckId = qualityCheckId,
                DeliveryId = deliveryId,
                DeliveryDate = UiFormat.TryParseDate(deliveryDate, out var d) ? d : DateTime.Now,
                WarehouseId = WarehouseId("WFG"),
                Status = DocStatuses.Draft,
                ReceiptStatus = "None"
            };
            // §B96 — حارس التكرار يمنع بندين بنفس (الصنف + الدفعة) في سند واحد: رفض مبكر برسالة واضحة
            // (لعملاء مختلفين على نفس الدفعة: استلم كل بند تسليم في سند مستقل — فالترقيم مختلف ولا تعارض)
            var dupLine = items.GroupBy(i => new { i.ProductId, i.LotId, i.PackagingTypeId, i.DeliveryItemId }).FirstOrDefault(g => g.Count() > 1);
            if (dupLine != null)
                throw new DomainException(
                    "⛔ بندَان مكرران لنفس هوية الصنف/الدفعة/العبوة في سند واحد — وحّدهما في بند واحد.\n" +
                    "لعملاء مختلفين على نفس الدفعة: استلم كل بند تسليم في سند مستقل.",
                    "DUP_LINE");
            foreach (var it in items)
            {
                // §PRD-01 — بند التسليم هو المصدر الوحيد لهوية الصنف/المخرج والعبوة/الكراتين.
                int? effCust = null;
                int? effLine = null;
                if (it.DeliveryItemId == null)
                    throw new DomainException("حدد بند أمر التسليم لكل صنف في السند المربوط.", "NO_DELIVERY_LINE");
                var line = delivery.Items.FirstOrDefault(l => l.Id == it.DeliveryItemId.Value)
                    ?? throw new DomainException("بند التسليم غير تابع لأمر التسليم المحدد.", "NO_DELIVERY_LINE");
                if (line.ProductId != it.ProductId)
                    throw new DomainException("الصنف لا يطابق بند أمر التسليم المحدد.", "LINE_MISMATCH");
                UnitsPolicy.RequireItemType(Db, it.ProductId, "Finished", "استلام الإنتاج التام");
                if (it.LotId != null && it.LotId != line.LotId)
                    throw new DomainException("الدفعة لا تطابق بند أمر التسليم المحدد.", "LOT_MISMATCH");
                if (it.LotId == null) it.LotId = line.LotId;
                if (it.CustomerId != null && it.CustomerId != line.CustomerId)
                    throw new DomainException("العميل لا يطابق عميل بند أمر التسليم المحدد.", "CUSTOMER_MISMATCH");
                if (it.PackagingTypeId != line.PackagingTypeId)
                    throw new DomainException("العبوة لا تطابق عبوة بند أمر التسليم المحدد.", "PACKAGING_MISMATCH");
                if (!double.IsFinite(it.NetWeightKg) || it.NetWeightKg <= 0)
                    throw new DomainException("كمية بند الاستلام يجب أن تكون أكبر من صفر.", "RECEIPT_QTY_INVALID");
                // افحص سقف الكمية أولاً حتى تظهر رسالة المتبقي الصحيحة قبل أي تحقق للكراتين.
                double lineRemaining = Math.Max(0, line.QtyKg - line.ReceivedQtyKg);
                if (it.NetWeightKg > lineRemaining + 0.001)
                    throw new DomainException(
                        $"⛔ كمية البند ({it.NetWeightKg:N1} كجم) تتجاوز المتبقي في بند أمر التسليم ({lineRemaining:N1} كجم).",
                        "OVER_DELIVERY");
                if (line.PackageCount <= 0 || line.QtyKg <= 0.001)
                    throw new DomainException("بند أمر التسليم لا يحمل عدد كراتين ووزناً صالحين لتوزيع الاستلام.", "PACKAGE_COUNT_REQUIRED");

                int priorCartonTarget = (int)Math.Round(line.PackageCount * line.ReceivedQtyKg / line.QtyKg,
                    MidpointRounding.AwayFromZero);
                int currentCartonTarget = (int)Math.Round(line.PackageCount * (line.ReceivedQtyKg + it.NetWeightKg) / line.QtyKg,
                    MidpointRounding.AwayFromZero);
                priorCartonTarget = Math.Clamp(priorCartonTarget, 0, line.PackageCount);
                currentCartonTarget = Math.Clamp(currentCartonTarget, priorCartonTarget, line.PackageCount);
                int distributedCartons = Math.Max(0, currentCartonTarget - priorCartonTarget);
                if (distributedCartons <= 0)
                    throw new DomainException("كمية الاستلام أصغر من أن تقابل كرتوناً واحداً وفق توزيع بند التسليم.", "PACKAGE_DISTRIBUTION_ZERO");
                int receiptPackageCount = it.PackageCount > 0 ? it.PackageCount : distributedCartons;
                if (receiptPackageCount > line.PackageCount - priorCartonTarget)
                    throw new DomainException("عدد كراتين سند الاستلام يتجاوز المتبقي في بند أمر التسليم.", "PACKAGE_COUNT_OVER");
                UnitsPolicy.RequireCartonWeight(Db, it.ProductId, it.PackagingTypeId, receiptPackageCount, "استلام الإنتاج التام");
                double cartonWeight = UnitsPolicy.CartonWeight(Db, it.ProductId, it.PackagingTypeId);
                int directCartons = cartonWeight > 0
                    ? (int)Math.Round(it.NetWeightKg / cartonWeight, MidpointRounding.AwayFromZero)
                    : 0;
                if (it.PackageCount <= 0 && distributedCartons > 0)
                {
                    receiptPackageCount = distributedCartons;
                }
                double expectedKg = Math.Round(receiptPackageCount * cartonWeight, 1);
                if (cartonWeight > 0 && it.PackageCount <= 0 && Math.Abs(it.NetWeightKg - expectedKg) > Math.Max(1.0, it.NetWeightKg * 0.05))
                    throw new DomainException(
                        $"وزن السند ({it.NetWeightKg:N1} كجم) لا يتوافق مع {receiptPackageCount:N0} كرتوناً من العبوة المحددة ({expectedKg:N1} كجم) ضمن هامش التشغيل المسموح.",
                        "PACKAGE_WEIGHT_MISMATCH");
                int targetWarehouse = it.WarehouseId ?? line.WarehouseId ?? rcpt.WarehouseId;
                if (!Db.Warehouses.AsNoTracking().Any(w => w.Id == targetWarehouse && w.IsActive))
                    throw new DomainException("مخزن استلام البند غير موجود أو موقوف.", "WAREHOUSE_INVALID");
                effCust = line.CustomerId;
                effLine = line.Id;

                rcpt.Items.Add(new FinishedGoodsReceiptItem
                {
                    ProductId = it.ProductId,
                    LotId = it.LotId,
                    CustomerId = effCust,
                    WarehouseId = targetWarehouse,
                    DeliveryItemId = effLine,
                    PackagingTypeId = it.PackagingTypeId,
                    PackageCount = receiptPackageCount,
                    NetWeightKg = it.NetWeightKg,
                    ReceivedQtyKg = 0,
                    // §القاعدة 7: وزن الكرتون وقت الاستلام — لا يتغير بتعريف العبوة لاحقاً
                    CartonWeightKg = UnitsPolicy.CartonWeight(Db, it.ProductId, it.PackagingTypeId)
                });
            }
            Db.FinishedGoodsReceipts.Add(rcpt);
            Db.SaveChanges();
            // الإصدار لا يمس الأرصدة؛ الترحيل الفعلي يتم لاحقاً من إجراء الاستلام فقط.
            rcpt.CreatedBy = Session?.UserId;
            return OpResult.Success($"تم إنشاء أمر استلام الإنتاج {rcpt.DocumentNumber} من أمر التسليم المحرر {delivery.DocumentNumber} — أصدره ثم نفّذ الاستلام (المستخدم: {Session?.UserName} — التاريخ: {DateTime.Now:dd/MM/yyyy}).", rcpt.Id, rcpt.DocumentNumber);
        });
    }

    /// <summary>
    /// مصادر مخزن التام: جلسات الإقفال الأخيرة لكل أمر، أو محاضر الفحص المعتمدة.
    /// لا تُعرض كمية قابلة للترحيل قبل اعتماد فحص مرتبط بالتنفيذ المغلق.
    /// </summary>
    public List<FinishedGoodsReceiptSourceDto> GetDirectReceiptSources(string sourceType)
    {
        Require("finishedgoods", "View");
        if (sourceType != DeliverySources.FromActual && sourceType != DeliverySources.FromCheck)
            throw new DomainException("نوع مصدر الاستلام المباشر غير معروف.");
        return BuildDirectReceiptSources(sourceType, null);
    }

    private List<FinishedGoodsReceiptSourceDto> BuildDirectReceiptSources(string sourceType, int? excludeReceiptId)
    {
        var orderRows = Db.ProductionOrders.AsNoTracking().Where(o => o.Status != DocStatuses.Cancelled)
            .Select(o => new { o.Id, o.DocumentNumber, o.CustomerId }).ToList();
        var orderMap = orderRows.ToDictionary(o => o.Id);
        var orderIds = orderRows.Select(o => o.Id).ToList();
        var executions = Db.ProductionExecutions.AsNoTracking()
            .Where(e => e.Status == DocStatuses.Completed && e.IsDayClosed && e.EndDateTime != null && orderIds.Contains(e.OrderId))
            .OrderBy(e => e.EndDateTime).ThenBy(e => e.Id).ToList();
        if (executions.Count == 0) return new();
        var executionIds = executions.Select(e => e.Id).ToList();
        var executionItems = Db.ProductionExecutionItems.AsNoTracking().Where(i => executionIds.Contains(i.ExecutionId)).ToList();
        var qualityChecks = Db.QualityChecks.AsNoTracking().Include(q => q.Items)
            .Where(q => q.ExecutionId != null && executionIds.Contains(q.ExecutionId.Value))
            .OrderByDescending(q => q.Id).ToList()
            .GroupBy(q => q.ExecutionId.Value).ToDictionary(g => g.Key, g => g.First());
        var orderItems = Db.ProductionOrderItems.AsNoTracking().Where(i => orderIds.Contains(i.OrderId)).ToList();
        var orderItemMap = orderItems.ToDictionary(i => i.Id);
        var finishedProductIds = orderItems.Select(i => i.ProductId).Distinct().ToList();
        var products = Db.Products.AsNoTracking().Where(p => finishedProductIds.Contains(p.Id)).ToList().ToDictionary(p => p.Id);
        var lots = Db.Lots.AsNoTracking().ToList().ToDictionary(l => l.Id);
        var packages = Db.PackagingTypes.AsNoTracking().ToList().ToDictionary(p => p.Id);
        var customers = Db.Customers.AsNoTracking().ToList().ToDictionary(c => c.Id);
        var activeReceipts = Db.FinishedGoodsReceipts.AsNoTracking().Include(r => r.Items)
            .Where(r => r.Status != DocStatuses.Cancelled && orderIds.Contains(r.OrderId)
                && (excludeReceiptId == null || r.Id != excludeReceiptId.Value)).ToList();
        var receiptDeliveryIds = activeReceipts.Where(r => r.DeliveryId != null).Select(r => r.DeliveryId.Value).Distinct().ToList();
        var deliverySources = receiptDeliveryIds.Count == 0 ? new Dictionary<int, int>()
            : Db.ProductionDeliveries.AsNoTracking().Where(d => receiptDeliveryIds.Contains(d.Id) && d.SourceType == DeliverySources.FromActual)
                .Select(d => new { d.Id, d.SourceId }).ToList().ToDictionary(d => d.Id, d => d.SourceId);
        var firstExecutionByOrder = executions.GroupBy(e => e.OrderId).ToDictionary(g => g.Key, g => g.First().Id);

        var executionByProducts = Db.ExecutionByProducts.AsNoTracking().Where(b => executionIds.Contains(b.ExecutionId)).ToList();
        var byProductIds = executionByProducts.Select(b => b.ByProductId).Distinct().ToList();
        var byProductDefinitions = Db.ByProducts.AsNoTracking().Where(b => byProductIds.Contains(b.Id)).ToList().ToDictionary(b => b.Id);
        var mappedProductIds = byProductDefinitions.Values.Where(b => b.ProductId != null).Select(b => b.ProductId.Value).Distinct().ToList();
        var mappedProducts = Db.Products.AsNoTracking().Where(p => mappedProductIds.Contains(p.Id)).ToList().ToDictionary(p => p.Id);
        var rawProductIds = executionByProducts.Where(b => b.RawProductId != null).Select(b => b.RawProductId.Value).Distinct().ToList();
        var rawProducts = Db.Products.AsNoTracking().Where(p => rawProductIds.Contains(p.Id)).ToList().ToDictionary(p => p.Id);

        List<FinishedGoodsReceipt> ReceiptsForExecution(int orderId, int executionId)
        {
            bool legacyFallback = firstExecutionByOrder.TryGetValue(orderId, out var first) && first == executionId;
            return activeReceipts.Where(r => r.OrderId == orderId &&
                (r.ProductionExecutionId == executionId
                    || (r.DeliveryId is int deliveryId && deliverySources.TryGetValue(deliveryId, out var sourceId) && sourceId == executionId)
                    || (legacyFallback && r.ProductionExecutionId == null && r.DeliveryId == null))).ToList();
        }
        static double ClaimedKg(FinishedGoodsReceipt receipt, FinishedGoodsReceiptItem item)
            => receipt.Status == DocStatuses.Draft || receipt.Status == DocStatuses.Issued ? item.NetWeightKg : item.ReceivedQtyKg;
        static int ClaimedCartons(FinishedGoodsReceipt receipt, FinishedGoodsReceiptItem item)
            => receipt.Status == DocStatuses.Draft || receipt.Status == DocStatuses.Issued
                ? item.PackageCount
                : (item.NetWeightKg > 0 ? (int)Math.Round(item.PackageCount * item.ReceivedQtyKg / item.NetWeightKg, MidpointRounding.AwayFromZero) : 0);

        var result = new List<FinishedGoodsReceiptSourceDto>();
        foreach (var execution in executions)
        {
            var order = orderMap[execution.OrderId];
            bool directRoute = execution.WarehouseRoute == ProductionWarehouseRoutes.Direct;
            bool qualityRoute = execution.WarehouseRoute == ProductionWarehouseRoutes.Quality || execution.WarehouseRoute == null;
            if (sourceType == DeliverySources.FromActual && !directRoute) continue;
            if (sourceType == DeliverySources.FromCheck && !qualityRoute) continue;
            qualityChecks.TryGetValue(execution.Id, out var check);
            bool approved = check != null && check.IsApproved && check.Status == DocStatuses.Approved;
            if (sourceType == DeliverySources.FromCheck && !approved) continue;
            var sourceLines = new List<FinishedGoodsReceiptSourceLineDto>();
            var executionReceipts = ReceiptsForExecution(order.Id, execution.Id);

            if (sourceType == DeliverySources.FromActual)
            {
                foreach (var actual in executionItems.Where(i => i.ExecutionId == execution.Id && i.ActualQtyKg > 0))
                {
                    double producedKg = (double)actual.ActualQtyKg;
                    var prior = executionReceipts.SelectMany(r => r.Items.Select(i => new { Receipt = r, Item = i }))
                        .Where(x => x.Item.ByProductId == null && x.Item.ProductId == actual.ProductId
                            && x.Item.LotId == actual.LotId && x.Item.PackagingTypeId == actual.PackagingTypeId
                            && x.Item.CustomerId == actual.CustomerId
                            && (x.Item.OrderItemId == actual.OrderItemId || x.Item.OrderItemId == null)).ToList();
                    double claimedKg = prior.Sum(x => ClaimedKg(x.Receipt, x.Item));
                    int claimedCartons = prior.Sum(x => ClaimedCartons(x.Receipt, x.Item));
                    double availableKg = Math.Max(0, producedKg - claimedKg);
                    int availableCartons = Math.Max(0, actual.ActualCartons - claimedCartons);
                    if (availableKg <= 0.001) continue;
                    string block = null;
                    var product = products.GetValueOrDefault(actual.ProductId);
                    double cartonWeight = orderItemMap.TryGetValue(actual.OrderItemId, out var sourceOrderItem)
                        ? sourceOrderItem.CartonWeightKg : 0;
                    if (cartonWeight <= 0) cartonWeight = UnitsPolicy.CartonWeight(Db, actual.ProductId, actual.PackagingTypeId);
                    if (product == null || !product.IsActive || product.ItemType != "Finished") block = "الصنف غير نشط أو ليس منتجاً تاماً من المجموعة 002.";
                    else if (cartonWeight <= 0) block = "وزن الكرتون غير معرّف لهذا البند.";
                    else if (availableCartons <= 0) block = "لا توجد كراتين فعلية متبقية للاستلام.";
                    else availableKg = Math.Min(availableKg, availableCartons * cartonWeight);
                    sourceLines.Add(new FinishedGoodsReceiptSourceLineDto
                    {
                        OrderItemId = actual.OrderItemId, ProductId = actual.ProductId,
                        ProductName = product?.ProductNameAr ?? $"#{actual.ProductId}", LotId = actual.LotId,
                        LotCode = actual.LotId is int lotId && lots.TryGetValue(lotId, out var lot) ? lot.LotCode : "—",
                        PackagingTypeId = actual.PackagingTypeId, CartonWeightKg = cartonWeight,
                        PackagingName = actual.PackagingTypeId is int packId && packages.TryGetValue(packId, out var pack) ? pack.PackageNameAr : "—",
                        CustomerId = actual.CustomerId ?? order.CustomerId,
                        CustomerName = (actual.CustomerId ?? order.CustomerId) is int custId && customers.TryGetValue(custId, out var customer) ? customer.CustomerName : "—",
                        AvailableQtyKg = Math.Round(availableKg, 4), AvailableCartons = availableCartons,
                        IsByProduct = false, Unit = "كرتون", BlockReason = block
                    });
                }
            }
            else if (approved)
            {
                foreach (var q in check.Items.Where(i => i.AcceptedQtyKg > 0.001))
                {
                    var prior = executionReceipts.SelectMany(r => r.Items.Select(i => new { Receipt = r, Item = i }))
                        .Where(x => x.Item.ByProductId == null && x.Item.ProductId == q.ProductId
                            && x.Item.LotId == q.LotId && x.Item.PackagingTypeId == q.PackagingTypeId
                            && x.Item.CustomerId == (q.CustomerId ?? order.CustomerId)
                            && ((q.OrderItemId != null && x.Item.OrderItemId == q.OrderItemId) || x.Item.OrderItemId == null)).ToList();
                    double claimedKg = prior.Sum(x => ClaimedKg(x.Receipt, x.Item));
                    int claimedCartons = prior.Sum(x => ClaimedCartons(x.Receipt, x.Item));
                    double availableKg = Math.Max(0, q.AcceptedQtyKg - claimedKg);
                    int acceptedCartons = Math.Max(0, (int)Math.Round(q.AcceptedCartons, MidpointRounding.AwayFromZero));
                    int availableCartons = Math.Max(0, acceptedCartons - claimedCartons);
                    if (availableKg <= 0.001) continue;
                    string block = null;
                    var product = products.GetValueOrDefault(q.ProductId);
                    double cartonWeight = q.OrderItemId is int orderItemId && orderItemMap.TryGetValue(orderItemId, out var sourceOrderItem)
                        ? sourceOrderItem.CartonWeightKg : 0;
                    if (cartonWeight <= 0) cartonWeight = UnitsPolicy.CartonWeight(Db, q.ProductId, q.PackagingTypeId);
                    if (q.OrderItemId == null) block = "محضر الجودة غير مربوط ببند أمر إنتاج محدد.";
                    else if (product == null || !product.IsActive || product.ItemType != "Finished") block = "الصنف غير نشط أو ليس منتجاً تاماً من المجموعة 002.";
                    else if (cartonWeight <= 0) block = "وزن الكرتون غير معرّف لهذا البند.";
                    else if (availableCartons <= 0) block = "المتبقي لا يقابل كرتوناً مقبولاً قابلاً للاستلام.";
                    else availableKg = Math.Min(availableKg, availableCartons * cartonWeight);
                    sourceLines.Add(new FinishedGoodsReceiptSourceLineDto
                    {
                        OrderItemId = q.OrderItemId, ProductId = q.ProductId,
                        ProductName = product?.ProductNameAr ?? $"#{q.ProductId}", LotId = q.LotId,
                        LotCode = q.LotId is int lotId && lots.TryGetValue(lotId, out var lot) ? lot.LotCode : "—",
                        PackagingTypeId = q.PackagingTypeId, CartonWeightKg = cartonWeight,
                        PackagingName = q.PackagingTypeId is int packId && packages.TryGetValue(packId, out var pack) ? pack.PackageNameAr : "—",
                        CustomerId = q.CustomerId ?? order.CustomerId,
                        CustomerName = (q.CustomerId ?? order.CustomerId) is int custId && customers.TryGetValue(custId, out var customer) ? customer.CustomerName : "—",
                        AvailableQtyKg = Math.Round(availableKg, 4), AvailableCartons = availableCartons,
                        IsByProduct = false, Unit = "كرتون", BlockReason = block
                    });
                }
            }

            var outputGroups = executionByProducts.Where(b => b.ExecutionId == execution.Id)
                .GroupBy(b => new { b.ByProductId, b.SourceOrderItemId, b.RawProductId, b.RawLotId, b.CustomerId });
            foreach (var group in outputGroups)
            {
                double produced = (double)group.Sum(x => x.Qty);
                if (produced <= 0.001) continue;
                var key = group.Key;
                var bp = byProductDefinitions.GetValueOrDefault(key.ByProductId);
                int? customerId = key.CustomerId ?? order.CustomerId;
                var prior = executionReceipts.SelectMany(r => r.Items.Select(i => new { Receipt = r, Item = i }))
                    .Where(x => x.Item.ByProductId == key.ByProductId && x.Item.SourceOrderItemId == key.SourceOrderItemId
                        && x.Item.RawProductId == key.RawProductId && x.Item.RawLotId == key.RawLotId
                        && x.Item.CustomerId == customerId).ToList();
                double claimed = prior.Sum(x => ClaimedKg(x.Receipt, x.Item));
                double available = Math.Max(0, produced - claimed);
                if (available <= 0.001) continue;
                string block = null;
                int mappedProductId = bp?.ProductId ?? 0;
                if (key.SourceOrderItemId == null || key.RawProductId == null || key.RawLotId == null || customerId == null)
                    block = "يلزم إكمال ربط المخرج الثانوي ببند الخام والدفعة والعميل.";
                else if (bp?.ProductId is not int mappedId || !mappedProducts.TryGetValue(mappedId, out var mapped)
                    || !mapped.IsActive || mapped.ItemType != "ByProduct" || mapped.GroupCode != UnitsPolicy.GroupByProduct
                    || mapped.UnitOfMeasure != UnitsPolicy.UnitKg)
                    block = "اربط تعريف المخرج الثانوي بصنف مخزني نشط من المجموعة 003 ووحدته كجم.";
                else mappedProductId = mappedId;
                var rawName = key.RawProductId is int rawId && rawProducts.TryGetValue(rawId, out var raw) ? raw.ProductNameAr : "—";
                var lotCode = key.RawLotId is int rawLotId && lots.TryGetValue(rawLotId, out var rawLot) ? rawLot.LotCode : "—";
                sourceLines.Add(new FinishedGoodsReceiptSourceLineDto
                {
                    OrderItemId = key.SourceOrderItemId, SourceOrderItemId = key.SourceOrderItemId,
                    ByProductId = key.ByProductId, ProductId = mappedProductId, ProductName = bp?.ByProductNameAr ?? $"مخرج #{key.ByProductId}",
                    LotId = null, LotCode = $"خام: {rawName} — دفعة {lotCode}", CustomerId = customerId,
                    CustomerName = customerId is int custId && customers.TryGetValue(custId, out var customer) ? customer.CustomerName : "—",
                    AvailableQtyKg = Math.Round(available, 4), AvailableCartons = 0,
                    RawProductId = key.RawProductId, RawLotId = key.RawLotId,
                    IsByProduct = true, Unit = "كجم", BlockReason = block
                });
            }

            // لا نُبقي إقفالات استُنفد رصيدها أو لم يبق فيها بند صالح في قائمة الاختيار؛
            // تظهر فقط المصادر القابلة للاستلام بكمية موجبة كي لا تتكرر أوامر «متاح 0 كجم».
            var availableTotal = sourceLines.Where(l => string.IsNullOrWhiteSpace(l.BlockReason)).Sum(l => l.AvailableQtyKg);
            if (availableTotal <= 0.001) continue;
            bool qualityApproved = approved;
            string blockReason = availableTotal > 0.001 ? null :
                sourceLines.Any(i => !string.IsNullOrWhiteSpace(i.BlockReason)) ? sourceLines.First(i => !string.IsNullOrWhiteSpace(i.BlockReason)).BlockReason
                : "لا توجد كمية متبقية قابلة للاستلام لهذا الإقفال.";
            string sourceLabel = sourceType == DeliverySources.FromActual
                ? $"إقفال مباشر {execution.DocumentNumber} — أمر {order.DocumentNumber} — متاح {availableTotal:N1} كجم"
                : $"محضر معتمد {check?.DocumentNumber} — إقفال {execution.DocumentNumber} — أمر {order.DocumentNumber} — متاح {availableTotal:N1} كجم";
            result.Add(new FinishedGoodsReceiptSourceDto
            {
                OrderId = order.Id, ExecutionId = execution.Id, QualityCheckId = sourceType == DeliverySources.FromCheck ? check?.Id : null,
                SourceType = sourceType, OrderNumber = order.DocumentNumber, ExecutionNumber = execution.DocumentNumber,
                QualityNumber = check?.DocumentNumber ?? "—",
                CustomerName = order.CustomerId is int orderCustomerId && customers.TryGetValue(orderCustomerId, out var orderCustomer) ? orderCustomer.CustomerName : "—",
                Label = sourceLabel, QualityApproved = qualityApproved,
                CanReceive = availableTotal > 0.001, BlockReason = blockReason,
                AvailableQtyKg = availableTotal, Items = sourceLines
            });
        }
        return result.OrderByDescending(x => x.ExecutionId).ToList();
    }

    /// <summary>حفظ سند الاستلام المباشر وترحيله ذرياً؛ أي فشل في الفحص أو المخزون يعيد المعاملة كاملة.</summary>
    public OpResult SaveAndReceiveDirect(int orderId, int executionId, int? qualityCheckId, string sourceType,
        string deliveryDate, List<FinishedGoodsItemDto> items)
    {
        Require("finishedgoods", "Create");
        if (items == null || items.Count == 0) return OpResult.Fail("أدخل كمية واحدة على الأقل للاستلام.");
        if (sourceType != DeliverySources.FromActual && sourceType != DeliverySources.FromCheck)
            return OpResult.Fail("مصدر الاستلام غير صحيح.");
        return RunOp(() =>
        {
            Db.ChangeTracker.Clear();
            var source = BuildDirectReceiptSources(sourceType, null)
                .FirstOrDefault(x => x.OrderId == orderId && x.ExecutionId == executionId && x.QualityCheckId == qualityCheckId);
            if (source == null)
                throw new DomainException("مصدر الاستلام غير متاح أو لم يكتمل مساره المختار.", "SOURCE_NOT_READY");
            if (sourceType == DeliverySources.FromCheck && !source.QualityApproved)
                throw new DomainException("محضر الجودة غير معتمد؛ لا يمكن الترحيل.", "QUALITY_NOT_APPROVED");
            if (!source.CanReceive)
                throw new DomainException(source.BlockReason ?? "لا توجد كمية قابلة للاستلام.", "NO_RECEIPT_REMAINING");
            if (items.Any(i => i == null || !double.IsFinite(i.NetWeightKg) || i.NetWeightKg <= 0 || !double.IsFinite(i.ReceivedQtyKg ?? 0)))
                throw new DomainException("كل كمية استلام يجب أن تكون موجبة وصالحة.", "RECEIPT_QTY_INVALID");
            var identities = items.GroupBy(i => new { i.OrderItemId, i.ByProductId, i.SourceOrderItemId, i.RawProductId, i.RawLotId, i.CustomerId, i.ProductId, i.LotId, i.PackagingTypeId })
                .FirstOrDefault(g => g.Count() > 1);
            if (identities != null) throw new DomainException("يوجد بند مكرر في سند واحد — ادمج كميته في بند واحد.", "DUP_LINE");
            var order = Db.ProductionOrders.AsNoTracking().FirstOrDefault(o => o.Id == orderId)
                ?? throw new DomainException("أمر الإنتاج غير موجود.", "ORDER_MISSING");
            var receipt = new FinishedGoodsReceipt
            {
                DocumentNumber = Numbering.Next("FGR"), OrderId = orderId, QualityCheckId = qualityCheckId,
                ProductionExecutionId = executionId, SourceType = sourceType, DeliveryId = null,
                DeliveryDate = UiFormat.TryParseDate(deliveryDate, out var date) ? date : DateTime.Now,
                WarehouseId = WarehouseId("WFG"), Status = DocStatuses.Draft, ReceiptStatus = "None"
            };
            foreach (var input in items)
            {
                var sourceLine = source.Items.FirstOrDefault(l =>
                    l.IsByProduct == (input.ByProductId != null)
                    && (input.ByProductId != null
                        ? l.ByProductId == input.ByProductId && l.SourceOrderItemId == input.SourceOrderItemId
                            && l.RawProductId == input.RawProductId && l.RawLotId == input.RawLotId && l.CustomerId == input.CustomerId
                        : l.OrderItemId == input.OrderItemId && l.ProductId == input.ProductId));
                if (sourceLine == null) throw new DomainException("بند الاستلام لا يطابق بنداً متاحاً من مصدر الإنتاج/الفحص.", "SOURCE_LINE_MISMATCH");
                if (!string.IsNullOrWhiteSpace(sourceLine.BlockReason)) throw new DomainException(sourceLine.BlockReason, "SOURCE_LINE_BLOCKED");
                if (input.NetWeightKg > sourceLine.AvailableQtyKg + 0.001)
                    throw new DomainException($"كمية الاستلام {input.NetWeightKg:N1} كجم تتجاوز المتبقي المقبول للمصدر ({sourceLine.AvailableQtyKg:N1} كجم).", "QUALITY_CAP_EXCEEDED");
                int targetWarehouse = input.WarehouseId ?? receipt.WarehouseId;
                if (!Db.Warehouses.AsNoTracking().Any(w => w.Id == targetWarehouse && w.IsActive))
                    throw new DomainException("مخزن استلام البند غير موجود أو موقوف.", "WAREHOUSE_INVALID");
                double cartonWeight = 0;
                int packages = input.PackageCount;
                int? lotId = input.LotId;
                int? packagingId = input.PackagingTypeId;
                if (sourceLine.IsByProduct)
                {
                    if (input.ProductId != sourceLine.ProductId || input.PackageCount != 0 || input.PackagingTypeId != null)
                        throw new DomainException("المخرج الثانوي يجب أن يرحّل إلى الصنف المخزني المرتبط، بالكيلو فقط ومن دون عبوات.", "BYPRODUCT_STOCK_ITEM");
                    UnitsPolicy.RequireItemType(Db, input.ProductId, "ByProduct", "استلام المخرجات الثانوية");
                    lotId = null;
                    packagingId = null;
                    packages = 0;
                }
                else
                {
                    if (input.ProductId != sourceLine.ProductId || input.LotId != sourceLine.LotId
                        || input.PackagingTypeId != sourceLine.PackagingTypeId || input.CustomerId != sourceLine.CustomerId)
                        throw new DomainException("الصنف أو الدفعة أو العميل أو العبوة لا يطابق بند الفحص المعتمد.", "QUALITY_LINE_MISMATCH");
                    if (input.OrderItemId != sourceLine.OrderItemId)
                        throw new DomainException("بند أمر الإنتاج لا يطابق بند الفحص المعتمد.", "ORDER_ITEM_MISMATCH");
                    UnitsPolicy.RequireItemType(Db, input.ProductId, "Finished", "استلام الإنتاج التام");
                    if (packages <= 0 || packages > sourceLine.AvailableCartons)
                        throw new DomainException($"عدد الكراتين يجب أن يكون بين 1 و{sourceLine.AvailableCartons:N0} كرتوناً مقبولاً.", "PACKAGE_COUNT_OVER");
                    cartonWeight = sourceLine.CartonWeightKg > 0 ? sourceLine.CartonWeightKg
                        : UnitsPolicy.CartonWeight(Db, input.ProductId, packagingId);
                    UnitsPolicy.RequireCartonWeight(Db, input.ProductId, packagingId, packages, "استلام الإنتاج التام");
                    double expectedKg = Math.Round(packages * cartonWeight, 1);
                    if (Math.Abs(input.NetWeightKg - expectedKg) > Math.Max(1.0, input.NetWeightKg * 0.02))
                        throw new DomainException($"الكمية {input.NetWeightKg:N1} كجم لا تتوافق مع {packages:N0} كرتوناً ({expectedKg:N1} كجم).", "PACKAGE_WEIGHT_MISMATCH");
                }
                receipt.Items.Add(new FinishedGoodsReceiptItem
                {
                    ProductId = input.ProductId, LotId = lotId, PackagingTypeId = packagingId,
                    PackageCount = packages, NetWeightKg = input.NetWeightKg, ReceivedQtyKg = 0,
                    CustomerId = input.CustomerId ?? sourceLine.CustomerId, WarehouseId = targetWarehouse,
                    OrderItemId = input.OrderItemId, ByProductId = input.ByProductId,
                    RawProductId = input.RawProductId, RawLotId = input.RawLotId,
                    SourceOrderItemId = input.SourceOrderItemId,
                    CartonWeightKg = sourceLine.IsByProduct ? 0 : cartonWeight
                });
            }
            Db.FinishedGoodsReceipts.Add(receipt);
            Db.SaveChanges();
            // الحفظ يرحّل للمخزون مباشرة، لكنه يظل بانتظار الاعتماد النهائي الذي يقفل السند.
            // نسجل حالة الإصدار داخلياً ثم نرحّل دون اعتماد حتى تتم خطوة الاعتماد من الزر المخصص.
            receipt.Status = DocStatuses.Issued;
            Db.SaveChanges();
            var nested = new FinishedGoodsService(Db, Session, Numbering) { JoinParentTransaction = true };
            var map = receipt.Items.ToDictionary(i => i.Id, i => i.NetWeightKg);
            var received = nested.Receive(receipt.Id, map, approve: false);
            if (!received.Ok) throw new DomainException(received.Message, "DIRECT_RECEIVE_FAILED");
            string postedReceiptNumber = Db.FinishedGoodsReceipts.AsNoTracking()
                .Where(r => r.Id == receipt.Id).Select(r => r.ReceiptNumber).FirstOrDefault();
            string postedDocumentNo = postedReceiptNumber ?? receipt.DocumentNumber;
            return OpResult.Success($"حُفظ سند الاستلام {postedDocumentNo} ورُحّلت الكميات المقبولة إلى المخزن مباشرة. السند بانتظار الاعتماد النهائي. {received.Message}", receipt.Id, postedDocumentNo);
        });
    }

    private bool IsApprovedDirectSource(FinishedGoodsReceipt receipt)
    {
        if (receipt == null || receipt.DeliveryId != null || receipt.ProductionExecutionId == null
            || (receipt.SourceType != DeliverySources.FromActual && receipt.SourceType != DeliverySources.FromCheck)) return false;
        var execution = Db.ProductionExecutions.AsNoTracking().FirstOrDefault(e => e.Id == receipt.ProductionExecutionId.Value
            && e.OrderId == receipt.OrderId && e.Status == DocStatuses.Completed && e.IsDayClosed && e.EndDateTime != null);
        if (execution == null) return false;
        if (receipt.SourceType == DeliverySources.FromActual)
            return execution.WarehouseRoute == ProductionWarehouseRoutes.Direct && receipt.QualityCheckId == null;
        return (execution.WarehouseRoute == ProductionWarehouseRoutes.Quality || execution.WarehouseRoute == null)
            && receipt.QualityCheckId is int checkId
            && Db.QualityChecks.AsNoTracking().Any(q => q.Id == checkId
                && q.ExecutionId == receipt.ProductionExecutionId.Value
                && q.IsApproved && q.Status == DocStatuses.Approved);
    }

    /// <summary>الإصدار إلى المخزن — لا يمس أي رصيد (§7).</summary>
    public OpResult Issue(int receiptId)
    {
        Require("finishedgoods", "Approve");
        var rcpt = Db.FinishedGoodsReceipts.FirstOrDefault(r => r.Id == receiptId);
        if (rcpt == null) return OpResult.Fail("أمر الاستلام غير موجود.");
        bool directSource = rcpt.DeliveryId == null && IsApprovedDirectSource(rcpt);
        if (!directSource && (rcpt.DeliveryId == null || !Db.ProductionDeliveries.AsNoTracking().Any(d => d.Id == rcpt.DeliveryId.Value
            && d.SourceType == DeliverySources.FromActual)))
            return OpResult.Fail("السند لا يرتبط بمصدر مباشر معتمد أو بأمر تسليم فعلي صحيح.");
        if (rcpt.Status == DocStatuses.Cancelled) return OpResult.Fail("أمر الاستلام ملغى.");
        if (rcpt.Status == DocStatuses.Issued) return OpResult.Fail("أمر الاستلام مُصدر مسبقاً.");

        return RunOp(() =>
        {
            Db.ChangeTracker.Clear();
            var currentRcpt = Db.FinishedGoodsReceipts.AsNoTracking().FirstOrDefault(r => r.Id == receiptId)
                ?? throw new DomainException("أمر الاستلام غير موجود.", "RECEIPT_MISSING");
            var currentDelivery = currentRcpt.DeliveryId != null
                ? Db.ProductionDeliveries.AsNoTracking().FirstOrDefault(d => d.Id == currentRcpt.DeliveryId.Value)
                : null;
            bool currentDirect = currentRcpt.DeliveryId == null && IsApprovedDirectSource(currentRcpt);
            if (!currentDirect && currentDelivery == null)
                throw new DomainException("مصدر الاستلام المباشر أو أمر التسليم المرتبط غير صالح.", "DELIVERY_MISSING");
            if (currentDelivery != null && currentDelivery.SourceType != DeliverySources.FromActual)
                throw new DomainException("لا يمكن إصدار استلام مصدره أمر تسليم قديم.", "DELIVERY_SOURCE");
            if (currentDelivery?.Status == DocStatuses.Cancelled)
                throw new DomainException("أمر التسليم المرتبط ملغى.", "DELIVERY_CANCELLED");
            if (currentDelivery?.Status == DocStatuses.Completed)
                throw new DomainException("أمر التسليم المرتبط مستلم بالكامل.", "DELIVERY_COMPLETED");
            if (currentRcpt.Status == DocStatuses.Cancelled)
                throw new DomainException("أمر الاستلام ملغى.", "RECEIPT_CANCELLED");
            if (currentRcpt.Status == DocStatuses.Issued)
                throw new DomainException("أمر الاستلام مُصدر مسبقاً.", "RECEIPT_ISSUED");
            if (currentDelivery != null && Db.FinishedGoodsReceipts.AsNoTracking().Any(r => r.Id != receiptId
                && r.DeliveryId == currentRcpt.DeliveryId
                && (r.Status == DocStatuses.Draft || r.Status == DocStatuses.Issued)))
                throw new DomainException("يوجد سند استلام آخر مفتوح لأمر التسليم نفسه؛ عالج السند المفتوح أولاً منعاً لتكرار الكميات.", "DUPLICATE_OPEN_RECEIPT");
            rcpt = Db.FinishedGoodsReceipts.First(r => r.Id == receiptId);
            rcpt.Status = DocStatuses.Issued;
            Db.SaveChanges();
            return OpResult.Success("تم إصدار أمر التسليم إلى المخزن — بانتظار سند الاستلام.");
        });
    }

    /// <summary>§7/§8 — الاستلام المخزني يرحّل الأصناف التامة والمخرجات الثانوية إلى المخزن المختار.</summary>
    public OpResult Receive(int receiptId, Dictionary<int, double> receivedByItemId, bool approve = true)
    {
        Require("finishedgoods", approve ? "Approve" : "Create");
        var rcpt = Db.FinishedGoodsReceipts.Include(r => r.Items).FirstOrDefault(r => r.Id == receiptId);
        if (rcpt == null) return OpResult.Fail("أمر الاستلام غير موجود.");
        bool directSource = rcpt.DeliveryId == null && IsApprovedDirectSource(rcpt);
        if (!directSource && (rcpt.DeliveryId == null || !Db.ProductionDeliveries.AsNoTracking().Any(d => d.Id == rcpt.DeliveryId.Value
            && d.SourceType == DeliverySources.FromActual)))
            return OpResult.Fail("لا يمكن ترحيل استلام بلا مصدر إقفال/فحص معتمد أو أمر تسليم فعلي.");
        if (rcpt.ReceiptStatus == "Full") return OpResult.Fail("السند منفذ بالكامل مسبقاً.");
        if (rcpt.Items.All(i => i.NetWeightKg - i.ReceivedQtyKg <= 0.001))
            return OpResult.Fail("لا توجد كمية متبقية للاستلام — لم يُحجز رقم سند متابعة.");
        if (rcpt.Status != DocStatuses.Issued && rcpt.Status != DocStatuses.Completed)
            return OpResult.Fail("لا يمكن الاستلام قبل إصدار أمر التسليم.");

        return RunOp(() =>
        {
            // PRD-03: أعد تحميل رأس السند والمصدر داخل المعاملة، لا تستخدم نسخة قبلية.
            Db.ChangeTracker.Clear();
            rcpt = Db.FinishedGoodsReceipts.Include(r => r.Items).FirstOrDefault(r => r.Id == receiptId)
                ?? throw new DomainException("أمر الاستلام غير موجود.", "RECEIPT_MISSING");
            var currentDelivery = rcpt.DeliveryId != null
                ? Db.ProductionDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == rcpt.DeliveryId.Value)
                : null;
            bool currentDirect = rcpt.DeliveryId == null && IsApprovedDirectSource(rcpt);
            if (!currentDirect && (currentDelivery == null || currentDelivery.SourceType != DeliverySources.FromActual))
                throw new DomainException("لا يمكن ترحيل استلام مصدره إقفال/فحص معتمد أو أمر تسليم فعلي.", "DELIVERY_SOURCE");
            if (currentDelivery?.Status == DocStatuses.Cancelled)
                throw new DomainException("أمر التسليم المرتبط ملغى.", "DELIVERY_CANCELLED");
            if (rcpt.ReceiptStatus == "Full")
                throw new DomainException("السند منفذ بالكامل مسبقاً.", "RECEIPT_FULL");
            if (rcpt.Status != DocStatuses.Issued && rcpt.Status != DocStatuses.Completed)
                throw new DomainException("لا يمكن الاستلام قبل إصدار أمر التسليم.", "RECEIPT_NOT_ISSUED");
            var whFg = rcpt.WarehouseId;
            var orderCust = Db.ProductionOrders.Where(o => o.Id == rcpt.OrderId).Select(o => o.CustomerId).FirstOrDefault();
            double totalReceived = 0;
            // لا نحجز رقماً ولا نزيد عداد المتابعة قبل التأكد من وجود كمية موجبة
            // على بند ما زال له متبقي — وهذا الفحص داخل المعاملة أيضاً لمعالجة
            // سباق استلامين وصلا بعد أن أكمل أحدهما السند.
            bool fullRemainingMode = receivedByItemId == null || receivedByItemId.Count == 0;
            if (!fullRemainingMode && !receivedByItemId!.Any(x => x.Value > 0.001))
                return OpResult.Fail("لم تُدخل أي كمية مستلمة.");
            if (!fullRemainingMode && receivedByItemId!.Keys.Any(id => rcpt.Items.All(i => i.Id != id)))
                throw new DomainException("توجد كمية مرتبطة ببند سند استلام غير تابع لهذا السند.", "RECEIPT_ITEM_MISMATCH");
            bool hasPositiveRemaining = fullRemainingMode
                ? rcpt.Items.Any(i => i.NetWeightKg - i.ReceivedQtyKg > 0.001)
                : rcpt.Items.Any(i => i.NetWeightKg - i.ReceivedQtyKg > 0.001
                    && receivedByItemId!.TryGetValue(i.Id, out var requested) && requested > 0.001);
            if (!hasPositiveRemaining)
                return OpResult.Fail("لا توجد كمية موجبة متبقية للاستلام — لم يُحجز رقم سند متابعة.");
            rcpt.ReceiveCount++;
            rcpt.ReceiptNumber ??= Numbering.Next("RCV");
            var voucher = $"{rcpt.ReceiptNumber}#{rcpt.ReceiveCount}"; // لكل سند استلام (متابعة) ترقيم متسلسل
            var recvAcc = new Dictionary<int, double>(); // §B86/H8: مستلَم هذه الدفعة لكل صنف — بندَان لصنف واحد لا يتجاوزا السقف معاً
            // §B96 — المربوط: بنود التسليم للتحديث + مجمّع لكل بند (سندان لبند واحد لا يتجاوزاه معاً)
            var delLines = rcpt.DeliveryId != null
                ? Db.ProductionDeliveryItems.Where(i => i.DeliveryId == rcpt.DeliveryId.Value).ToList()
                : new List<ProductionDeliveryItem>();
            var recvAccLine = new Dictionary<int, double>();
            foreach (var item in rcpt.Items)
            {
                double remaining = item.NetWeightKg - item.ReceivedQtyKg;
                if (remaining <= 0.001) continue;
                // null أو قاموس فارغ يبقى توافقاً مع استدعاء «استلام كامل» الصريح.
                // أما القاموس الجزئي غير الفارغ فالسطر الغائب = صفر.
                double recv = fullRemainingMode
                    ? remaining
                    : (receivedByItemId!.TryGetValue(item.Id, out var v) ? v : 0);
                if (recv <= 0) continue;
                if (recv > remaining + 0.001)
                    throw new DomainException($"الكمية المستلمة أكبر من المتبقي للبند ({remaining:N1} كجم).", "OVER_RECEIPT");
                // §B96 — المربوط: سقف بند التسليم أولاً (رسالة دقيقة) ثم السقف الفيزيائي الموحد (شبكة أمان ضد المباشر)
                ProductionDeliveryItem delLine = null;
                double recvLineBeforeThisItem = 0;
                double delLineReceivedBeforeThisItem = 0;
                if (item.DeliveryItemId != null)
                {
                    delLine = delLines.FirstOrDefault(l => l.Id == item.DeliveryItemId.Value)
                        ?? throw new DomainException("بند أمر التسليم المربوط غير موجود.", "NO_DELIVERY_LINE");
                    delLineReceivedBeforeThisItem = delLine.ReceivedQtyKg;
                    recvAccLine.TryGetValue(delLine.Id, out recvLineBeforeThisItem);
                    if (delLine.ReceivedQtyKg + recvLineBeforeThisItem + recv > delLine.QtyKg + 0.001)
                        throw new DomainException(
                            $"⛔ الاستلام يتجاوز بند أمر التسليم.\nالبند: {delLine.QtyKg:N1} كجم | المستلَم منه: {delLine.ReceivedQtyKg + recvLineBeforeThisItem:N1} | المطلوب: {recv:N1}",
                            "OVER_DELIVERY");
                    recvAccLine[delLine.Id] = recvLineBeforeThisItem + recv;
                    if (delLine.OrderId is int capOrder)
                    {
                        double producedCap = Db.ProductionOrderItems.AsNoTracking()
                            .Where(o => o.OrderId == capOrder && o.ProductId == item.ProductId)
                            .Sum(o => o.ProducedQtyKg);
                        // §بنود التسليم لنفس الأمر (مجموعة محلية — Contains تُترجم إلى IN)
                        var capLineIds = delLines.Where(l => l.OrderId == capOrder).Select(l => l.Id).ToHashSet();
                        double receivedAll = Db.FinishedGoodsReceiptItems.AsNoTracking()
                            .Join(Db.FinishedGoodsReceipts.AsNoTracking(), i => i.ReceiptId, r => r.Id, (i, r) => new { i, r })
                            .Where(x => x.r.Status != DocStatuses.Cancelled && x.i.Id != item.Id
                                && ((x.i.DeliveryItemId != null && capLineIds.Contains(x.i.DeliveryItemId.Value))
                                    || (x.i.DeliveryItemId == null && x.r.OrderId == capOrder)))
                            .Where(x => x.i.ProductId == item.ProductId)
                            .Sum(x => x.i.ReceivedQtyKg);
                        recvAcc.TryGetValue(item.ProductId, out var recvThisCall2);
                        if (receivedAll + item.ReceivedQtyKg + recvThisCall2 + recv > producedCap + 0.001)
                            throw new DomainException(
                                $"الاستلام يتجاوز المنتَج الفعلي للصنف (مباشر + مربوط معاً).\nالمنتَج: {producedCap:N1} كجم | المستلَم: {receivedAll + item.ReceivedQtyKg + recvThisCall2:N1} | المطلوب: {recv:N1}",
                                "EXCEED_ORDER_QTY");
                        recvAcc[item.ProductId] = recvThisCall2 + recv;
                    }
                    delLine.ReceivedQtyKg += recv;
                }
                else if (currentDirect)
                {
                    var source = BuildDirectReceiptSources(rcpt.SourceType, rcpt.Id)
                        .FirstOrDefault(x => x.OrderId == rcpt.OrderId && x.ExecutionId == rcpt.ProductionExecutionId
                            && x.QualityCheckId == rcpt.QualityCheckId);
                    var sourceLine = source?.Items.FirstOrDefault(l => item.ByProductId != null
                        ? l.ByProductId == item.ByProductId && l.SourceOrderItemId == item.SourceOrderItemId
                            && l.RawProductId == item.RawProductId && l.RawLotId == item.RawLotId && l.CustomerId == item.CustomerId
                        : l.OrderItemId == item.OrderItemId && l.ProductId == item.ProductId);
                    if (sourceLine == null || !string.IsNullOrWhiteSpace(sourceLine.BlockReason)
                        || sourceLine.AvailableQtyKg + 0.001 < item.ReceivedQtyKg + recv)
                        throw new DomainException("الكمية المطلوبة تتجاوز المتبقي المسموح به في مصدر الإقفال/الفحص.",
                            item.ByProductId == null ? "SOURCE_CAP_EXCEEDED" : "BYPRODUCT_CAP_EXCEEDED");
                }
                else
                {
                // §B86/H8: سقف المنتَج يُفحص عند الاستلام أيضاً — مسودتان معاً قد تتجاوزا المنتَج الفعلي
                double receivedOthers = Db.FinishedGoodsReceiptItems
                    .Join(Db.FinishedGoodsReceipts, i => i.ReceiptId, r => r.Id, (i, r) => new { i, r })
                    .Where(x => x.r.OrderId == rcpt.OrderId && x.i.ProductId == item.ProductId
                        && x.r.Status != DocStatuses.Cancelled && x.i.Id != item.Id)
                    .Sum(x => x.i.ReceivedQtyKg);
                double producedCap = Db.ProductionOrderItems.AsNoTracking()
                    .Where(o => o.OrderId == rcpt.OrderId && o.ProductId == item.ProductId)
                    .Sum(o => o.ProducedQtyKg);
                recvAcc.TryGetValue(item.ProductId, out var recvThisCall);
                if (receivedOthers + item.ReceivedQtyKg + recvThisCall + recv > producedCap + 0.001)
                    throw new DomainException(
                        $"الاستلام يتجاوز المنتَج الفعلي للصنف.\nالمنتَج: {producedCap:N1} كجم | المستلَم في سندات أخرى: {receivedOthers:N1} | هذا السند بعد الاستلام: {item.ReceivedQtyKg + recvThisCall + recv:N1}",
                        "EXCEED_ORDER_QTY");

                recvAcc[item.ProductId] = recvThisCall + recv;
                }
                // PRD-06 — الكراتين تُرحّل بالفرق بين هدفين تراكميين على بند التسليم،
                // لا بتقريب مستقل لكل استلام. لذلك 1.5 + 1.5 كرتوناً لا تصبح 2 + 2.
                double priorLineKg = 0;
                int linePackages = 0;
                double lineQtyKg = 0;
                if (delLine != null)
                {
                    priorLineKg = delLineReceivedBeforeThisItem + recvLineBeforeThisItem;
                    linePackages = delLine.PackageCount;
                    lineQtyKg = delLine.QtyKg;
                }
                int previousTarget = linePackages > 0 && lineQtyKg > 0
                    ? (int)Math.Round(linePackages * priorLineKg / lineQtyKg, MidpointRounding.AwayFromZero) : 0;
                item.ReceivedQtyKg += recv;
                totalReceived += recv;
                double cumulativeLineKg = priorLineKg + recv;
                int currentTarget = linePackages > 0 && lineQtyKg > 0
                    ? (int)Math.Round(linePackages * cumulativeLineKg / lineQtyKg, MidpointRounding.AwayFromZero)
                    : (item.PackageCount > 0 && item.NetWeightKg > 0
                        ? (int)Math.Round(item.PackageCount * item.ReceivedQtyKg / item.NetWeightKg, MidpointRounding.AwayFromZero) : 0);
                int pkgRecv = Math.Max(0, currentTarget - previousTarget);
                int itemWarehouseId = item.WarehouseId ?? whFg;
                if (!Db.Warehouses.AsNoTracking().Any(w => w.Id == itemWarehouseId && w.IsActive))
                    throw new DomainException("مخزن استلام البند غير موجود أو موقوف.", "WAREHOUSE_INVALID");
                int? movementCustomer = item.CustomerId ?? orderCust;
                item.ReceivedPackageCount += pkgRecv;
                string movementSource = currentDelivery != null
                    ? $"أمر التسليم {currentDelivery.DocumentNumber}"
                    : $"إقفال {rcpt.ProductionExecutionId} / فحص {rcpt.QualityCheckId}";
                string traceNote = item.ByProductId is int bpId
                    ? $"مخرج ثانوي #{bpId} — الخام #{item.RawProductId} — دفعة الخام #{item.RawLotId} — بند الأمر #{item.SourceOrderItemId} — العميل #{movementCustomer}"
                    : $"بند الأمر #{item.OrderItemId} — الصنف التام";
                PostStockMovement(itemWarehouseId, MovementType.Inbound, recv, pkgRecv,
                    ReferenceDocType.FinishedGoodsReceipt, $"{voucher}-{item.Id}",
                    productId: item.ProductId, lotId: item.LotId, orderId: rcpt.OrderId,
                    customerId: movementCustomer, packagingTypeId: item.PackagingTypeId,
                    notes: $"استلام إنتاج — {movementSource} — {traceNote} — سند {rcpt.ReceiptNumber}",
                    txnDate: rcpt.DeliveryDate);
            }

            if (totalReceived <= 0.001) return OpResult.Fail("لم تُدخل أي كمية مستلمة.");

            bool full = rcpt.Items.All(i => i.ReceivedQtyKg + 0.001 >= i.NetWeightKg);
            rcpt.ReceiptStatus = full ? "Full" : "Partial";
            rcpt.IsApproved = approve;
            rcpt.Status = approve ? DocStatuses.Completed : DocStatuses.Submitted;
            // §B96 — عكس التقدم على أمر التسليم المربوط
            if (rcpt.DeliveryId != null)
            {
                var delivery = Db.ProductionDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == rcpt.DeliveryId.Value);
                if (delivery != null)
                {
                    bool dFull = delivery.Items.Count > 0 && delivery.Items.All(i => i.ReceivedQtyKg + 0.001 >= i.QtyKg);
                    bool dAny = delivery.Items.Any(i => i.ReceivedQtyKg > 0.001);
                    delivery.ReceiptStatus = dFull ? "Full" : (dAny ? "Partial" : "None");
                    if (dFull) delivery.Status = DocStatuses.Completed;
                }
            }
            rcpt.ApprovedBy = approve ? Session?.UserId : null;
            rcpt.ApprovedDate = approve ? DateTime.Now : null;

            // إغلاق أمر الإنتاج تلقائياً عند الاكتمال الكامل للإنتاج والاستلام؛
            // أما إذا كان استلام جلسة جزئية وما زال للأمر إنتاج متبقٍ، فيبقى InProgress قابلاً لاستكمال الجلسة التالية!
            bool sourceFullyReceived = full;
            if (full && currentDirect)
            {
                // التذكرة الواحدة قد تكون استلاماً جزئياً من الكمية المقبولة؛ لا نغلق الأمر
                // حتى لا يبقى أي جزء مقبول أو مخرج ثانوي غير مرحّل.
                Db.SaveChanges();
                var remainingSource = BuildDirectReceiptSources(rcpt.SourceType, null)
                    .FirstOrDefault(x => x.OrderId == rcpt.OrderId && x.ExecutionId == rcpt.ProductionExecutionId
                        && x.QualityCheckId == rcpt.QualityCheckId);
                sourceFullyReceived = remainingSource == null || !remainingSource.Items.Any(i => i.AvailableQtyKg > 0.001);
            }
            if (full && sourceFullyReceived)
            {
                var order = Db.ProductionOrders.Include(o => o.Items).FirstOrDefault(o => o.Id == rcpt.OrderId);
                if (order != null && order.Items.All(i => i.IsClosed || i.ProducedQtyKg + 0.001 >= i.PlannedQtyKg))
                {
                    order.Status = DocStatuses.Completed;
                    order.IsClosed = true;
                    order.ClosedDate = DateTime.Now;
                }
                else if (order != null && !order.IsClosed)
                {
                    order.Status = DocStatuses.InProgress;
                }
            }

            if (full && rcpt.DeliveryId is int pdIdDone)
                CompleteDocumentTasks(WorkflowDocTypes.ProductionDelivery, pdIdDone, WorkflowTaskTypes.WarehouseReceipt, actionResult: "Received", notes: $"استُلم بموجب {rcpt.ReceiptNumber ?? rcpt.DocumentNumber}");
            if (approve)
                RaiseWorkflowTask(new NewTaskRequest
                {
                    Stage = WorkflowStage.CustomerDelivery,
                    TaskType = WorkflowTaskTypes.CustomerDelivery,
                    DocumentType = WorkflowDocTypes.FinishedGoodsReceipt,
                    DocumentId = rcpt.Id,
                    DocumentNumber = rcpt.ReceiptNumber ?? rcpt.DocumentNumber,
                    BusinessDate = rcpt.DeliveryDate ?? Db.BusinessNow.Date,
                    DueDate = rcpt.DeliveryDate ?? Db.BusinessNow.Date,
                    RequiredCapability = WorkflowCapabilities.DeliveryIssue,
                    FromCapability = WorkflowCapabilities.WarehouseReceive,
                    Title = $"جاهز للتسليم للعميل — سند استلام التام {rcpt.ReceiptNumber ?? rcpt.DocumentNumber}"
                });
            Db.SaveChanges();
            return OpResult.Success(full
                ? "تم الاستلام الكامل وتقييد كامل الكمية في مخزن الإنتاج التام."
                : $"تم الاستلام الجزئي ({totalReceived:N1} كجم) وتقييدها في مخزن التام.", rcpt.Id, rcpt.ReceiptNumber);
        });
    }

    /// <summary>اعتماد سند رُحّل مباشرة إلى المخزون؛ الاعتماد يقفل السند ويطلق مهمة تسليم العميل.</summary>
    public OpResult ApproveReceipt(int receiptId)
    {
        Require("finishedgoods", "Approve");
        if (receiptId <= 0) return OpResult.Fail("حدد سند الاستلام المراد اعتماده.");
        return RunOp(() =>
        {
            Db.ChangeTracker.Clear();
            var receipt = Db.FinishedGoodsReceipts.Include(r => r.Items).FirstOrDefault(r => r.Id == receiptId)
                ?? throw new DomainException("سند الاستلام غير موجود.", "RECEIPT_MISSING");
            if (receipt.Status == DocStatuses.Cancelled)
                throw new DomainException("لا يمكن اعتماد سند ملغى.", "RECEIPT_CANCELLED");
            if (receipt.IsApproved || receipt.Status == DocStatuses.Completed)
                throw new DomainException("سند الاستلام معتمد مسبقاً.", "RECEIPT_ALREADY_APPROVED");
            if (receipt.Status != DocStatuses.Submitted || receipt.ReceiptStatus is not ("Full" or "Partial")
                || !receipt.Items.Any(i => i.ReceivedQtyKg > 0.001))
                throw new DomainException("لا يمكن الاعتماد قبل حفظ الاستلام وترحيل كمية فعلية للمخزون.", "RECEIPT_NOT_POSTED");

            receipt.IsApproved = true;
            receipt.Status = DocStatuses.Completed;
            receipt.ApprovedBy = Session?.UserId;
            receipt.ApprovedDate = DateTime.Now;
            RaiseWorkflowTask(new NewTaskRequest
            {
                Stage = WorkflowStage.CustomerDelivery,
                TaskType = WorkflowTaskTypes.CustomerDelivery,
                DocumentType = WorkflowDocTypes.FinishedGoodsReceipt,
                DocumentId = receipt.Id,
                DocumentNumber = receipt.ReceiptNumber ?? receipt.DocumentNumber,
                BusinessDate = receipt.DeliveryDate ?? Db.BusinessNow.Date,
                DueDate = receipt.DeliveryDate ?? Db.BusinessNow.Date,
                RequiredCapability = WorkflowCapabilities.DeliveryIssue,
                FromCapability = WorkflowCapabilities.WarehouseReceive,
                Title = $"جاهز للتسليم للعميل — سند استلام التام {receipt.ReceiptNumber ?? receipt.DocumentNumber}"
            });
            Db.SaveChanges();
            return OpResult.Success($"تم اعتماد سند الاستلام {receipt.ReceiptNumber ?? receipt.DocumentNumber} وقُفل للتعديل.", receipt.Id, receipt.ReceiptNumber ?? receipt.DocumentNumber);
        });
    }

    /// <summary>إلغاء السند يعكس الأرصدة بدقة ويحذف حركاته (§6).</summary>
    public OpResult Unapprove(int receiptId)
    {
        Require("finishedgoods", "Cancel");
        var rcpt = Db.FinishedGoodsReceipts.Include(r => r.Items).FirstOrDefault(r => r.Id == receiptId);
        if (rcpt == null) return OpResult.Fail("السند غير موجود.");
        if (rcpt.Status == DocStatuses.Cancelled) return OpResult.Fail("السند ملغى مسبقاً.");

        return RunOp(() =>
        {
            Db.ChangeTracker.Clear();
            rcpt = Db.FinishedGoodsReceipts.Include(r => r.Items).FirstOrDefault(r => r.Id == receiptId)
                ?? throw new DomainException("السند غير موجود.", "RECEIPT_MISSING");
            if (rcpt.Status == DocStatuses.Cancelled)
                throw new DomainException("السند ملغى مسبقاً.", "RECEIPT_CANCELLED");
            bool pendingDirectApproval = !rcpt.IsApproved && rcpt.Status == DocStatuses.Submitted
                && rcpt.DeliveryId == null && rcpt.ProductionExecutionId != null;
            if (!rcpt.IsApproved && rcpt.Status != DocStatuses.Draft && rcpt.Status != DocStatuses.Issued && !pendingDirectApproval)
                throw new DomainException("لا يمكن إلغاء السند في حالته الحالية.", "RECEIPT_STATE_INVALID");
            if (!rcpt.IsApproved && !pendingDirectApproval && rcpt.Items.Any(i => i.ReceivedQtyKg > 0.001))
                throw new DomainException("توجد كمية مرحّلة في السند غير المعتمد؛ يلزم فحصه قبل الإلغاء.", "RECEIPT_UNAPPROVED_MOVEMENT");
            var delivery = rcpt.DeliveryId != null
                ? Db.ProductionDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == rcpt.DeliveryId.Value)
                : null;
            bool directReceipt = rcpt.DeliveryId == null && rcpt.ProductionExecutionId != null
                && ((rcpt.SourceType == DeliverySources.FromActual && rcpt.QualityCheckId == null)
                    || (rcpt.SourceType == DeliverySources.FromCheck && rcpt.QualityCheckId != null));
            if (!directReceipt && delivery == null)
                throw new DomainException("مرجع سند الاستلام غير موجود.", "RECEIPT_SOURCE_MISSING");
            if (delivery?.Status == DocStatuses.Cancelled)
                throw new DomainException("أمر التسليم المرتبط ملغى.", "DELIVERY_CANCELLED");
            var whFg = rcpt.WarehouseId;
            var orderCust = Db.ProductionOrders.Where(o => o.Id == rcpt.OrderId).Select(o => o.CustomerId).FirstOrDefault();
            var prefix = rcpt.ReceiptNumber ?? rcpt.DocumentNumber;
            // §إصلاح حرج — الإلغاء بقيد عكسي لا بحذف دفتر الأستاذ.
            // كان يحذف الحركات بـ StartsWith(prefix):
            //  • يدمّر سجلّاً إلحاقياً (قرارهم #48: «إلحاقي غير قابل للتعديل»)
            //  • وتصادم بادئات: عند السند رقم 10000 يصبح RCV-...-1000 بادئة له فيحذف حركاته
            //  • وكان يبحث الرصيد بلا CustomerId بينما Receive يكتب به ← قد يطرح من صف آخر
            // §B96 — أمر التسليم المربوط محمّل قبل تصفير الكميات ليُعكس عنه المستلَم بدقة.
            int seq = 0;
            foreach (var item in rcpt.Items.Where(i => i.ReceivedQtyKg > 0))
            {
                int itemWarehouseId = item.WarehouseId ?? whFg;
                int? movementCustomer = item.CustomerId ?? orderCust;
                int pkgBack = Db.InventoryTransactions
                    .Where(t => t.ReferenceDocType == ReferenceDocType.FinishedGoodsReceipt
                        && t.MovementType == MovementType.Inbound
                        && t.WarehouseId == itemWarehouseId
                        && t.ReferenceDocNumber.StartsWith(prefix + "#")
                        && t.ProductId == item.ProductId && t.LotId == item.LotId
                        && t.CustomerId == movementCustomer
                        && t.PackagingTypeId == item.PackagingTypeId)
                    .Sum(t => t.PackageCount);
                seq++;
                PostStockMovement(itemWarehouseId, MovementType.Outbound, item.ReceivedQtyKg, pkgBack,
                    ReferenceDocType.FinishedGoodsReceipt, $"{prefix}#REV{seq}",
                    productId: item.ProductId, lotId: item.LotId, orderId: rcpt.OrderId,
                    customerId: movementCustomer, packagingTypeId: item.PackagingTypeId,
                    notes: $"إلغاء سند الاستلام {rcpt.ReceiptNumber} — قيد عكسي");
                if (delivery != null && item.DeliveryItemId != null)
                {
                    var back = delivery.Items.FirstOrDefault(l => l.Id == item.DeliveryItemId.Value);
                    if (back != null) back.ReceivedQtyKg = Math.Max(0, back.ReceivedQtyKg - item.ReceivedQtyKg);
                }
                item.ReceivedQtyKg = 0;
            }
            rcpt.IsApproved = false;
            rcpt.ReceiptStatus = "None";
            rcpt.Status = DocStatuses.Cancelled;
            // §B96 — إعادة احتساب حالة أمر التسليم المربوط وإعادة فتحه إن اكتمل سابقاً
            string delReopenMsg = "";
            if (delivery != null)
            {
                bool dFull = delivery.Items.Count > 0 && delivery.Items.All(i => i.ReceivedQtyKg + 0.001 >= i.QtyKg);
                bool dAny = delivery.Items.Any(i => i.ReceivedQtyKg > 0.001);
                delivery.ReceiptStatus = dFull ? "Full" : (dAny ? "Partial" : "None");
                if (dFull) delivery.Status = DocStatuses.Completed;
                else if (delivery.Status == DocStatuses.Completed)
                {
                    delivery.Status = DocStatuses.Issued;
                    delReopenMsg = " وأُعيد فتح أمر التسليم (استلامه لم يعد مكتملاً).";
                }
            }
            // §B86/H8: إلغاء آخر سند كامل يعيد فتح الأمر المغلق تلقائياً (التلقائي = Completed+مقفل؛ اليدوي = Closed فلا يُمس)
            string reopenMsg = "";
            bool otherFull = Db.FinishedGoodsReceipts.AsNoTracking()
                .Any(r => r.OrderId == rcpt.OrderId && r.Id != rcpt.Id
                    && r.Status != DocStatuses.Cancelled && r.ReceiptStatus == "Full");
            if (!otherFull)
            {
                var ord = Db.ProductionOrders.FirstOrDefault(o => o.Id == rcpt.OrderId);
                if (ord != null && ord.IsClosed && ord.Status == DocStatuses.Completed)
                {
                    ord.IsClosed = false;
                    ord.ClosedDate = null;
                    ord.Status = DocStatuses.PendingDelivery;
                    reopenMsg = " وأُعيد فتح أمر الإنتاج (تسليمه لم يعد مكتملاً).";
                }
            }
            CancelDocumentTasks(WorkflowDocTypes.FinishedGoodsReceipt, rcpt.Id, "أُلغي سند استلام التام");
            Db.SaveChanges();
            return OpResult.Success("تم إلغاء السند وعكس أرصدة مخزن التام بالكامل." + reopenMsg + delReopenMsg);
        });
    }

}
