using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace DatesErp.Application.Services;

public partial class PlanningService
{
    private static readonly JsonSerializerOptions ChangeRequestJsonOptions = new(JsonSerializerDefaults.Web);

    public OpResult SubmitFuturePlanChangeRequest(int planId, string reason, List<PlanItemDto> items, List<int> replaceSessionIds)
    {
        Require("planning", "Edit");
        var cleanReason = (reason ?? "").Trim();
        items ??= new List<PlanItemDto>();
        replaceSessionIds = (replaceSessionIds ?? new List<int>()).Where(id => id > 0).Distinct().ToList();
        if (cleanReason.Length < 10) return OpResult.Fail("سبب طلب تعديل الخطة إلزامي — 10 أحرف على الأقل.");
        if (replaceSessionIds.Count == 0) return OpResult.Fail("حدد جلسة مستقبلية واحدة على الأقل لاستبدالها أو إلغائها.");
        if (items.Any(i => i == null || i.SessionId == null || !replaceSessionIds.Contains(i.SessionId.Value)))
            return OpResult.Fail("كل بند مقترح يجب أن يرتبط بإحدى الجلسات المحددة للاستبدال.");

        return RunOp(() =>
        {
            var plan = Db.ProductionPlans.Include(p => p.Sessions).FirstOrDefault(p => p.Id == planId)
                ?? throw new DomainException("الخطة غير موجودة.");
            if (!plan.IsApproved || plan.IsClosed || plan.Status != DocStatuses.Approved)
                throw new DomainException("لا يقبل طلب تعديل إلا لخطة معتمدة ومفتوحة.");
            var sessions = plan.Sessions.Where(s => replaceSessionIds.Contains(s.Id)).ToList();
            if (sessions.Count != replaceSessionIds.Count || sessions.Any(s => s.Status != "Pending" || s.SessionDate.Date <= Db.BusinessNow.Date))
                throw new DomainException("طلب التغيير يقتصر على الجلسات المستقبلية المعلقة التابعة للخطة فقط.");
            var oldItems = Db.ProductionPlanItems.Where(i => i.PlanId == plan.Id && i.SessionId != null
                && replaceSessionIds.Contains(i.SessionId.Value)).ToList();
            var oldIds = oldItems.Select(i => i.Id).ToList();
            if (oldItems.Any(i => i.ProducedQtyKg > 0 || i.AcceptedQtyKg > 0 || i.DeliveredQtyKg > 0)
                || (oldIds.Count > 0 && Db.ProductionOrderItems.Any(i => i.PlanItemId != null && oldIds.Contains(i.PlanItemId.Value)
                    && i.Status != DocStatuses.Cancelled)))
                throw new DomainException("لا يمكن طلب تعديل جلسة دخلت أمراً قائماً أو إنتاجاً فعلياً.");
            ValidateRequestItems(plan, items);
            var oldCustomers = oldItems.Where(i => i.CustomerId != null).Select(i => i.CustomerId!.Value).ToHashSet();
            if (items.Any(i => i.CustomerId is int cid && !oldCustomers.Contains(cid) && !HasRawSource(i)))
                throw new DomainException("عند تغيير العميل يجب تحديد خام صالح ومملوك للعميل البديل.", "ALLOCATION_OWNERSHIP");
            if (Db.ProductionPlanChangeRequests.Any(r => r.PlanId == planId && r.ChangeType == "FuturePlan" && r.Status == DocStatuses.Submitted))
                throw new DomainException("توجد بالفعل طلبات تعديل مستقبلية معلقة لهذه الخطة؛ اعتمدها أو ارفضها أولاً.");
            int revision = Db.ProductionPlanRevisions.Where(r => r.PlanId == planId).Select(r => (int?)r.RevisionNo).Max() ?? 0;
            var request = new ProductionPlanChangeRequest
            {
                DocumentNumber = Numbering.Next("PCR"), PlanId = planId, BaseRevisionNo = revision,
                ChangeType = "FuturePlan", Reason = cleanReason,
                ReplaceSessionIdsJson = JsonSerializer.Serialize(replaceSessionIds, ChangeRequestJsonOptions),
                ProposedItemsJson = JsonSerializer.Serialize(items, ChangeRequestJsonOptions),
                Status = DocStatuses.Submitted, IsApproved = false, CreatedBy = Session?.UserId,
                CreatedDate = Db.BusinessNow, Notes = cleanReason
            };
            Db.ProductionPlanChangeRequests.Add(request);
            Db.SaveChanges();
            return OpResult.Success("تم تسجيل طلب التعديل دون تغيير الخطة؛ ينتظر الاعتماد.", request.Id, request.DocumentNumber);
        });
    }

    public OpResult SubmitOrderRemainderChangeRequest(int orderItemId, string reason, List<PlanItemDto> replacementItems)
    {
        Require("planning", "Edit");
        var cleanReason = (reason ?? "").Trim();
        replacementItems ??= new List<PlanItemDto>();
        if (cleanReason.Length < 10) return OpResult.Fail("سبب نقل المتبقي إلزامي — 10 أحرف على الأقل.");
        if (replacementItems.Count == 0) return OpResult.Fail("أدخل بنود الإنتاج البديلة المقترحة للمتبقي.");
        return RunOp(() =>
        {
            var source = Db.ProductionOrderItems.Include(i => i.Allocations).FirstOrDefault(i => i.Id == orderItemId)
                ?? throw new DomainException("بند أمر الإنتاج غير موجود.");
            var order = Db.ProductionOrders.FirstOrDefault(o => o.Id == source.OrderId)
                ?? throw new DomainException("أمر الإنتاج غير موجود.");
            if (order.SourcePlanId is not int planId || source.PlanItemId is not int planItemId)
                throw new DomainException("طلب نقل المتبقي متاح فقط للبند المرتبط بخطة إنتاج.");
            if (order.IsClosed || order.Status is DocStatuses.Completed or DocStatuses.Closed or DocStatuses.Cancelled)
                throw new DomainException("الأمر مغلق أو مكتمل أو ملغي؛ لا يمكن نقل متبقيه.");
            if (order.Status is not (DocStatuses.InProgress or DocStatuses.Stopped) && source.ProducedQtyKg <= 0.001)
                throw new DomainException("لا يقبل هذا الطلب إلا لأمر بدأ إنتاجه.");
            double remaining = Math.Max(0, source.PlannedQtyKg - source.ProducedQtyKg - source.TransferredOutQtyKg);
            if (remaining <= 0.001) throw new DomainException("لا يوجد متبقٍ غير منتج يمكن نقله.");
            if (replacementItems.Any(i => i == null || i.SessionId == null))
                throw new DomainException("اربط كل بند بديل بجلسة مستقبلية معلقة.");
            if (Math.Abs(replacementItems.Sum(i => Math.Max(0, i.PlannedQtyKg)) - remaining) > 0.01)
                throw new DomainException("إجمالي البدائل يجب أن يساوي المتبقي غير المنتج في أمر المصدر.");
            var plan = Db.ProductionPlans.Include(p => p.Sessions).Include(p => p.CustomerScopes)
                .FirstOrDefault(p => p.Id == planId) ?? throw new DomainException("خطة الأمر غير موجودة.");
            if (!plan.IsApproved || plan.IsClosed || plan.Status != DocStatuses.Approved)
                throw new DomainException("لا يمكن نقل المتبقي إلى خطة غير معتمدة أو مقفلة.");
            var planItem = Db.ProductionPlanItems.FirstOrDefault(i => i.Id == planItemId && i.PlanId == planId)
                ?? throw new DomainException("بند الخطة الأصلي غير موجود.");
            var sessionIds = replacementItems.Select(i => i.SessionId!.Value).Distinct().ToList();
            var targets = plan.Sessions.Where(s => sessionIds.Contains(s.Id)).ToList();
            if (targets.Count != sessionIds.Count || targets.Any(s => s.Status != "Pending" || s.SessionDate.Date <= Db.BusinessNow.Date))
                throw new DomainException("النقل مسموح إلى جلسات مستقبلية معلقة تابعة للخطة نفسها فقط.");
            if (targets.Any(s => Db.ProductionPlanItems.Any(i => i.SessionId == s.Id && i.PlanId == planId && !i.IsClosed)))
                throw new DomainException("اختر جلسة مستقبلية فارغة؛ لا يستبدل الطلب بنوداً أخرى.");
            ValidateRequestItems(plan, replacementItems);
            int? sourceCustomer = source.CustomerId ?? order.CustomerId;
            if (replacementItems.Any(i => i.CustomerId != sourceCustomer && !HasRawSource(i)))
                throw new DomainException("عند نقل المتبقي إلى عميل آخر يجب تحديد خام صالح ومملوك لذلك العميل.", "ALLOCATION_OWNERSHIP");
            if (Db.ProductionPlanChangeRequests.Any(r => r.SourceOrderItemId == orderItemId && r.Status == DocStatuses.Submitted))
                throw new DomainException("يوجد طلب نقل متبقٍ معلق لهذا البند بالفعل.");
            int revision = Db.ProductionPlanRevisions.Where(r => r.PlanId == planId).Select(r => (int?)r.RevisionNo).Max() ?? 0;
            var request = new ProductionPlanChangeRequest
            {
                DocumentNumber = Numbering.Next("PCR"), PlanId = planId, BaseRevisionNo = revision,
                ChangeType = "OrderRemainder", Reason = cleanReason,
                ReplaceSessionIdsJson = JsonSerializer.Serialize(sessionIds, ChangeRequestJsonOptions),
                ProposedItemsJson = JsonSerializer.Serialize(replacementItems, ChangeRequestJsonOptions),
                SourceOrderId = order.Id, SourceOrderItemId = source.Id, RemainingQtyKgAtRequest = remaining,
                Status = DocStatuses.Submitted, IsApproved = false, CreatedBy = Session?.UserId,
                CreatedDate = Db.BusinessNow, Notes = cleanReason
            };
            Db.ProductionPlanChangeRequests.Add(request);
            Db.SaveChanges();
            return OpResult.Success("تم تسجيل طلب نقل المتبقي دون تعديل الأمر أو الخطة؛ ينتظر الاعتماد.", request.Id, request.DocumentNumber);
        });
    }

    public List<PlanChangeRequestDto> GetPlanChangeRequests(int? planId = null, bool pendingOnly = true)
    {
        Require("planning", "View");
        var query = Db.ProductionPlanChangeRequests.AsNoTracking().AsQueryable();
        if (planId is int pId) query = query.Where(r => r.PlanId == pId);
        if (pendingOnly) query = query.Where(r => r.Status == DocStatuses.Submitted);
        var requests = query.OrderByDescending(r => r.CreatedDate).ThenByDescending(r => r.Id).ToList();
        var planIds = requests.Select(r => r.PlanId).Distinct().ToList();
        var plans = Db.ProductionPlans.AsNoTracking().Where(p => planIds.Contains(p.Id)).ToDictionary(p => p.Id);
        var userIds = requests.Where(r => r.CreatedBy != null).Select(r => r.CreatedBy!.Value).Distinct().ToList();
        var users = Db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionary(u => u.Id, u => u.FullName ?? u.UserName);
        var orderIds = requests.Where(r => r.SourceOrderId != null).Select(r => r.SourceOrderId!.Value).Distinct().ToList();
        var orders = Db.ProductionOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionary(o => o.Id);
        var rows = new List<PlanChangeRequestDto>();
        foreach (var r in requests)
        {
            plans.TryGetValue(r.PlanId, out var plan);
            var proposed = DeserializeItems(r.ProposedItemsJson);
            var targetIds = proposed.Where(i => i.CustomerId != null).Select(i => i.CustomerId!.Value).Distinct().ToList();
            var targetNames = Db.Customers.AsNoTracking().Where(c => targetIds.Contains(c.Id)).Select(c => c.CustomerName).Distinct().ToList();
            int? sourceCustomerId = r.SourceOrderItemId is int srcItem
                ? Db.ProductionOrderItems.AsNoTracking().Where(i => i.Id == srcItem).Select(i => i.CustomerId).FirstOrDefault()
                : null;
            if (sourceCustomerId == null && r.SourceOrderId is int srcOrder && orders.TryGetValue(srcOrder, out var sourceOrder))
                sourceCustomerId = sourceOrder.CustomerId;
            var sessionIds = DeserializeIds(r.ReplaceSessionIdsJson);
            var sessionRows = Db.ProductionPlanSessions.AsNoTracking().Where(s => sessionIds.Contains(s.Id))
                .OrderBy(s => s.SessionDate).Select(s => new { s.SessionDate, s.SequenceNo }).ToList();
            rows.Add(new PlanChangeRequestDto
            {
                Id = r.Id, PlanId = r.PlanId, RequestNumber = r.DocumentNumber,
                PlanNumber = plan?.DocumentNumber, PlanTitle = plan?.PlanTitle, Reason = r.Reason,
                ChangeType = r.ChangeType, SourceOrderId = r.SourceOrderId,
                SourceOrderNumber = r.SourceOrderId is int orderId && orders.TryGetValue(orderId, out var order) ? order.DocumentNumber : null,
                SourceCustomerName = sourceCustomerId is int customerId ? Db.Customers.AsNoTracking().Where(c => c.Id == customerId).Select(c => c.CustomerName).FirstOrDefault() : null,
                TargetCustomerName = string.Join("، ", targetNames), RemainingQtyKgAtRequest = r.RemainingQtyKgAtRequest,
                Status = r.Status, RequesterName = r.CreatedBy is int uid && users.TryGetValue(uid, out var user) ? user : null,
                RequestedDate = r.CreatedDate, ProposedItemsCount = proposed.Count,
                SessionSummary = string.Join("، ", sessionRows.Select(s => s.SessionDate.ToString("yyyy-MM-dd") + " · جلسة " + s.SequenceNo))
            });
        }
        return rows;
    }

    public OpResult ApprovePlanChangeRequest(int requestId)
    {
        Require("planning", "Approve");
        return RunOp(() =>
        {
            var request = Db.ProductionPlanChangeRequests.FirstOrDefault(r => r.Id == requestId)
                ?? throw new DomainException("طلب التعديل غير موجود.");
            if (request.Status != DocStatuses.Submitted || request.IsApproved)
                throw new DomainException("الطلب ليس معلقاً للاعتماد.");
            if (request.CreatedBy == Session?.UserId && !FourEyesOff())
                throw new DomainException("لا يجوز لمقدم الطلب اعتماده عند تفعيل فصل المنشئ عن المعتمد.");
            var items = DeserializeItems(request.ProposedItemsJson);
            var sessionIds = DeserializeIds(request.ReplaceSessionIdsJson);
            if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length < 10 || sessionIds.Count == 0
                || (request.ChangeType == "OrderRemainder" && items.Count == 0))
                throw new DomainException("بيانات طلب التعديل ناقصة أو غير صالحة؛ أعد تقديمه.");
            int currentRevision = Db.ProductionPlanRevisions.Where(r => r.PlanId == request.PlanId).Select(r => (int?)r.RevisionNo).Max() ?? 0;
            if (currentRevision != request.BaseRevisionNo) throw new DomainException("تغير إصدار الخطة منذ تقديم الطلب؛ أعد تقديمه على الإصدار الحالي.");
            var plan = Db.ProductionPlans.Include(p => p.CustomerScopes).FirstOrDefault(p => p.Id == request.PlanId)
                ?? throw new DomainException("الخطة المرتبطة بالطلب غير موجودة.");
            var newCustomers = items.Where(i => i.CustomerId != null).Select(i => i.CustomerId!.Value).Distinct()
                .Where(id => !plan.CustomerScopes.Any(s => s.IsEnabled && s.CustomerId == id)).ToList();
            foreach (var customerId in newCustomers)
            {
                if (!Db.Customers.AsNoTracking().Any(c => c.Id == customerId && c.IsActive))
                    throw new DomainException($"العميل البديل رقم {customerId} غير موجود أو غير نشط.");
                plan.CustomerScopes.Add(new ProductionPlanCustomerScope
                {
                    PlanId = plan.Id, CustomerId = customerId, ScopeMode = "Multi",
                    PriorityNo = plan.CustomerScopes.Select(s => s.PriorityNo).DefaultIfEmpty(0).Max() + 1,
                    IsEnabled = true
                });
            }
            if (newCustomers.Count > 0 && plan.ScopeMode == "Single") { plan.ScopeMode = "Multi"; plan.SingleCustomerId = null; }
            if (request.ChangeType == "OrderRemainder")
            {
                var transfer = PrepareOrderRemainderTransfer(request, items);
                if (!transfer.ok) throw new DomainException(transfer.reason);
            }
            else if (request.ChangeType != "FuturePlan") throw new DomainException("نوع طلب التعديل غير مدعوم.");
            request.Status = DocStatuses.Approved; request.IsApproved = true;
            request.ApprovedBy = Session?.UserId; request.ApprovedDate = Db.BusinessNow;
            request.ModifiedBy = Session?.UserId; request.ModifiedDate = Db.BusinessNow;
            var applier = new PlanningService(Db, Session, Numbering) { JoinParentTransaction = true };
            var applied = applier.AmendFuturePlan(request.PlanId, request.Reason, items, sessionIds);
            if (!applied.Ok) throw new DomainException(applied.Message ?? "تعذر تطبيق التعديل.");
            var existingIds = DeserializeIds(request.AppliedPlanItemIdsJson).ToHashSet();
            var appliedIds = Db.ProductionPlanItems.AsNoTracking().Where(i => i.PlanId == request.PlanId && i.ChangeRequestId == request.Id)
                .Select(i => i.Id).ToList();
            if (appliedIds.Count == 0)
                appliedIds = Db.ProductionPlanItems.AsNoTracking().Where(i => i.PlanId == request.PlanId && i.SessionId != null
                    && sessionIds.Contains(i.SessionId.Value) && !existingIds.Contains(i.Id)).Select(i => i.Id).ToList();
            request.AppliedPlanItemIdsJson = JsonSerializer.Serialize(appliedIds, ChangeRequestJsonOptions);
            foreach (var id in appliedIds)
            {
                var item = Db.ProductionPlanItems.FirstOrDefault(i => i.Id == id);
                if (item != null) item.ChangeRequestId = request.Id;
            }
            Db.SaveChanges();
            return OpResult.Success("تم اعتماد طلب التعديل وتطبيقه بإصدار خطة جديد.", request.Id, request.DocumentNumber);
        });
    }

    public OpResult RejectPlanChangeRequest(int requestId, string reason)
    {
        Require("planning", "Approve");
        var cleanReason = (reason ?? "").Trim();
        if (cleanReason.Length < 10) return OpResult.Fail("سبب رفض الطلب إلزامي — 10 أحرف على الأقل.");
        var request = Db.ProductionPlanChangeRequests.FirstOrDefault(r => r.Id == requestId);
        if (request == null) return OpResult.Fail("طلب التعديل غير موجود.");
        if (request.Status != DocStatuses.Submitted || request.IsApproved) return OpResult.Fail("لا يمكن رفض طلب سبق البت فيه.");
        return RunOp(() =>
        {
            request.Status = "Rejected"; request.RejectedBy = Session?.UserId; request.RejectedDate = Db.BusinessNow;
            request.RejectionReason = cleanReason; request.StatusReason = cleanReason;
            request.ModifiedBy = Session?.UserId; request.ModifiedDate = Db.BusinessNow;
            Db.SaveChanges();
            return OpResult.Success("تم رفض طلب التعديل مع حفظ سبب الرفض.", request.Id, request.DocumentNumber);
        });
    }

    private (bool ok, string reason) PrepareOrderRemainderTransfer(ProductionPlanChangeRequest request, List<PlanItemDto> items)
    {
        if (request.SourceOrderItemId is not int itemId || request.SourceOrderId is not int orderId) return (false, "طلب النقل لا يحتوي مرجع أمر/بند صالحاً.");
        var source = Db.ProductionOrderItems.Include(i => i.Allocations).FirstOrDefault(i => i.Id == itemId && i.OrderId == orderId);
        var order = Db.ProductionOrders.FirstOrDefault(o => o.Id == orderId);
        if (source == null || order == null || order.SourcePlanId != request.PlanId) return (false, "أمر المصدر أو بند المصدر لم يعد صالحاً.");
        if (order.IsClosed || order.Status is DocStatuses.Completed or DocStatuses.Closed or DocStatuses.Cancelled) return (false, "أمر المصدر أُغلق أو اكتمل؛ لا يمكن نقل متبقيه.");
        double remaining = Math.Max(0, source.PlannedQtyKg - source.ProducedQtyKg - source.TransferredOutQtyKg);
        if (remaining <= 0.001 || request.RemainingQtyKgAtRequest is not double requested || Math.Abs(remaining - requested) > 0.01)
            return (false, "تغير المتبقي منذ تقديم الطلب؛ أعد احتساب الكمية وقدم طلباً جديداً.");
        var targetIds = items.Select(i => i.SessionId!.Value).Distinct().ToList();
        var plan = Db.ProductionPlans.Include(p => p.Sessions).Include(p => p.Items).ThenInclude(i => i.Allocations).FirstOrDefault(p => p.Id == request.PlanId);
        if (plan == null) return (false, "الخطة المرتبطة غير موجودة.");
        if (plan.Sessions.Where(s => targetIds.Contains(s.Id)).Any(s => s.Status != "Pending" || s.SessionDate.Date <= Db.BusinessNow.Date))
            return (false, "إحدى الجلسات المستهدفة لم تعد مستقبلية ومعلقة.");
        if (targetIds.Any(id => plan.Items.Any(i => i.SessionId == id && !i.IsClosed))) return (false, "لا تُستبدل بنود جلسة أخرى؛ اختر جلسات فارغة.");
        var sourcePlanItem = plan.Items.FirstOrDefault(i => i.Id == source.PlanItemId);
        if (sourcePlanItem == null) return (false, "بند الخطة الأصلي غير موجود.");
        source.TransferredOutQtyKg += remaining;
        int cartons = Math.Max(0, source.PlannedCartons - source.ProducedCartons - source.TransferredOutCartons);
        source.TransferredOutCartons += cartons; source.RemainderChangeRequestId = request.Id;
        sourcePlanItem.AdjustedOutQtyKg += remaining; sourcePlanItem.AdjustedOutCartons += cartons;
        double beforeTransferQty = Math.Max(0.001, sourcePlanItem.PlannedQtyKg - sourcePlanItem.ProducedQtyKg - sourcePlanItem.AdjustedOutQtyKg + remaining);
        double ratio = Math.Min(1, remaining / beforeTransferQty);
        foreach (var allocation in sourcePlanItem.Allocations.Where(a => a.AllocatedQtyKg > 0.001 && a.Status != "Released"))
        {
            double free = Math.Max(0, allocation.AllocatedQtyKg - allocation.ConsumedQtyKg - allocation.ReleasedQtyKg);
            double release = Math.Min(free, free * ratio);
            allocation.ReleasedQtyKg += release;
            allocation.Status = allocation.AllocatedQtyKg - allocation.ConsumedQtyKg - allocation.ReleasedQtyKg <= 0.001
                ? (allocation.ConsumedQtyKg > 0.001 ? "Consumed" : "Released") : "PartiallyConsumed";
        }
        ApplyLotReservations(plan);
        return (true, null);
    }

    private void ValidateRequestItems(ProductionPlan plan, List<PlanItemDto> items)
    {
        var ids = items.Where(i => i.SessionId != null).Select(i => i.SessionId!.Value).Distinct().ToHashSet();
        var sessions = plan.Sessions.Where(s => ids.Contains(s.Id)).ToDictionary(s => s.Id);
        if (sessions.Count != ids.Count) throw new DomainException("بند مقترح مرتبط بجلسة لا تتبع الخطة.");
        foreach (var item in items)
        {
            if (item.CustomerId is not int customerId || !Db.Customers.AsNoTracking().Any(c => c.Id == customerId && c.IsActive))
                throw new DomainException("كل بند بديل يحتاج عميلاً نشطاً محدداً.");
            if (item.PlannedQtyKg <= 0 || item.PlannedCartons <= 0) throw new DomainException("الكمية والكراتين يجب أن تكونا موجبتين.");
            ProductIdentityGuard.EnsurePlanningLink(Db, item.ProductId, item.LotId, item.SelectedRawProductId);
            ValidateRawOwnerForCustomer(item, customerId);
            var target = sessions[item.SessionId!.Value];
            item.ScheduledDate = target.SessionDate.ToString(UiFormat.DatePattern);
            item.SuggestedShiftId = target.ShiftId; item.SuggestedLineId = target.LineId;
        }
    }

    private static bool HasRawSource(PlanItemDto item)
        => item.LotId is > 0 || (item.Allocations ?? new List<PlanItemAllocationDto>()).Any(a => a.AllocatedQtyKg > 0.001);

    private void ValidateRawOwnerForCustomer(PlanItemDto item, int customerId)
    {
        var lotIds = (item.Allocations ?? new List<PlanItemAllocationDto>()).Where(a => a.AllocatedQtyKg > 0).Select(a => a.LotId)
            .Concat(item.LotId is int singleLotId ? new[] { singleLotId } : Array.Empty<int>()).Distinct().ToList();
        foreach (var lotId in lotIds)
        {
            var owner = (from lot in Db.Lots.AsNoTracking()
                         join shipment in Db.Shipments.AsNoTracking() on lot.ShipmentId equals shipment.Id into sj
                         from shipment in sj.DefaultIfEmpty()
                         where lot.Id == lotId
                         select lot.CustomerId ?? (int?)shipment.CustomerId).FirstOrDefault();
            if (owner != customerId) throw new DomainException($"الدفعة البديلة #{lotId} ليست مملوكة للعميل المحدد؛ اختر خاماً صالحاً ومملوكاً له.", "ALLOCATION_OWNERSHIP");
        }
        if ((item.Allocations ?? new List<PlanItemAllocationDto>()).Any(a => a.CustomerId != null && a.CustomerId != customerId))
            throw new DomainException("عميل تخصيص الخام لا يطابق عميل البند البديل.", "ALLOCATION_OWNERSHIP");
    }

    private static List<PlanItemDto> DeserializeItems(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<PlanItemDto>>(json, ChangeRequestJsonOptions) ?? new(); }
        catch (JsonException) { throw new DomainException("بيانات طلب التغيير تالفة؛ لا يمكن تطبيقها."); }
    }
    private static List<int> DeserializeIds(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<int>>(json, ChangeRequestJsonOptions) ?? new(); }
        catch (JsonException) { throw new DomainException("قائمة جلسات طلب التغيير تالفة."); }
    }
}
