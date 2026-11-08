using DatesErp.Application.Services;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using DatesErp.Infrastructure.Session;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B96 — فصل أمر تسليم الإنتاج (إدارة الإنتاج) عن أمر الاستلام (المخازن):
/// مصدر التسليم التشغيلي (الإنتاج الفعلي) + فصل الاستلام + إقفال الخطة عند التحرير + حراس الصلاحيات.
/// </summary>
public class B96DeliveryTests
{
    private static (int orderId, int lotId) SeedAndClose(TestHost host, int producedKg = 500, int producedCtn = 67, bool sendToQuality = false)
    {
        var db = host.Get<DatesErpDbContext>();
        FullWorkflowTests.SeedQuickOrder(host, db, out var oid, out var lotId);
        var close = host.Get<IExecutionService>()
            .CloseProductionDay(oid, producedKg, producedCtn, 0, 0, 0, false, new List<DowntimeDto>(), sendToQuality);
        Assert.True(close.Ok, close.Message);
        return (oid, lotId);
    }

    private static (int planId, int orderId, int lotId) SeedPlanWithProduction(TestHost host, bool addPendingSecondOrder = false)
    {
        var db = host.Get<DatesErpDbContext>();
        int whAux = db.Warehouses.Single(w => w.WarehouseCode == "WAUX").Id;
        db.StockBalances.AddRange(
            new StockBalance { WarehouseId = whAux, MaterialId = 1, QtyKg = 5000 },
            new StockBalance { WarehouseId = whAux, MaterialId = 2, QtyKg = 5000 });
        db.SaveChanges();

        var rcv = host.Get<IReceivingService>();
        var s = rcv.SaveShipment(1, "2026-08-10", "2026-08-10", new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = 1, PackageCount = 100, UnitWeightKg = 20, QtyKg = 2000 } });
        rcv.ApproveShipment(s.Id);
        int lot = db.Lots.OrderBy(l => l.Id).Last().Id;
        int secondLot = lot;
        if (addPendingSecondOrder)
        {
            var s2 = rcv.SaveShipment(1, "2026-08-11", "2026-08-11", new List<ShipmentItemDto>
            { new() { TreatmentRequired = false, ProductId = 1, PackageCount = 100, UnitWeightKg = 20, QtyKg = 2000 } });
            Assert.True(s2.Ok, s2.Message);
            Assert.True(rcv.ApproveShipment(s2.Id).Ok);
            secondLot = db.Lots.OrderBy(l => l.Id).Last().Id;
        }

        var plan = host.Get<IPlanningService>();
        var planItems = new List<PlanItemDto>
        {
            new() { SourceType = "FromReceiving", LotId = lot, CustomerId = 1, ProductId = 3, PlannedQtyKg = 500, PlannedCartons = 67 },
        };
        if (addPendingSecondOrder)
            planItems.Add(new() { SourceType = "FromReceiving", LotId = secondLot, CustomerId = 1, ProductId = 3, PlannedQtyKg = 500, PlannedCartons = 67 });
        var p = plan.SavePlan("خطة B96", "Daily", "2026-08-20", "2026-08-20", 1, 1, planItems);
        Assert.True(p.Ok, p.Message);
        Assert.True(plan.ApprovePlan(p.Id).Ok);
        var planItemIds = db.ProductionPlanItems.AsNoTracking().Where(i => i.PlanId == p.Id).OrderBy(i => i.Id).Select(i => i.Id).ToList();

        var orders = host.Get<IProductionOrderService>();
        host.SetBusinessDate("2026-08-20"); // Arrange production on its scheduled business day, not the machine date.
        var o = orders.SaveOrder("FromPlan", p.Id, 1, "2026-08-20", 1, 1, new List<OrderItemDto>
        { new() { PlanItemId = planItemIds[0], LotId = lot, CustomerId = 1, ProductId = 3, PlannedQtyKg = 500, PlannedCartons = 67 } });
        Assert.True(o.Ok, o.Message);
        if (addPendingSecondOrder)
        {
            var pending = orders.SaveOrder("FromPlan", p.Id, 1, "2026-08-20", 1, 1, new List<OrderItemDto>
            { new() { PlanItemId = planItemIds[1], LotId = secondLot, CustomerId = 1, ProductId = 3, PlannedQtyKg = 500, PlannedCartons = 67 } });
            Assert.True(pending.Ok, pending.Message);
            // Keep the second order pending: no approval, execution, or delivery is performed for it.
        }
        Assert.True(orders.ApproveOrder(o.Id).Ok);

        var c = host.Get<IExecutionService>()
            .CloseProductionDay(o.Id, 500, 67, 0, 0, 0, false, new List<DowntimeDto>(), false, null);
        Assert.True(c.Ok, c.Message);
        return (p.Id, o.Id, lot);
    }

    private static (int orderId, int lotA, int lotB, int custB) SeedMultiCustomer(TestHost host)
    {
        var db = host.Get<DatesErpDbContext>();
        int whAux = db.Warehouses.Single(w => w.WarehouseCode == "WAUX").Id;
        db.StockBalances.AddRange(
            new StockBalance { WarehouseId = whAux, MaterialId = 1, QtyKg = 9000 },
            new StockBalance { WarehouseId = whAux, MaterialId = 2, QtyKg = 9000 });
        db.SaveChanges();

        var rcv = host.Get<IReceivingService>();
        var s1 = rcv.SaveShipment(1, "2026-08-10", "2026-08-10", new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = 1, PackageCount = 100, UnitWeightKg = 20, QtyKg = 2000 } });
        rcv.ApproveShipment(s1.Id);
        int lotA = db.Lots.OrderBy(l => l.Id).Last().Id;
        var cust2 = host.Get<MasterDataService>().SaveCustomer(null, "B96-C2", "عميل ثانٍ", "جملة", "777", "-", true);
        var s2 = rcv.SaveShipment(cust2.Id, "2026-08-10", "2026-08-10", new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = 1, PackageCount = 100, UnitWeightKg = 20, QtyKg = 2000 } });
        rcv.ApproveShipment(s2.Id);
        int lotB = db.Lots.OrderBy(l => l.Id).Last().Id;

        var orders = host.Get<IProductionOrderService>();
        var o = LegacyOrderFixture.ImportApproved(host, null, null, "2026-08-21", 1, 1, new List<OrderItemDto>
        {
            new() { LotId = lotA, CustomerId = 1, ProductId = 3, PlannedQtyKg = 500, PlannedCartons = 67 },
            new() { LotId = lotB, CustomerId = cust2.Id, ProductId = 3, PlannedQtyKg = 500, PlannedCartons = 67 }
        });
        Assert.True(o.Ok, o.Message);
        Assert.True(db.ProductionOrders.Single(x => x.Id == o.Id).IsApproved); // Historical approval, not new issuance.
        return (o.Id, lotA, lotB, cust2.Id);
    }

    private static int SaveApprovedCheck(TestHost host, int orderId, List<QualityItemDto> items)
    {
        var quality = host.Get<IQualityService>();
        var q = quality.SaveCheck(orderId, null, "2026-08-23", "نهائي", items);
        Assert.True(q.Ok, q.Message);
        Assert.True(quality.ApproveCheck(q.Id).Ok);
        return q.Id;
    }

    private static (int orderId, int executionId, int checkId, int orderItemId, int lotId, int packagingId, int warehouseId, int byProductStockId)
        SeedApprovedDirectReceipt(TestHost host, bool approved = true, bool directRoute = true)
    {
        var (orderId, lotId) = SeedAndClose(host);
        var db = host.Get<DatesErpDbContext>();
        var execution = db.ProductionExecutions.Single(e => e.OrderId == orderId && e.Status == DocStatuses.Completed);
        var orderItem = db.ProductionOrderItems.Single(i => i.OrderId == orderId);
        double cartonWeight = UnitsPolicy.CartonWeight(db, orderItem.ProductId, orderItem.PackagingTypeId);
        var check = new QualityCheck
        {
            DocumentNumber = "QC-DIRECT-" + Guid.NewGuid().ToString("N")[..6],
            OrderId = orderId, ExecutionId = execution.Id,
            Status = approved ? DocStatuses.Approved : DocStatuses.Completed,
            IsApproved = approved, Decision = "Passed",
            TotalCheckedKg = 500, AcceptedKg = 500, RejectedKg = 0,
            TotalCheckedCartons = 67, AcceptedCartons = 67, RejectedCartons = 0,
            Items = new List<QualityCheckItem>
            {
                new()
                {
                    OrderItemId = orderItem.Id, CustomerId = orderItem.CustomerId,
                    PackagingTypeId = orderItem.PackagingTypeId, ProductId = orderItem.ProductId, LotId = orderItem.LotId,
                    CheckedQtyKg = 500, AcceptedQtyKg = 500, RejectedQtyKg = 0,
                    CheckedCartons = 67, AcceptedCartons = 67, RejectedCartons = 0
                }
            }
        };
        db.QualityChecks.Add(check);
        execution.WarehouseRoute = directRoute ? ProductionWarehouseRoutes.Direct : ProductionWarehouseRoutes.Quality;
        db.ProductionExecutionItems.Add(new ProductionExecutionItem
        {
            ExecutionId = execution.Id, OrderItemId = orderItem.Id, ProductId = orderItem.ProductId,
            LotId = orderItem.LotId, CustomerId = orderItem.CustomerId ?? 1, PackagingTypeId = orderItem.PackagingTypeId,
            ActualCartons = 67, ActualQtyKg = 500m
        });
        var groupId = db.ItemGroups.Single(g => g.GroupCode == UnitsPolicy.GroupByProduct).Id;
        var stockProduct = new Product
        {
            ProductCode = "BPSTOCK-" + Guid.NewGuid().ToString("N")[..6], ProductNameAr = "مخرج ثانوي مخزني",
            GroupId = groupId, GroupCode = UnitsPolicy.GroupByProduct, ItemType = "ByProduct",
            UnitOfMeasure = UnitsPolicy.UnitKg, IsActive = true
        };
        db.Products.Add(stockProduct);
        db.SaveChanges();
        var definition = db.ByProducts.FirstOrDefault(b => b.Id == 1);
        if (definition == null)
        {
            definition = new ByProduct { ByProductCode = "BP-DIRECT", ByProductNameAr = "مخرج اختبار", UnitOfMeasure = UnitsPolicy.UnitKg, IsActive = true };
            db.ByProducts.Add(definition);
        }
        definition.ProductId = stockProduct.Id;
        db.SaveChanges();
        db.ExecutionByProducts.Add(new ExecutionByProduct
        {
            ExecutionId = execution.Id, ByProductId = definition.Id, SourceOrderItemId = orderItem.Id,
            RawProductId = 1, RawLotId = lotId, CustomerId = orderItem.CustomerId ?? 1, Qty = 5m
        });
        db.SaveChanges();
        int warehouseId = db.Warehouses.Single(w => w.WarehouseCode == "WFG").Id;
        return (orderId, execution.Id, check.Id, orderItem.Id, lotId, orderItem.PackagingTypeId ?? 2, warehouseId, stockProduct.Id);
    }

    [Fact]
    public void DirectReceipt_IsBlockedUntilQualityCheckIsApproved()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ids = SeedApprovedDirectReceipt(host, approved: false, directRoute: false);
        var fg = host.Get<IFinishedGoodsService>();
        Assert.Empty(fg.GetDirectReceiptSources(DeliverySources.FromActual));
        Assert.Empty(fg.GetDirectReceiptSources(DeliverySources.FromCheck));
        var attempt = fg.SaveAndReceiveDirect(ids.orderId, ids.executionId, ids.checkId, DeliverySources.FromCheck,
            "2026-08-24", new List<FinishedGoodsItemDto>());
        Assert.False(attempt.Ok);
        var db = host.Get<DatesErpDbContext>();
        Assert.Empty(db.FinishedGoodsReceipts);
        var check = db.QualityChecks.Single(q => q.Id == ids.checkId);
        check.IsApproved = true; check.Status = DocStatuses.Approved; db.SaveChanges();
        var released = Assert.Single(fg.GetDirectReceiptSources(DeliverySources.FromCheck));
        Assert.True(released.CanReceive);
        Assert.Equal(ids.checkId, released.QualityCheckId);
    }

    [Fact]
    public void DirectSave_AtomicallyPostsAcceptedGoodsAndMappedByproduct_AndLeavesRemainderAvailable()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ids = SeedApprovedDirectReceipt(host, approved: false, directRoute: true);
        var db = host.Get<DatesErpDbContext>();
        var fg = host.Get<IFinishedGoodsService>();
        var actual = Assert.Single(fg.GetDirectReceiptSources(DeliverySources.FromActual));
        Assert.True(actual.CanReceive);
        Assert.Null(actual.QualityCheckId); // المسار المباشر يتجاوز الجودة صراحةً.
        Assert.Empty(fg.GetDirectReceiptSources(DeliverySources.FromCheck));
        var finishedLine = Assert.Single(actual.Items.Where(i => !i.IsByProduct));
        var byProductLine = Assert.Single(actual.Items.Where(i => i.IsByProduct));
        var cartonKg = UnitsPolicy.CartonWeight(db, finishedLine.ProductId, finishedLine.PackagingTypeId);
        const int cartonsNow = 5;
        const double byProductNowKg = 1.25;
        var save = fg.SaveAndReceiveDirect(ids.orderId, ids.executionId, null, DeliverySources.FromActual,
            "2026-08-24", new List<FinishedGoodsItemDto>
            {
                new()
                {
                    ProductId = finishedLine.ProductId, LotId = finishedLine.LotId, PackagingTypeId = finishedLine.PackagingTypeId,
                    PackageCount = cartonsNow, NetWeightKg = cartonsNow * cartonKg, CustomerId = finishedLine.CustomerId,
                    WarehouseId = ids.warehouseId, OrderItemId = ids.orderItemId
                },
                new()
                {
                    ProductId = byProductLine.ProductId, NetWeightKg = byProductNowKg, CustomerId = byProductLine.CustomerId,
                    WarehouseId = ids.warehouseId, ByProductId = byProductLine.ByProductId,
                    RawProductId = byProductLine.RawProductId, RawLotId = byProductLine.RawLotId,
                    SourceOrderItemId = byProductLine.SourceOrderItemId
                }
            });
        Assert.True(save.Ok, save.Message);
        var receipt = db.FinishedGoodsReceipts.Include(r => r.Items).Single(r => r.Id == save.Id);
        Assert.Equal(DocStatuses.Submitted, receipt.Status);
        Assert.False(receipt.IsApproved);
        Assert.False(string.IsNullOrWhiteSpace(receipt.DocumentNumber));
        Assert.False(string.IsNullOrWhiteSpace(receipt.ReceiptNumber));
        Assert.Null(receipt.DeliveryId);
        Assert.Equal(ids.executionId, receipt.ProductionExecutionId);
        Assert.Null(receipt.QualityCheckId);
        var byReceipt = Assert.Single(receipt.Items.Where(i => i.ByProductId != null));
        Assert.Equal(1, byReceipt.RawProductId);
        Assert.Equal(ids.lotId, byReceipt.RawLotId);
        Assert.Equal(ids.orderItemId, byReceipt.SourceOrderItemId);

        var finishedBalance = db.StockBalances.Single(b => b.WarehouseId == ids.warehouseId
            && b.ProductId == finishedLine.ProductId && b.LotId == finishedLine.LotId
            && b.CustomerId == finishedLine.CustomerId && b.PackagingTypeId == finishedLine.PackagingTypeId);
        Assert.Equal(cartonsNow * cartonKg, finishedBalance.QtyKg, 2);
        Assert.Equal(cartonsNow, finishedBalance.PackageCount);
        var secondaryBalance = db.StockBalances.Single(b => b.WarehouseId == ids.warehouseId
            && b.ProductId == ids.byProductStockId && b.LotId == null && b.CustomerId == byProductLine.CustomerId);
        Assert.Equal(byProductNowKg, secondaryBalance.QtyKg, 2);
        Assert.Equal(0, secondaryBalance.PackageCount);
        Assert.Contains(db.InventoryTransactions, t => t.ProductId == ids.byProductStockId
            && t.ReferenceDocType == ReferenceDocType.FinishedGoodsReceipt && t.MovementType == MovementType.Inbound);

        var approval = fg.ApproveReceipt(save.Id);
        Assert.True(approval.Ok, approval.Message);
        db.ChangeTracker.Clear();
        var approvedReceipt = db.FinishedGoodsReceipts.Single(r => r.Id == save.Id);
        Assert.True(approvedReceipt.IsApproved);
        Assert.Equal(DocStatuses.Completed, approvedReceipt.Status);

        // المسار المباشر للمخزن لا يحتاج فحص جودة، لكنه يظل محكوماً بكمية الاستلام والرصيد.
        var directReady = QualityGate.CustomerDeliveryAllowed(db, ids.orderId, finishedLine.LotId,
            finishedLine.ProductId, finishedLine.PackagingTypeId, finishedLine.CustomerId);
        Assert.True(directReady.ok, directReady.reason);
        var deliveryDraft = new CustomerDelivery { CustomerId = finishedLine.CustomerId!.Value, OrderId = ids.orderId };
        var deliveryItem = new CustomerDeliveryItem
        {
            ProductId = finishedLine.ProductId, LotId = finishedLine.LotId,
            PackagingTypeId = finishedLine.PackagingTypeId, QtyKg = cartonsNow * cartonKg
        };
        deliveryDraft.Items.Add(deliveryItem);
        var qtyReady = QualityGate.CustomerDeliveryQtyAllowed(db, deliveryDraft, deliveryItem);
        Assert.True(qtyReady.ok, qtyReady.reason);
        var pick = new DeliveryPickService(db).GetPickList(finishedLine.CustomerId!.Value, new DateTime(2026, 8, 24));
        var pickLine = Assert.Single(pick, row => row.ProductId == finishedLine.ProductId && row.LotId == finishedLine.LotId);
        Assert.True(pickLine.QcReady);
        Assert.Equal("جاهز للتسليم (استلام مباشر)", pickLine.GradeAr);

        var remaining = Assert.Single(fg.GetDirectReceiptSources(DeliverySources.FromActual));
        Assert.True(remaining.CanReceive);
        var remainingFinished = Assert.Single(remaining.Items.Where(i => !i.IsByProduct));
        var remainingByProduct = Assert.Single(remaining.Items.Where(i => i.IsByProduct));
        Assert.Equal(finishedLine.AvailableCartons - cartonsNow, remainingFinished.AvailableCartons);
        Assert.Equal(finishedLine.AvailableQtyKg - cartonsNow * cartonKg, remainingFinished.AvailableQtyKg, 1);
        Assert.Equal(5m - (decimal)byProductNowKg, (decimal)remainingByProduct.AvailableQtyKg, 3);

        var tooMany = fg.SaveAndReceiveDirect(ids.orderId, ids.executionId, null, DeliverySources.FromActual,
            "2026-08-24", new List<FinishedGoodsItemDto>
            {
                new() { ProductId = finishedLine.ProductId, LotId = finishedLine.LotId, PackagingTypeId = finishedLine.PackagingTypeId,
                    PackageCount = remainingFinished.AvailableCartons + 1, NetWeightKg = (remainingFinished.AvailableCartons + 1) * cartonKg,
                    CustomerId = finishedLine.CustomerId, WarehouseId = ids.warehouseId, OrderItemId = ids.orderItemId }
            });
        Assert.False(tooMany.Ok);
        Assert.Equal(1, db.FinishedGoodsReceipts.Count(r => r.OrderId == ids.orderId && r.SourceType == DeliverySources.FromActual));

        var reverse = fg.Unapprove(save.Id);
        Assert.True(reverse.Ok, reverse.Message);
        db.ChangeTracker.Clear();
        Assert.Equal(0, db.StockBalances.Single(b => b.WarehouseId == ids.warehouseId && b.ProductId == finishedLine.ProductId
            && b.LotId == finishedLine.LotId && b.CustomerId == finishedLine.CustomerId
            && b.PackagingTypeId == finishedLine.PackagingTypeId).QtyKg, 3);
        Assert.Equal(0, db.StockBalances.Single(b => b.WarehouseId == ids.warehouseId
            && b.ProductId == ids.byProductStockId && b.LotId == null && b.CustomerId == byProductLine.CustomerId).QtyKg, 3);
    }

    // ── 1) المصدر التشغيلي الجديد: الإنتاج الفعلي المكتمل فقط + سقف المتبقي ──
    [Fact]
    public void Delivery_FromActual_AutoFill_And_Caps_Remaining()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (oid, lot) = SeedAndClose(host);
        var db = host.Get<DatesErpDbContext>();
        var del = host.Get<IProductionDeliveryService>();
        int executionId = db.ProductionExecutions.Single(e => e.OrderId == oid).Id;
        var ctx = del.GetSourceContext(DeliverySources.FromActual, executionId);
        var line = Assert.Single(ctx.Lines);
        Assert.Equal(500, line.RemainingQtyKg, 1);
        Assert.Equal(1, line.CustomerId);

        var r = del.CreateDeliveryFromActual(executionId, "2026-08-24");
        Assert.True(r.Ok, r.Message);
        var card = del.GetDelivery(r.Id);
        Assert.Equal(500, Assert.Single(card.Lines).QtyKg, 1);

        var over = del.UpdateDelivery(r.Id, "2026-08-24", new List<ProductionDeliveryItemDto>
        { new() { OrderId = oid, ProductId = 3, LotId = lot, CustomerId = 1, QtyKg = 501 } });
        Assert.False(over.Ok);
        Assert.Contains("المتبقي", over.Message);

        var legacy = del.SaveDelivery(DeliverySources.FromCheck, 999999, "2026-08-24", new List<ProductionDeliveryItemDto>
        { new() { ProductId = 3, LotId = lot, CustomerId = 1, QtyKg = 1 } });
        Assert.False(legacy.Ok);
    }

    // ── 2) لا تسليم من محضر غير معتمد ولا من فحص يدوي ──
    [Fact]
    public void Delivery_FromUnapproved_Or_Manual_Check_IsRejected()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (oid, lot) = SeedAndClose(host);
        var quality = host.Get<IQualityService>();
        var q = quality.SaveCheck(oid, null, "2026-08-23", "نهائي", new List<QualityItemDto>
        { new() { ProductId = 3, LotId = lot, CheckedQtyKg = 500, AcceptedQtyKg = 490, RejectedQtyKg = 10 } });
        Assert.True(q.Ok, q.Message);

        var del = host.Get<IProductionDeliveryService>();
        var r = del.SaveDelivery(DeliverySources.FromCheck, q.Id, "2026-08-24", new List<ProductionDeliveryItemDto>
        { new() { ProductId = 3, LotId = lot, CustomerId = 1, QtyKg = 490 } });
        Assert.False(r.Ok);
        Assert.Contains("غير معتمد", r.Message);

        var m = quality.SaveCheck(null, null, "2026-08-24", "نهائي", new List<QualityItemDto>
        { new() { ProductId = 3, AcceptedQtyKg = 100 } });
        Assert.True(m.Ok, m.Message);
        var r2 = del.SaveDelivery(DeliverySources.FromCheck, m.Id, "2026-08-24", new List<ProductionDeliveryItemDto>
        { new() { ProductId = 3, QtyKg = 100 } });
        Assert.False(r2.Ok);
        Assert.Contains("اليدوي", r2.Message);
    }

    // ── 3) التسليم من الخطة: تجاوز بصلاحية وسبب — بدونهما مرفوض ──
    [Fact]
    public void Delivery_FromPlan_Requires_BypassPermission_And_Reason()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (planId, _, lot) = SeedPlanWithProduction(host);
        var del = host.Get<IProductionDeliveryService>();
        var session = host.Services.GetRequiredService<SessionContext>();
        List<ProductionDeliveryItemDto> Items() => new()
        { new() { ProductId = 3, LotId = lot, CustomerId = 1, QtyKg = 500 } };

        // سحب صلاحية التجاوز ← رفض استثنائي
        session.PermissionCache[("production", "BypassInspection")] = false;
        Assert.Throws<PermissionDeniedException>(() =>
            del.SaveDelivery(DeliverySources.FromPlan, planId, "2026-08-24", Items(), "سبب تجريبي"));

        // إعادة المنح بلا سبب ← رفض برسالة
        session.PermissionCache[("production", "BypassInspection")] = true;
        var noReason = del.SaveDelivery(DeliverySources.FromPlan, planId, "2026-08-24", Items());
        Assert.False(noReason.Ok);
        Assert.Contains("سبب", noReason.Message);

        // صلاحية + سبب ← قبول، والسبب محفوظ موثقاً
        var ok = del.SaveDelivery(DeliverySources.FromPlan, planId, "2026-08-24", Items(), "استعجال معتمد من الإدارة — عميل موسمي");
        Assert.True(ok.Ok, ok.Message);
        var db = host.Get<DatesErpDbContext>();
        Assert.Contains("استعجال", db.ProductionDeliveries.Single(d => d.Id == ok.Id).BypassReason);
    }

    // ── 4) إقفال الخطة نتيجة تحرير أمر التسليم، وليس إجراءً مستقلاً ──
    [Fact]
    public void Plan_Closes_Only_After_Issued_Actual_Delivery()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (planId, orderId, _) = SeedPlanWithProduction(host);
        var db = host.Get<DatesErpDbContext>();
        var closure = host.Get<IPlanClosureService>();
        Assert.False(closure.ClosePlanFinal(planId).Ok);
        Assert.False(db.ProductionPlans.Single(p => p.Id == planId).IsClosed);

        var del = host.Get<IProductionDeliveryService>();
        int executionId = db.ProductionExecutions.Single(e => e.OrderId == orderId).Id;
        var draft = del.CreateDeliveryFromActual(executionId, "2026-08-24");
        Assert.True(draft.Ok, draft.Message);
        Assert.True(del.IssueDelivery(draft.Id).Ok);
        Assert.True(db.ProductionPlans.Single(p => p.Id == planId).IsClosed);
    }

    /// <summary>
    /// Regression for the original early-close defect: issuing the first actual delivery
    /// must not close a plan while a second plan item has a separate pending production order.
    /// </summary>
    [Fact]
    public void Plan_With_Second_Pending_Order_Remains_Open_After_First_Delivery()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (planId, firstOrderId, _) = SeedPlanWithProduction(host, addPendingSecondOrder: true);
        var db = host.Get<DatesErpDbContext>();
        var del = host.Get<IProductionDeliveryService>();
        int executionId = db.ProductionExecutions.Single(e => e.OrderId == firstOrderId).Id;

        var draft = del.CreateDeliveryFromActual(executionId, "2026-08-24");
        Assert.True(draft.Ok, draft.Message);
        Assert.True(del.IssueDelivery(draft.Id).Ok);

        var plan = db.ProductionPlans.Single(p => p.Id == planId);
        Assert.False(plan.IsClosed);
        Assert.NotEqual(DocStatuses.Closed, plan.Status);
        Assert.Contains(db.ProductionOrders, o => o.SourcePlanId == planId && o.Status != DocStatuses.Cancelled
            && !o.IsApproved && !o.IsClosed);
    }

    // ── 5) تعدد العملاء: بند لكل عميل ← التام يُقيَّد بعميل البند ──
    [Fact]
    public void MultiCustomer_Delivery_And_Receipt_Posts_PerLineCustomer()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var (oid, lotA, lotB, custB) = SeedMultiCustomer(host);
        var close = host.Get<IExecutionService>()
            .CloseProductionDay(oid, 1000, 134, 0, 0, 0, false, new List<DowntimeDto>(), false, null);
        Assert.True(close.Ok, close.Message);

        var del = host.Get<IProductionDeliveryService>();
        int executionId = db.ProductionExecutions.Single(e => e.OrderId == oid).Id;
        var ctx = del.GetSourceContext(DeliverySources.FromActual, executionId);
        Assert.Equal(2, ctx.Lines.Count);
        var r = del.CreateDeliveryFromActual(executionId, "2026-08-24");
        Assert.True(r.Ok, r.Message);
        Assert.True(del.IssueDelivery(r.Id).Ok);

        var fg = host.Get<IFinishedGoodsService>();
        var card = del.GetDelivery(r.Id);
        var fr = fg.SaveReceipt(oid, null, "2026-08-24",
            card.Lines.Select(l => new FinishedGoodsItemDto
            {
                ProductId = l.ProductId, LotId = l.LotId, PackageCount = 0,
                NetWeightKg = l.QtyKg, CustomerId = l.CustomerId, DeliveryItemId = l.Id
            }).ToList(), r.Id);
        Assert.True(fr.Ok, fr.Message);
        Assert.True(fg.Issue(fr.Id).Ok);
        var recv = fg.Receive(fr.Id, null);
        Assert.True(recv.Ok, recv.Message);

        int wfg = db.Warehouses.Single(w => w.WarehouseCode == "WFG").Id;
        var balA = db.StockBalances.Single(b => b.WarehouseId == wfg && b.ProductId == 3 && b.LotId == lotA && b.CustomerId == 1);
        Assert.Equal(500, balA.QtyKg, 1);
        var balB = db.StockBalances.Single(b => b.WarehouseId == wfg && b.ProductId == 3 && b.LotId == lotB && b.CustomerId == custB);
        Assert.Equal(500, balB.QtyKg, 1);
        Assert.Equal("Full", db.ProductionDeliveries.Single(d => d.Id == r.Id).ReceiptStatus);
    }

    /// <summary>Regression for partial receipt: an omitted receipt-item id is zero, never full remaining.</summary>
    [Fact]
    public void Partial_Receipt_Of_One_Line_Does_Not_Receive_Other_Lines()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var (orderId, _, _, _) = SeedMultiCustomer(host);
        Assert.True(host.Get<IExecutionService>()
            .CloseProductionDay(orderId, 1000, 134, 0, 0, 0, false, new List<DowntimeDto>(), false, null).Ok);

        var delivery = host.Get<IProductionDeliveryService>();
        int executionId = db.ProductionExecutions.Single(e => e.OrderId == orderId).Id;
        var draft = delivery.CreateDeliveryFromActual(executionId, "2026-08-24");
        Assert.True(draft.Ok, draft.Message);
        Assert.True(delivery.IssueDelivery(draft.Id).Ok);
        var card = delivery.GetDelivery(draft.Id);

        var fg = host.Get<IFinishedGoodsService>();
        var receipt = fg.SaveReceipt(orderId, null, "2026-08-24", card.Lines.Select(l => new FinishedGoodsItemDto
        {
            ProductId = l.ProductId,
            LotId = l.LotId,
            PackagingTypeId = l.PackagingTypeId,
            PackageCount = l.PackageCount,
            NetWeightKg = l.QtyKg,
            CustomerId = l.CustomerId,
            DeliveryItemId = l.Id
        }).ToList(), draft.Id);
        Assert.True(receipt.Ok, receipt.Message);
        Assert.True(fg.Issue(receipt.Id).Ok);
        var receiptItems = db.FinishedGoodsReceiptItems.Where(i => i.ReceiptId == receipt.Id).OrderBy(i => i.Id).ToList();
        Assert.Equal(2, receiptItems.Count);

        Assert.True(fg.Receive(receipt.Id, new Dictionary<int, double> { [receiptItems[0].Id] = 200 }).Ok);
        db.ChangeTracker.Clear();
        var persisted = db.FinishedGoodsReceiptItems.Where(i => i.ReceiptId == receipt.Id).OrderBy(i => i.Id).ToList();
        Assert.Equal(200, persisted[0].ReceivedQtyKg, 1);
        Assert.Equal(0, persisted[1].ReceivedQtyKg, 1);
    }

    [Fact]
    public void Duplicate_Open_Receipt_For_One_Delivery_Is_Rejected()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var (orderId, lotId) = SeedAndClose(host);
        var deliveries = host.Get<IProductionDeliveryService>();
        int executionId = db.ProductionExecutions.Single(e => e.OrderId == orderId).Id;
        var delivery = deliveries.CreateDeliveryFromActual(executionId, "2026-08-24");
        Assert.True(delivery.Ok, delivery.Message);
        Assert.True(deliveries.IssueDelivery(delivery.Id).Ok);
        var card = deliveries.GetDelivery(delivery.Id);
        var items = card.Lines.Select(l => new FinishedGoodsItemDto
        {
            ProductId = l.ProductId, LotId = l.LotId ?? lotId, PackagingTypeId = l.PackagingTypeId,
            PackageCount = l.PackageCount, NetWeightKg = l.QtyKg, CustomerId = l.CustomerId,
            DeliveryItemId = l.Id
        }).ToList();
        var fg = host.Get<IFinishedGoodsService>();
        var first = fg.SaveReceipt(orderId, null, "2026-08-24", items, delivery.Id);
        Assert.True(first.Ok, first.Message);
        var duplicate = fg.SaveReceipt(orderId, null, "2026-08-24", items, delivery.Id);
        Assert.False(duplicate.Ok);
        Assert.Contains("مسودة", duplicate.Message);
        Assert.Single(db.FinishedGoodsReceipts.Where(r => r.DeliveryId == delivery.Id
            && (r.Status == DocStatuses.Draft || r.Status == DocStatuses.Issued)));
    }

    // ── 6) الاستلام الجزئي يتراكم + فوق المتبقي مرفوض + الاكتمال يُكمل الأمر ──
    [Fact]
    public void Receipt_Partial_Accumulates_And_OverRemaining_Rejected()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var (oid, lot) = SeedAndClose(host);
        var del = host.Get<IProductionDeliveryService>();
        int executionId = db.ProductionExecutions.Single(e => e.OrderId == oid).Id;
        var r = del.CreateDeliveryFromActual(executionId, "2026-08-24");
        Assert.True(r.Ok, r.Message);
        Assert.True(del.IssueDelivery(r.Id).Ok);
        int lineId = db.ProductionDeliveryItems.Single(i => i.DeliveryId == r.Id).Id;

        var fg = host.Get<IFinishedGoodsService>();
        var fr1 = fg.SaveReceipt(oid, null, "2026-08-24", new List<FinishedGoodsItemDto>
        { new() { ProductId = 3, LotId = lot, PackageCount = 0, NetWeightKg = 500, CustomerId = 1, DeliveryItemId = lineId } }, r.Id);
        Assert.True(fr1.Ok, fr1.Message);
        Assert.True(fg.Issue(fr1.Id).Ok);
        int item1 = db.FinishedGoodsReceiptItems.Single(i => i.ReceiptId == fr1.Id).Id;
        var p1 = fg.Receive(fr1.Id, new Dictionary<int, double> { [item1] = 200 });
        Assert.True(p1.Ok, p1.Message);
        Assert.Equal("Partial", db.ProductionDeliveries.Single(d => d.Id == r.Id).ReceiptStatus);

        // فوق المتبقي (300) مرفوض
        var over = fg.SaveReceipt(oid, null, "2026-08-25", new List<FinishedGoodsItemDto>
        { new() { ProductId = 3, LotId = lot, PackageCount = 0, NetWeightKg = 301, CustomerId = 1, DeliveryItemId = lineId } }, r.Id);
        Assert.False(over.Ok);
        Assert.Contains("المتبقي", over.Message);

        // استكمال 300 ← الأمر مكتمل
        var fr2 = fg.SaveReceipt(oid, null, "2026-08-25", new List<FinishedGoodsItemDto>
        { new() { ProductId = 3, LotId = lot, PackageCount = 0, NetWeightKg = 300, CustomerId = 1, DeliveryItemId = lineId } }, r.Id);
        Assert.True(fr2.Ok, fr2.Message);
        Assert.True(fg.Issue(fr2.Id).Ok);
        Assert.True(fg.Receive(fr2.Id, null).Ok);
        var d = db.ProductionDeliveries.Single(x => x.Id == r.Id);
        Assert.Equal("Full", d.ReceiptStatus);
        Assert.Equal(DocStatuses.Completed, d.Status);
    }

    // ── 7) إلغاء أمر مستلَم مرفوض ← إلغاء السند يعكس ويعيد الفتح ← الإلغاء يُقبل ──
    [Fact]
    public void CancelDelivery_Blocked_By_Receipts_Then_Unapprove_Reopens()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var (oid, lot) = SeedAndClose(host);
        var del = host.Get<IProductionDeliveryService>();
        int executionId = db.ProductionExecutions.Single(e => e.OrderId == oid).Id;
        var r = del.CreateDeliveryFromActual(executionId, "2026-08-24");
        Assert.True(r.Ok, r.Message);
        Assert.True(del.IssueDelivery(r.Id).Ok);
        int lineId = db.ProductionDeliveryItems.Single(i => i.DeliveryId == r.Id).Id;

        var fg = host.Get<IFinishedGoodsService>();
        var fr = fg.SaveReceipt(oid, null, "2026-08-24", new List<FinishedGoodsItemDto>
        { new() { ProductId = 3, LotId = lot, PackageCount = 0, NetWeightKg = 500, CustomerId = 1, DeliveryItemId = lineId } }, r.Id);
        Assert.True(fg.Issue(fr.Id).Ok);
        Assert.True(fg.Receive(fr.Id, null).Ok);

        var blocked = del.CancelDelivery(r.Id);
        Assert.False(blocked.Ok);
        Assert.Contains("مستلم", blocked.Message);

        Assert.True(fg.Unapprove(fr.Id).Ok);
        var reopened = db.ProductionDeliveries.Single(d => d.Id == r.Id);
        Assert.Equal("None", reopened.ReceiptStatus);
        Assert.Equal(DocStatuses.Issued, reopened.Status);
        Assert.True(del.CancelDelivery(r.Id).Ok);

        int wfg = db.Warehouses.Single(w => w.WarehouseCode == "WFG").Id;
        Assert.Equal(0, db.StockBalances.Where(b => b.WarehouseId == wfg && b.ProductId == 3).Sum(b => b.QtyKg), 1);
    }

    // ── 8) المسار المباشر القديم مرفوض دائماً ──
    [Fact]
    public void Legacy_Direct_Receipt_Path_Is_Rejected()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var (oid, lot) = SeedAndClose(host, sendToQuality: true);
        var fg = host.Get<IFinishedGoodsService>();

        var fr = fg.SaveReceipt(oid, null, "2026-08-24", new List<FinishedGoodsItemDto>
        { new() { ProductId = 3, LotId = lot, PackageCount = 0, NetWeightKg = 500 } });
        Assert.False(fr.Ok);
        Assert.Contains("أمر تسليم", fr.Message);
        Assert.Empty(db.FinishedGoodsReceipts);
    }
}
