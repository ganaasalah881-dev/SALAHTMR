using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §7/§8 — تسليم الإنتاج للعميل مع الحراس:
/// لا تسليم أكبر من رصيد العميل في مخزن التام، لا تسليم دفعة عميل لعميل آخر، لا تكرار التسليم.
/// </summary>
public class CustomerDeliveryService : ServiceBase, ICustomerDeliveryService
{
    public CustomerDeliveryService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering) { }

    /// <summary>§B105/P1 — حارس الكمية: صفر وسالب ممنوعان (كان السالب يخصم الرصيد ويعكس المسلَّم سالباً).</summary>
    private static string QtyGuard(CustomerDeliveryItemDto it)
    {
        if (it.QtyKg <= 0) return "كمية البند يجب أن تكون أكبر من صفر — لا تُقبل بنود صفرية أو سالبة.";
        if (it.PackageCount < 0) return "عدد العبوات لا يمكن أن يكون سالباً.";
        return null;
    }

    /// <summary>حراس البند المشتركة (الحفظ والتعديل): كمية + ملكية دفعة + تحويل + نوع تام.</summary>
    private void ItemGuards(int customerId, CustomerDeliveryItemDto it)
    {
        string q = QtyGuard(it);
        if (q != null) throw new DomainException(q, "INVALID_QTY");

        // §8 — الدفعة يجب أن تخص نفس العميل
        if (it.LotId is int lotId)
        {
            var lot = Db.Lots.FirstOrDefault(l => l.Id == lotId);
            if (lot == null) throw new DomainException("الدفعة غير موجودة.");
            if (lot.CustomerId != null && lot.CustomerId != customerId)
                throw new DomainException($"لا يمكن تسليم كمية العميل إلى عميل آخر — الدفعة {lot.LotCode} تخص عميلاً مختلفاً.", "CROSS_CUSTOMER");
        }

        // §تتبع الصنف: لا تسليم خلاص من دفعة سكري — التحويل الرسمي فقط حسب بطاقة المنتج
        ProductIdentityGuard.EnsureConversionAllowed(Db, it.ProductId, it.LotId);

        // §نظام الوحدات: التسليم للعميل منتجات تامة فقط (002)
        UnitsPolicy.RequireItemType(Db, it.ProductId, "Finished", "تسليم العميل");
    }

    public OpResult Save(int customerId, string deliveryDate, int? orderId, List<CustomerDeliveryItemDto> items,
        string notes = null, string reservedDocumentNumber = null)
    {
        Require("delivery", "Create");
        if (items == null || items.Count == 0) return OpResult.Fail("أدخل بنداً واحداً على الأقل.");
        foreach (var itq in items)
        {
            string q = QtyGuard(itq);
            if (q != null) return OpResult.Fail(q);
        }
        var customer = Db.Customers.FirstOrDefault(c => c.Id == customerId);
        if (customer == null) return OpResult.Fail("العميل غير موجود.");

        return RunOp(() =>
        {
            var dlv = new CustomerDelivery
            {
                DocumentNumber = string.IsNullOrWhiteSpace(reservedDocumentNumber) ? Numbering.Next("CD") : reservedDocumentNumber,
                CustomerId = customerId,
                OrderId = orderId,
                DeliveryDate = UiFormat.TryParseDate(deliveryDate, out var d) ? d : DateTime.Now,
                Notes = notes,
                Status = DocStatuses.Draft
            };
            foreach (var it in items)
            {
                ItemGuards(customerId, it);
                // §مؤجَّل بقرار: توحيد الكرتون/الكيلو عند التسليم يغيّر كميات تاريخية ويحتاج
                // قرار منتج (أي المدخلين مرجعي). موثق في أمر العمل — لا يُطبَّق ضمن هذه الحزمة.

                dlv.Items.Add(new CustomerDeliveryItem
                {
                    WarehouseId = it.WarehouseId,
                    ProductId = it.ProductId,
                    LotId = it.LotId,
                    PackagingTypeId = it.PackagingTypeId,
                    PackageCount = it.PackageCount,
                    QtyKg = it.QtyKg,
                    // §القاعدة 7: وزن الكرتون وقت التسليم — لا يتغير بتعريف العبوة لاحقاً
                    CartonWeightKg = UnitsPolicy.CartonWeight(Db, it.ProductId, it.PackagingTypeId)
                });
            }
            dlv.TotalQtyKg = dlv.Items.Sum(i => i.QtyKg);
            dlv.TotalCartons = dlv.Items.Sum(i => i.PackageCount);
            Db.CustomerDeliveries.Add(dlv);
            Db.SaveChanges();
            return OpResult.Success($"تم حفظ سند تسليم العميل {dlv.DocumentNumber}.", dlv.Id, dlv.DocumentNumber);
        });
    }

    /// <summary>§B105/P2 — تعديل سند مسودة: يستبدل البنود بنفس حراس الحفظ. المعتمد يُرفض.</summary>
    public OpResult Update(int deliveryId, int customerId, string deliveryDate, int? orderId, List<CustomerDeliveryItemDto> items,
        string notes = null)
    {
        Require("delivery", "Edit");
        if (items == null || items.Count == 0) return OpResult.Fail("أدخل بنداً واحداً على الأقل.");
        foreach (var itq in items)
        {
            string q = QtyGuard(itq);
            if (q != null) return OpResult.Fail(q);
        }
        var customer = Db.Customers.FirstOrDefault(c => c.Id == customerId);
        if (customer == null) return OpResult.Fail("العميل غير موجود.");

        return RunOp(() =>
        {
            var dlv = Db.CustomerDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == deliveryId)
                      ?? throw new DomainException("سند التسليم غير موجود.");
            if (dlv.IsApproved || dlv.Status == DocStatuses.Completed)
                throw new DomainException("السند معتمد — لا يُعدَّل. ألغِ الاعتماد أولاً ثم عدّل.", "APPROVED_LOCK");

            dlv.CustomerId = customerId;
            dlv.OrderId = orderId;
            dlv.DeliveryDate = UiFormat.TryParseDate(deliveryDate, out var d) ? d : dlv.DeliveryDate;
            if (notes is not null) dlv.Notes = notes;

            Db.CustomerDeliveryItems.RemoveRange(dlv.Items);
            dlv.Items.Clear();
            foreach (var it in items)
            {
                ItemGuards(customerId, it);
                dlv.Items.Add(new CustomerDeliveryItem
                {
                    WarehouseId = it.WarehouseId,
                    ProductId = it.ProductId,
                    LotId = it.LotId,
                    PackagingTypeId = it.PackagingTypeId,
                    PackageCount = it.PackageCount,
                    QtyKg = it.QtyKg,
                    CartonWeightKg = UnitsPolicy.CartonWeight(Db, it.ProductId, it.PackagingTypeId)
                });
            }
            dlv.TotalQtyKg = dlv.Items.Sum(i => i.QtyKg);
            dlv.TotalCartons = dlv.Items.Sum(i => i.PackageCount);
            Db.SaveChanges();
            return OpResult.Success($"تم تحديث سند التسليم {dlv.DocumentNumber}.", dlv.Id, dlv.DocumentNumber);
        });
    }

    /// <summary>§B105/P2 — حذف مسودة فقط: لم تخصم شيئاً فلا أثر مخزني — المعتمد لا يُحذف أبداً.</summary>
    public OpResult DeleteDraft(int deliveryId)
    {
        var dlv = Db.CustomerDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == deliveryId);
        if (dlv == null) return OpResult.Fail("سند التسليم غير موجود.");
        if (dlv.IsApproved || dlv.Status == DocStatuses.Completed)
            return OpResult.Fail("السند معتمد — لا يُحذف. إن لزم التصحيح: ألغِ الاعتماد (قيد عكسي) ثم عدّل.");
        if (!Session.Can("delivery", "Delete") && !Session.Can("delivery", "Edit") && !Session.Can("delivery", "Create"))
            throw new PermissionDeniedException("حذف مسودة تسليم");
        return RunOp(() =>
        {
            string docNo = dlv.DocumentNumber;
            Db.CustomerDeliveryItems.RemoveRange(dlv.Items);
            Db.CustomerDeliveries.Remove(dlv);
            Db.SaveChanges();
            return OpResult.Success($"تم حذف سند التسليم المسودة {docNo}.");
        });
    }

    public OpResult Approve(int deliveryId)
    {
        Require("delivery", "Approve");
        var dlv = Db.CustomerDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == deliveryId);
        if (dlv == null) return OpResult.Fail("سند التسليم غير موجود.");
        if (dlv.IsApproved) return OpResult.Fail("سند التسليم معتمد مسبقاً — لا يسمح بتكرار التسليم.");

        return RunOp(() =>
        {
            // §إصلاح حرج — بوابة الجودة المركزية.
            // كانت البوابة داخل if (dlv.OrderId is int) والواجهة تمرر null، فلم تُنفَّذ أبداً؛
            // وكانت تفحص IsApproved فقط لا Decision. أُثبت بالتشغيل أن دفعة «مرفوضة تماماً» سُلّمت للعميل.
            // §43: البوابة داخل المعاملة نفسها — لا نافذة زمنية بين فحص القرار وتنفيذ الأثر.
            // §1.50.72 P1-1: البوابة لكل بند تُفحص على **أمر البند نفسه** المشتق من دفعته —
            // كانت تمرر dlv.OrderId (أمر الرأس) لكل البنود، فسند يجمع أوامر متعددة (مسار
            // «تسليم كامل المتاح») يمرّر بنود أوامر أخرى حتى لو كان فحصها مرفوضاً/غائباً.
            // بند بلا دفعة يحتفظ بالسلوك الأصلي: أمر رأس السند.
            foreach (var item in dlv.Items)
            {
                int? gateOrderId = dlv.OrderId;
                var (ok, reason) = QualityGate.CustomerDeliveryAllowed(Db, gateOrderId, item.LotId, item.ProductId, item.PackagingTypeId, dlv.CustomerId);
                if (!ok) throw new DomainException(reason);
            }

            var defaultWhFg = WarehouseId("WFG");
            var usedOutRefs = new HashSet<string>();
            foreach (var item in dlv.Items)
            {
                if (item.QtyKg <= 0)
                    throw new DomainException("كمية البند يجب أن تكون أكبر من صفر — لا تُقبل بنود صفرية أو سالبة.", "INVALID_QTY");

                int whFg = item.WarehouseId ?? defaultWhFg;
                item.WarehouseId ??= whFg;

                int? effectiveLot = item.LotId;
                if (effectiveLot == null)
                {
                    var lotsWith = Db.StockBalances.AsNoTracking()
                        .Where(b => b.WarehouseId == whFg && b.ProductId == item.ProductId
                            && b.CustomerId == dlv.CustomerId && b.QtyKg > 0.001 && b.LotId != null
                            && b.PackagingTypeId == item.PackagingTypeId)
                        .Select(b => b.LotId!.Value).Distinct().ToList();
                    if (lotsWith.Count > 1)
                    {
                        string pname0 = Db.Products.AsNoTracking().Where(p => p.Id == item.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? $"صنف #{item.ProductId}";
                        throw new DomainException(
                            $"الصنف «{pname0}» له رصيد في {lotsWith.Count} دفعات مختلفة — حدّد الدفعة في البند حتى لا يُخصم اعتباطياً.",
                            "LOT_REQUIRED");
                    }
                    if (lotsWith.Count == 1) { effectiveLot = lotsWith[0]; item.LotId = effectiveLot; }
                }

                var balance = Db.StockBalances.Local.FirstOrDefault(b =>
                        b.WarehouseId == whFg && b.ProductId == item.ProductId
                        && (effectiveLot == null || b.LotId == effectiveLot) && b.CustomerId == dlv.CustomerId
                        && b.PackagingTypeId == item.PackagingTypeId)
                    ?? Db.StockBalances.FirstOrDefault(b =>
                        b.WarehouseId == whFg && b.ProductId == item.ProductId
                        && (effectiveLot == null || b.LotId == effectiveLot) && b.CustomerId == dlv.CustomerId
                        && b.PackagingTypeId == item.PackagingTypeId);
                var available = balance?.QtyKg ?? 0;
                if (available < item.QtyKg - 0.001)
                    throw new DomainException(
                        $"الكمية أكبر من رصيد العميل في مخزن التام.\nالمتاح: {available:N1} كجم — المطلوب: {item.QtyKg:N1} كجم",
                        "INSUFFICIENT_CUSTOMER_BALANCE");

                var (qok, qreason) = QualityGate.CustomerDeliveryQtyAllowed(Db, dlv, item);
                if (!qok) throw new DomainException(qreason);

                string outRef = dlv.DocumentNumber;
                int priorOut = Db.InventoryTransactions.Count(t =>
                    t.ReferenceDocType == ReferenceDocType.CustomerDelivery
                    && t.MovementType == MovementType.Outbound && t.WarehouseId == whFg
                    && t.ProductId == item.ProductId && t.LotId == item.LotId
                    && (t.ReferenceDocNumber == dlv.DocumentNumber
                        || t.ReferenceDocNumber.StartsWith(dlv.DocumentNumber + "#R")));
                if (priorOut > 0) outRef = $"{dlv.DocumentNumber}#R{priorOut + 1}";
                for (int bump = 2; !usedOutRefs.Add(outRef + "|" + item.ProductId + "|" + item.LotId + "|" + whFg); bump++)
                    outRef = $"{dlv.DocumentNumber}#R{priorOut + bump}";
                PostStockMovement(whFg, MovementType.Outbound, item.QtyKg, item.PackageCount,
                    ReferenceDocType.CustomerDelivery, outRef,
                    productId: item.ProductId, lotId: item.LotId, customerId: dlv.CustomerId,
                    orderId: dlv.OrderId, packagingTypeId: item.PackagingTypeId,
                    notes: $"تسليم عميل — سند {dlv.DocumentNumber}",
                    txnDate: dlv.DeliveryDate);

                if (item.LotId is int lotId)
                {
                    var lot = Db.Lots.First(l => l.Id == lotId);
                    lot.DeliveredQtyKg += item.QtyKg;
                }
            }
            dlv.IsApproved = true;
            dlv.IsPosted = true;
            dlv.Status = DocStatuses.Completed;
            dlv.PostedDate = dlv.ApprovedDate = DateTime.Now;
            dlv.ApprovedBy = Session?.UserId;

            foreach (var sItem in dlv.Items)
            {
                var (planIds, hasStandalone) = ResolvePlansForDeliveryLine(dlv, sItem);
                if (planIds.Count == 0)
                {
                    if (hasStandalone) continue; // أمر مستقل بلا خطة: لا يوجد خطة لتوزيع المسلَّم عليها
                    throw new DomainException(
                        "لا يمكن توزيع المسلَّم على الخطة: السند غير مرتبط بأمر إنتاج ولا بدفعة مرتبطة بأمر نشط — اربط السند أولاً.",
                        "PLAN_SCOPE");
                }
                SyncDeliveredAcrossPlans(planIds, dlv.CustomerId, sItem.ProductId, sItem.PackagingTypeId, sItem.QtyKg, dlv.DocumentNumber);
            }
            CompleteDocumentTasks(WorkflowDocTypes.CustomerDelivery, dlv.Id, WorkflowTaskTypes.CustomerDelivery, actionResult: "Approved", notes: $"اعتُمد سند التسليم {dlv.DocumentNumber}");
            Db.SaveChanges();
            return OpResult.Success($"تم اعتماد تسليم {dlv.TotalCartons:N0} كرتون وخصم الرصيد المقابل من حساب العميل.", dlv.Id, dlv.DocumentNumber);
        });
    }

    private (List<int> PlanIds, bool HasStandalone) ResolvePlansForDeliveryLine(CustomerDelivery dlv, CustomerDeliveryItem item)
    {
        var planIds = new List<int>();
        bool hasStandalone = false;

        if (dlv.OrderId is int explicitOid)
        {
            var ord = Db.ProductionOrders.AsNoTracking().FirstOrDefault(o => o.Id == explicitOid && o.Status != DocStatuses.Cancelled);
            if (ord != null)
            {
                if (ord.SourcePlanId is int sp) planIds.Add(sp);
                else if (ord.SourceType == "Standalone") hasStandalone = true;
            }
        }

        if (planIds.Count == 0 && item.LotId is int slid)
        {
            var allocOiIds = Db.Set<ProductionOrderItemAllocation>().AsNoTracking()
                .Where(a => a.LotId == slid).Select(a => a.OrderItemId).ToHashSet();
            var matchedOrders = (from oi in Db.ProductionOrderItems.AsNoTracking()
                                 join o in Db.ProductionOrders.AsNoTracking() on oi.OrderId equals o.Id
                                 where o.Status != DocStatuses.Cancelled && oi.Status != DocStatuses.Cancelled
                                     && oi.ProductId == item.ProductId
                                     && (oi.LotId == slid || (oi.LotId == null && allocOiIds.Contains(oi.Id)))
                                     && (oi.CustomerId == null || oi.CustomerId == dlv.CustomerId || o.CustomerId == dlv.CustomerId)
                                 orderby o.Id
                                 select new { o.SourcePlanId, o.SourceType }).ToList();
            foreach (var mo in matchedOrders)
            {
                if (mo.SourcePlanId is int sp && !planIds.Contains(sp)) planIds.Add(sp);
                else if (mo.SourcePlanId == null && mo.SourceType == "Standalone") hasStandalone = true;
            }
        }
        return (planIds, hasStandalone);
    }

    private void SyncDeliveredAcrossPlans(List<int> planIds, int customerId, int productId, int? packagingTypeId, double qtyKg, string docNo)
    {
        if (planIds.Count == 1)
        {
            PlanSync.SyncDeliveredForPlan(Db, planIds[0], customerId, productId, packagingTypeId, qtyKg, docNo);
            return;
        }
        double remaining = qtyKg;
        var items = Db.ProductionPlanItems
            .Where(i => planIds.Contains(i.PlanId) && i.CustomerId == customerId && i.ProductId == productId && i.ScheduledDate != null
                && (packagingTypeId == null || i.PackagingTypeId == packagingTypeId))
            .OrderBy(i => i.ScheduledDate).ThenBy(i => i.PlanId).ThenBy(i => i.PriorityNo)
            .ToList();
        foreach (var pi in items)
        {
            if (remaining <= 0) break;
            double slot = pi.PlannedQtyKg - pi.DeliveredQtyKg;
            if (slot <= 0) continue;
            double take = Math.Min(slot, remaining);
            pi.DeliveredQtyKg += take;
            remaining -= take;
        }
        if (remaining > 0.001)
            throw new DomainException(
                $"⛔ الكمية المسلَّمة في السند {docNo} تتجاوز سعة الخطط المرتبطة بـ {remaining:N1} كجم — لم يُوزَّع الفائض.\nراجع سعة الخطة قبل الاعتماد.",
                "PLAN_OVERFLOW");
    }

    public OpResult Unapprove(int deliveryId)
    {
        if (!Session.Can("delivery", "Cancel") && !Session.Can("delivery", "Approve"))
            throw new PermissionDeniedException("إلغاء اعتماد على وحدة delivery");
        var dlv = Db.CustomerDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == deliveryId);
        if (dlv == null) return OpResult.Fail("السند غير موجود.");
        if (!dlv.IsApproved) return OpResult.Fail("السند غير معتمد.");

        return RunOp(() =>
        {
            var defaultWhFg = WarehouseId("WFG");
            var usedRevRefs = new HashSet<string>();
            foreach (var item in dlv.Items)
            {
                int whFg = item.WarehouseId ?? defaultWhFg;
                int priorRev = Db.InventoryTransactions.Count(t =>
                    t.ReferenceDocType == ReferenceDocType.CustomerDelivery
                    && t.MovementType == MovementType.Inbound && t.WarehouseId == whFg
                    && t.ProductId == item.ProductId && t.LotId == item.LotId
                    && t.ReferenceDocNumber.StartsWith(dlv.DocumentNumber + "#REV"));
                string revRef = $"{dlv.DocumentNumber}#REV{priorRev + 1}";
                for (int bump = 2; !usedRevRefs.Add(revRef + "|" + item.ProductId + "|" + item.LotId + "|" + whFg); bump++)
                    revRef = $"{dlv.DocumentNumber}#REV{priorRev + bump}";
                PostStockMovement(whFg, MovementType.Inbound, item.QtyKg, item.PackageCount,
                    ReferenceDocType.CustomerDelivery, revRef,
                    productId: item.ProductId, lotId: item.LotId, customerId: dlv.CustomerId,
                    orderId: dlv.OrderId, packagingTypeId: item.PackagingTypeId,
                    notes: $"إلغاء سند التسليم {dlv.DocumentNumber} — قيد عكسي");
                if (item.LotId is int lotId)
                {
                    var lot = Db.Lots.FirstOrDefault(l => l.Id == lotId);
                    if (lot != null) lot.DeliveredQtyKg = Math.Max(0, lot.DeliveredQtyKg - item.QtyKg);
                }

                var (uplanIds, _) = ResolvePlansForDeliveryLine(dlv, item);
                if (uplanIds.Count == 1)
                {
                    PlanSync.UnsyncDeliveredForPlan(Db, uplanIds[0], dlv.CustomerId, item.ProductId, item.PackagingTypeId, item.QtyKg);
                }
                else if (uplanIds.Count > 1)
                {
                    double remRev = item.QtyKg;
                    var pItems = Db.ProductionPlanItems
                        .Where(i => uplanIds.Contains(i.PlanId) && i.CustomerId == dlv.CustomerId && i.ProductId == item.ProductId && i.ScheduledDate != null
                            && (item.PackagingTypeId == null || i.PackagingTypeId == item.PackagingTypeId))
                        .OrderByDescending(i => i.ScheduledDate).ThenByDescending(i => i.Id)
                        .ToList();
                    foreach (var pi in pItems)
                    {
                        if (remRev <= 0) break;
                        double take = Math.Min(Math.Max(pi.DeliveredQtyKg, 0), remRev);
                        if (take <= 0) continue;
                        pi.DeliveredQtyKg -= take;
                        remRev -= take;
                    }
                }
            }
            dlv.IsApproved = false;
            dlv.IsPosted = false;
            dlv.Status = DocStatuses.Draft;
            dlv.InvoicedQtyKg = 0;
            Db.SaveChanges();
            return OpResult.Success("تم إلغاء التسليم وإعادة الكميات إلى رصيد العميل.");
        });
    }
}
