using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §بوابة جودة مركزية موحدة:
/// تفحص استبعاد الأوامر الملغاة، وهوية (الأمر/البند/العميل/الصنف/الدفعة/العبوة)،
/// وتدعم تعدد الأوامر لنفس الدفعة وتعدد التخصيصات مع منع تسليم أي بضاعة مرفوضة أو محجوزة أو فوق المقبول المعتمد.
/// </summary>
public static class QualityGate
{
    public const string Passed = "Passed";
    public const string Quarantine = "Quarantine";
    public const string Rejected = "Rejected";

    private sealed record DirectReceiptRelease(int OrderId, int ExecutionId, int? OrderItemId, double QtyKg);

    /// <summary>
    /// الكمية المستلمة فعلياً بسند مخزني معتمد من مسار Direct. هذا المسار لا يخضع لفحص جودة؛
    /// لا يُعدّ وجود تنفيذ مباشر وحده كافياً، بل يجب وجود استلام مرحّل ومعتمد مطابق لهوية المخزون.
    /// </summary>
    private static List<DirectReceiptRelease> DirectReceiptReleases(
        DatesErpDbContext db, int? orderId, int? lotId, int productId, int? packagingTypeId, int? customerId)
    {
        var rows = (from receipt in db.FinishedGoodsReceipts.AsNoTracking()
                    join execution in db.ProductionExecutions.AsNoTracking()
                        on receipt.ProductionExecutionId equals (int?)execution.Id
                    join order in db.ProductionOrders.AsNoTracking() on receipt.OrderId equals order.Id
                    join item in db.FinishedGoodsReceiptItems.AsNoTracking() on receipt.Id equals item.ReceiptId
                    where receipt.IsApproved && receipt.Status == DocStatuses.Completed
                        && receipt.SourceType == DeliverySources.FromActual && receipt.QualityCheckId == null
                        && receipt.DeliveryId == null && execution.OrderId == receipt.OrderId
                        && execution.WarehouseRoute == ProductionWarehouseRoutes.Direct
                        && execution.Status == DocStatuses.Completed && execution.IsDayClosed && execution.EndDateTime != null
                        && item.ByProductId == null && item.ProductId == productId && item.ReceivedQtyKg > 0.001
                        && (orderId == null || receipt.OrderId == orderId.Value)
                        && (lotId == null || item.LotId == lotId)
                        && (packagingTypeId == null || item.PackagingTypeId == packagingTypeId)
                        && (customerId == null || (item.CustomerId ?? order.CustomerId) == customerId)
                    select new { receipt.OrderId, receipt.ProductionExecutionId, item.OrderItemId, item.ReceivedQtyKg })
            .ToList();
        return rows.Select(x => new DirectReceiptRelease(
            x.OrderId, x.ProductionExecutionId!.Value, x.OrderItemId, x.ReceivedQtyKg)).ToList();
    }

    private static List<ProductionOrderItem> MatchingActiveOrderItems(
        DatesErpDbContext db, int? orderId, int? lotId, int? productId, int? packagingTypeId = null, int? customerId = null)
    {
        var allocOrderItemIds = lotId != null
            ? db.Set<ProductionOrderItemAllocation>().AsNoTracking()
                .Where(a => a.LotId == lotId.Value)
                .Select(a => a.OrderItemId).ToHashSet()
            : new HashSet<int>();

        var rawItems = (from oi in db.ProductionOrderItems.AsNoTracking()
                        join o in db.ProductionOrders.AsNoTracking() on oi.OrderId equals o.Id
                        where o.Status != DocStatuses.Cancelled && oi.Status != DocStatuses.Cancelled
                            && (orderId == null || oi.OrderId == orderId.Value)
                            && (productId == null || oi.ProductId == productId.Value)
                        select new { oi, OrderCust = o.CustomerId }).ToList();

        return rawItems
            .Where(x => lotId == null || x.oi.LotId == lotId || (x.oi.LotId == null && allocOrderItemIds.Contains(x.oi.Id)))
            .Where(x => packagingTypeId == null || x.oi.PackagingTypeId == packagingTypeId)
            .Where(x => customerId == null || (x.oi.CustomerId ?? x.OrderCust) == null || (x.oi.CustomerId ?? x.OrderCust) == customerId)
            .Select(x => x.oi)
            .ToList();
    }

    public static (bool ok, string reason) CustomerDeliveryAllowed(
        DatesErpDbContext db, int? orderId, int? lotId, int? productId, int? packagingTypeId = null, int? customerId = null)
    {
        var candidates = MatchingActiveOrderItems(db, orderId, lotId, productId, packagingTypeId, customerId);
        if (packagingTypeId == null && candidates.Select(x => x.PackagingTypeId).Distinct().Count() > 1)
            return (false, "⛔ لا يمكن التسليم: توجد أكثر من عبوة لنفس الصنف/الدفعة — حدّد عبوة السطر قبل الإفراج.");

        var direct = productId is int pid
            ? DirectReceiptReleases(db, orderId, lotId, pid, packagingTypeId, customerId)
            : new List<DirectReceiptRelease>();
        double directQty = direct.Sum(x => x.QtyKg);
        var directOrderIds = direct.Select(x => x.OrderId).ToHashSet();
        var directExecutionIds = direct.Select(x => x.ExecutionId).ToHashSet();

        if (orderId != null && candidates.Count == 0 && direct.Count == 0)
            return (false, "⛔ لا يمكن التسليم: عبوة/دفعة السطر لا تطابق بند أمر الإنتاج المحدد.");

        var orderIds = orderId != null
            ? new List<int> { orderId.Value }
            : candidates.Select(x => x.OrderId).Concat(directOrderIds).Distinct().ToList();
        if (orderIds.Count == 0)
            return (false,
                "⛔ لا يمكن التسليم للعميل: لا يوجد بند أمر إنتاج نشط أو استلام مباشر معتمد مرتبط بهذه الدفعة والعبوة.\n" +
                "اربط السند بأمر الإنتاج وحدّد الدفعة والعبوة.");

        double totalAcceptedAcrossOrders = 0;
        bool anyApprovedCheck = false;
        string qualityBlockReason = null;
        foreach (var oid in orderIds)
        {
            // لا نفرض الجودة على جلسات Direct نفسها. إن كان للأمر جلسة أخرى سلكت الجودة،
            // تبقى فحوصاتها مطبقة على الكمية المقبولة الخاصة بها.
            var oidItems = candidates.Where(i => i.OrderId == oid).ToList();
            var oidItemIds = oidItems.Select(i => i.Id).ToHashSet();
            var checks = db.QualityChecks.AsNoTracking().Where(c => c.OrderId == oid && c.IsApproved).ToList()
                .Where(c => c.ExecutionId == null || !directExecutionIds.Contains(c.ExecutionId.Value)).ToList();
            if (checks.Count == 0)
            {
                if (directQty <= 0.001 && (orderId != null || oidItems.Sum(x => x.ProducedQtyKg) > 0.001))
                    qualityBlockReason ??= "⛔ لا يمكن التسليم للعميل: لا يوجد فحص جودة معتمد (فحص الجودة مطلوب) للبند المرتبط.\nنفّذ الفحص واعتمد نتيجته قبل الإفراج.";
                continue;
            }

            anyApprovedCheck = true;
            var checkIds = checks.Select(c => c.Id).ToList();
            var allCheckRows = db.QualityCheckItems.AsNoTracking()
                .Where(i => checkIds.Contains(i.CheckId) && (productId == null || i.ProductId == productId.Value))
                .ToList();
            bool hasUnscopedAmbiguity = oidItems.Count > 1
                && allCheckRows.Any(r => r.OrderItemId == null && r.PackagingTypeId == null && r.CustomerId == null
                    && (r.RejectedQtyKg > 0.001 || r.RejectedCartons > 0)
                    && (lotId == null || r.LotId == lotId));
            if (hasUnscopedAmbiguity && directQty <= 0.001)
                qualityBlockReason ??= "⛔ لا يمكن الإفراج: توجد عدة بنود متشابهة للصنف/الدفعة بلا تحديد بند الفحص ولا يمكن إثبات مالك المقبول.";

            var relevant = allCheckRows.Where(i =>
                (i.OrderItemId != null && oidItemIds.Contains(i.OrderItemId.Value))
                || (i.OrderItemId == null
                    && (lotId == null || i.LotId == lotId || i.LotId == null)
                    && (packagingTypeId == null || i.PackagingTypeId == null || i.PackagingTypeId == packagingTypeId)
                    && (customerId == null || i.CustomerId == null || i.CustomerId == customerId)))
                .ToList();
            var rejected = checks.FirstOrDefault(c => c.Decision == Rejected && relevant.Any(i => i.CheckId == c.Id));
            if (rejected != null && directQty <= 0.001)
                qualityBlockReason ??= $"⛔ لا يمكن التسليم للعميل: قرار فحص الجودة «مرفوض تماماً / عوادم».\nالفحص: {rejected.DocumentNumber} — اعتمد قرار الإتلاف أو إعادة التصنيع قبل أي تسليم.";
            var quarantined = checks.FirstOrDefault(c => c.Decision == Quarantine && relevant.Any(i => i.CheckId == c.Id));
            if (quarantined != null && directQty <= 0.001)
                qualityBlockReason ??= $"⛔ لا يمكن التسليم للعميل: البضاعة تحت «حجز وتحريز مؤقت».\nالفحص: {quarantined.DocumentNumber} — الإفراج يتطلب قرار جودة «مطابق ومقبول».";

            var passedCheckIds = checks.Where(c => c.Decision == Passed).Select(c => c.Id).ToHashSet();
            totalAcceptedAcrossOrders += relevant.Where(i => passedCheckIds.Contains(i.CheckId)).Sum(i => i.AcceptedQtyKg);
        }
        if (totalAcceptedAcrossOrders + directQty <= 0.001)
        {
            if (!string.IsNullOrWhiteSpace(qualityBlockReason)) return (false, qualityBlockReason);
            if (!anyApprovedCheck && directQty <= 0.001)
                return (false, "⛔ لا يمكن التسليم للعميل: لا يوجد فحص جودة معتمد للبند المرتبط، ولا يوجد استلام مباشر معتمد.");
            return (false, "⛔ لا يمكن التسليم للعميل: لا توجد كمية مقبولة بفحص معتمد أو مستلمة مباشرة للمخزن.");
        }
        return (true, null);
    }

    public static (bool ready, string label) DeliveryReadiness(
        DatesErpDbContext db, int? lotId, int productId, int? packagingTypeId = null, int? customerId = null)
    {
        if (DirectReceiptReleases(db, null, lotId, productId, packagingTypeId, customerId).Sum(x => x.QtyKg) > 0.001)
            return (true, "✔ جاهز للتسليم (استلام مباشر)");
        var (ok, reason) = CustomerDeliveryAllowed(db, null, lotId, productId, packagingTypeId, customerId);
        if (ok) return (true, "✔ معتمد");
        if (reason != null && reason.Contains("مرفوض")) return (false, "⛔ مرفوض");
        if (reason != null && reason.Contains("حجز")) return (false, "⛔ محجوز");
        return (false, "⏳ بانتظار الفحص");
    }

    public static (bool ok, string reason) CustomerDeliveryQtyAllowed(
        DatesErpDbContext db, CustomerDelivery dlv, CustomerDeliveryItem item)
    {
        var orderItems = MatchingActiveOrderItems(db, dlv.OrderId, item.LotId, item.ProductId, item.PackagingTypeId, dlv.CustomerId);
        var direct = DirectReceiptReleases(db, dlv.OrderId, item.LotId, item.ProductId, item.PackagingTypeId, dlv.CustomerId);
        var directQty = direct.Sum(x => x.QtyKg);
        var directOrderIds = direct.Select(x => x.OrderId).ToHashSet();
        var directExecutionIds = direct.Select(x => x.ExecutionId).ToHashSet();
        var orderIds = dlv.OrderId != null
            ? new List<int> { dlv.OrderId.Value }
            : orderItems.Select(i => i.OrderId).Concat(directOrderIds).Distinct().ToList();
        if (orderIds.Count == 0)
            return (false, "⛔ لا يمكن التسليم: لم يُعثر على بند إنتاج نشط أو استلام مباشر معتمد مطابق لهوية السطر.");

        var matchedItemIds = orderItems.Select(i => i.Id).ToHashSet();
        var passedChecks = db.QualityChecks.AsNoTracking()
            .Where(c => c.OrderId != null && orderIds.Contains(c.OrderId.Value)
                && c.IsApproved && c.Decision == Passed
                && (c.ExecutionId == null || !directExecutionIds.Contains(c.ExecutionId.Value)))
            .Select(c => c.Id).ToList();
        var checkItems = db.QualityCheckItems.AsNoTracking()
            .Where(q => passedChecks.Contains(q.CheckId) && q.ProductId == item.ProductId)
            .ToList();

        foreach (var oid in orderIds)
        {
            var oidOrderItems = orderItems.Where(i => i.OrderId == oid).ToList();
            if (oidOrderItems.Count > 1)
            {
                var oidCheckIds = db.QualityChecks.AsNoTracking()
                    .Where(c => c.OrderId == oid && c.IsApproved).Select(c => c.Id).ToHashSet();
                if (checkItems.Any(q => oidCheckIds.Contains(q.CheckId) && q.OrderItemId == null
                    && q.PackagingTypeId == null && q.CustomerId == null))
                    return (false, "⛔ لا يمكن التسليم: توجد عدة بنود متشابهة للصنف/الدفعة/العبوة بلا تحديد هوية الفحص.");
            }
        }

        double accepted = directQty + checkItems
            .Where(q => (q.OrderItemId != null && matchedItemIds.Contains(q.OrderItemId.Value))
                || (q.OrderItemId == null
                    && (item.LotId == null || q.LotId == item.LotId || q.LotId == null)
                    && (item.PackagingTypeId == null || q.PackagingTypeId == null || q.PackagingTypeId == item.PackagingTypeId)
                    && (q.CustomerId == null || q.CustomerId == dlv.CustomerId)))
            .Sum(q => q.AcceptedQtyKg);
        if (accepted <= 0.001)
            return (false, "⛔ لا يمكن التسليم: لا توجد كمية مقبولة بفحص معتمد أو مستلمة مباشرة للمخزن لهذه الهوية.");

        double delivered = (from di in db.CustomerDeliveryItems.AsNoTracking()
                            join dd in db.CustomerDeliveries.AsNoTracking() on di.DeliveryId equals dd.Id
                            where dd.Id != dlv.Id && dd.IsApproved
                                && dd.CustomerId == dlv.CustomerId
                                && (dlv.OrderId == null || dd.OrderId == null || dd.OrderId == dlv.OrderId)
                                && di.ProductId == item.ProductId && di.LotId == item.LotId
                                && di.PackagingTypeId == item.PackagingTypeId
                            select di.QtyKg).Sum();
        double inCurrent = dlv.Items
            .Where(x => x.ProductId == item.ProductId && x.LotId == item.LotId
                && x.PackagingTypeId == item.PackagingTypeId)
            .Sum(x => x.QtyKg);
        if (delivered + inCurrent > accepted + 0.01)
        {
            string pname = db.Products.AsNoTracking().Where(p => p.Id == item.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? $"صنف #{item.ProductId}";
            return (false,
                $"⛔ لا يمكن تسليم {inCurrent:N1} كجم من «{pname}»: الكمية المقبولة/المستلمة مباشرة لنفس الدفعة والعبوة {accepted:N1} كجم" +
                (delivered > 0.001 ? $" وسُلِّم منها {delivered:N1} كجم سابقاً" : "") + ".\n" +
                "لا تتجاوز الكمية التي استلمها المخزن أو قبلتها الجودة.");
        }
        return (true, null);
    }

    public static (bool ok, string reason) FinishedGoodsIssueAllowed(DatesErpDbContext db, int orderId)
    {
        var checks = db.QualityChecks.AsNoTracking()
            .Where(c => c.OrderId == orderId && c.IsApproved).ToList();
        if (checks.Count == 0)
            return (false,
                "لا يمكن تسليم الإنتاج قبل اعتماد فحص الجودة — نفّذ الإقفال والفحص أولاً.");
        var blocked = checks.FirstOrDefault(c => c.Decision is Rejected or Quarantine);
        if (blocked != null)
        {
            string decisionAr = blocked.Decision == Rejected ? "مرفوض" : "حجز وتحريز مؤقت";
            return (false, $"لا يمكن تسليم الإنتاج: قرار الفحص {blocked.DocumentNumber} هو «{decisionAr}».");
        }
        double accepted = db.QualityCheckItems.AsNoTracking()
            .Where(i => checks.Select(c => c.Id).Contains(i.CheckId))
            .Sum(i => i.AcceptedQtyKg);
        if (accepted <= 0.001)
            return (false, "لا يمكن تسليم الإنتاج: المقبول المعتمد يساوي صفراً.");
        return (true, null);
    }
}
