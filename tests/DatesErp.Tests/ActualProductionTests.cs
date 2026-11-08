using DatesErp.Application.Services;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Views.Screens;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace DatesErp.Tests;
public class ActualProductionTests
{
    [Fact]
    public void Actual_Delivery_Persists_Stock_QC_Reports_And_Rolls_Back_Atomically()
    {
        using var host = new TestHost(); host.LoginAsAdmin(); int steps = 0;
        ActualDeliveryScenarios.Run(host.Services, (ok, message) => { Assert.True(ok, message); steps++; });
        Assert.True(steps >= 105);
    }
    [Fact]
    public void SaveActualProduction_Persists_ClosedExecutionState()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        FullWorkflowTests.SeedQuickOrderPacked(host, db, out var orderId, out _);

        var delivery = host.Get<IProductionDeliveryService>();
        var order = Assert.Single(delivery.GetActualDeliveryOrders(), o => o.OrderId == orderId);
        var input = new ActualProductionDto
        {
            OrderId = orderId,
            ConsumedRawKg = 0, // واجهة الإنتاج تترك تقدير الخام الآلي للخدمة.
            Items = order.Items.Select(i => new ActualProductionItemDto
            {
                OrderItemId = i.OrderItemId,
                ActualCartons = i.PlannedCartons
            }).ToList()
        };
        double expectedRawEstimate = order.Items.Sum(i => i.RawMaterialBasisKg > 0 ? i.RawMaterialBasisKg : i.PlannedQtyKg);

        var saved = delivery.SaveActualProduction(input);
        Assert.True(saved.Ok, saved.Message);

        db.ChangeTracker.Clear();
        var execution = Assert.Single(db.ProductionExecutions.AsNoTracking().Where(e => e.OrderId == orderId));
        Assert.True(execution.IsDayClosed);
        Assert.Equal(DocStatuses.Completed, execution.Status);
        Assert.NotNull(execution.EndDateTime);
        Assert.Equal(expectedRawEstimate, execution.ConsumedRawKg, 2);

        // Regression guard: closing the execution must close the production order and every item too.
        db.ChangeTracker.Clear();
        var persistedOrder = Assert.Single(db.ProductionOrders.AsNoTracking().Include(o => o.Items).Where(o => o.Id == orderId));
        Assert.True(persistedOrder.IsClosed);
        Assert.Equal(DocStatuses.Completed, persistedOrder.Status);
        Assert.NotEmpty(persistedOrder.Items);
        Assert.All(persistedOrder.Items, item =>
        {
            Assert.True(item.IsClosed);
            Assert.Equal(DocStatuses.Completed, item.Status);
        });

        // تسليم الكمية كاملة مرة واحدة؛ أي محاولة ثانية للمصدر نفسه لا تنشئ مستنداً مكرراً.
        var firstDraft = delivery.CreateDeliveryFromActual(saved.Id, "2026-08-10");
        Assert.True(firstDraft.Ok, firstDraft.Message);
        var duplicateDraft = delivery.CreateDeliveryFromActual(saved.Id, "2026-08-10");
        Assert.False(duplicateDraft.Ok);
        Assert.Single(db.ProductionDeliveries.AsNoTracking().Where(d =>
            d.SourceType == DeliverySources.FromActual && d.SourceId == saved.Id && d.Status != DocStatuses.Cancelled));
    }

    [Fact]
    public void ActualClose_DirectRoute_SavesPerExecutionItems_AndAppearsWithoutQuality()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        FullWorkflowTests.SeedQuickOrderPacked(host, db, out var orderId, out _);
        var service = host.Get<IProductionDeliveryService>();
        var order = Assert.Single(service.GetActualDeliveryOrders(), o => o.OrderId == orderId);
        var saved = service.SaveActualProduction(new ActualProductionDto
        {
            OrderId = orderId,
            WarehouseRoute = ProductionWarehouseRoutes.Direct,
            Items = order.Items.Select(i => new ActualProductionItemDto
            {
                OrderItemId = i.OrderItemId, ActualCartons = i.PlannedCartons
            }).ToList()
        });
        Assert.True(saved.Ok, saved.Message);
        db.ChangeTracker.Clear();
        var execution = db.ProductionExecutions.AsNoTracking().Single(e => e.Id == saved.Id);
        Assert.Equal(ProductionWarehouseRoutes.Direct, execution.WarehouseRoute);
        Assert.Empty(db.QualityChecks.AsNoTracking().Where(q => q.ExecutionId == saved.Id));
        var sessionItems = db.ProductionExecutionItems.AsNoTracking().Where(i => i.ExecutionId == saved.Id).ToList();
        Assert.Equal(order.Items.Count(i => i.PlannedCartons > 0), sessionItems.Count);
        Assert.Equal((decimal)execution.ActualQtyKg, sessionItems.Sum(i => i.ActualQtyKg), 2);

        var quality = host.Get<IInspectionService>();
        Assert.DoesNotContain(quality.GetDeliverySources(), source => source.ExecutionId == saved.Id);

        var fg = host.Get<IFinishedGoodsService>();
        var direct = Assert.Single(fg.GetDirectReceiptSources(DeliverySources.FromActual));
        Assert.True(direct.CanReceive);
        Assert.Equal(saved.Id, direct.ExecutionId);
        Assert.Null(direct.QualityCheckId);
        Assert.Empty(fg.GetDirectReceiptSources(DeliverySources.FromCheck));
    }

    [Fact]
    public void SaveActualProduction_Persists_Byproduct_Source_Identity_And_Search_Reloads_It()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        FullWorkflowTests.SeedQuickOrderPacked(host, db, out var orderId, out _);

        var delivery = host.Get<IProductionDeliveryService>();
        var byproductStock = host.Get<MasterDataService>().SaveProductFull(null, "ACTTEST-BP", "مخرج مخزني للاختبار", "003", "ByProduct", "كجم", 0, 0, 0, new());
        Assert.True(byproductStock.Ok, byproductStock.Message);
        var definitionRow = db.ByProducts.First();
        Assert.True(host.Get<MasterDataService>().SaveByProduct(definitionRow.Id, definitionRow.ByProductNameAr, "كجم", byproductStock.Id).Ok);
        var order = Assert.Single(delivery.GetActualDeliveryOrders(), o => o.OrderId == orderId);
        var source = Assert.Single(order.ByProductSources);
        var definition = delivery.GetActualByProducts().First();
        var saved = delivery.SaveActualProduction(new ActualProductionDto
        {
            OrderId = orderId,
            ConsumedRawKg = 500,
            Items = order.Items.Select(i => new ActualProductionItemDto
            {
                OrderItemId = i.OrderItemId,
                ActualCartons = i.PlannedCartons
            }).ToList(),
            ByProducts = new List<ByProductQtyDto>
            {
                new()
                {
                    ByProductId = definition.Id, QtyKg = 12.5,
                    SourceOrderItemId = source.OrderItemId, RawProductId = source.RawProductId,
                    RawLotId = source.RawLotId, CustomerId = source.CustomerId
                }
            }
        });
        Assert.True(saved.Ok, saved.Message);

        db.ChangeTracker.Clear();
        var output = Assert.Single(db.ExecutionByProducts.AsNoTracking().Where(x => x.ExecutionId == saved.Id));
        Assert.Equal(source.OrderItemId, output.SourceOrderItemId);
        Assert.Equal(source.RawProductId, output.RawProductId);
        Assert.Equal(source.RawLotId, output.RawLotId);
        Assert.Equal(source.CustomerId, output.CustomerId);

        var history = Assert.Single(delivery.GetActualDeliveryOrders(orderId, includeCompleted: true), x => x.OrderId == orderId);
        var savedOutput = Assert.Single(history.ByProducts);
        Assert.Equal(source.OrderItemId, savedOutput.SourceOrderItemId);
        Assert.Equal(source.RawProductId, savedOutput.RawProductId);
        Assert.Equal(source.RawLotId, savedOutput.RawLotId);
        Assert.Equal(source.CustomerId, savedOutput.CustomerId);
        Assert.Equal(12.5, savedOutput.QtyKg);
    }

    [Fact]
    public void Quality_Excludes_Aggregate_Grade_And_Cancelled_Check_Returns_To_New_List()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        FullWorkflowTests.SeedQuickOrderPacked(host, db, out var orderId, out _);

        var delivery = host.Get<IProductionDeliveryService>();
        var actualOrder = Assert.Single(delivery.GetActualDeliveryOrders(), o => o.OrderId == orderId);
        var actual = delivery.SaveActualProduction(new ActualProductionDto
        {
            OrderId = orderId,
            ConsumedRawKg = 500,
            Items = actualOrder.Items.Select(i => new ActualProductionItemDto
            {
                OrderItemId = i.OrderItemId,
                ActualCartons = i.PlannedCartons
            }).ToList()
        });
        Assert.True(actual.Ok, actual.Message);

        var quality = host.Get<IInspectionService>();
        var source = Assert.Single(quality.GetDeliverySources(), s => s.OrderId == orderId);
        Assert.NotNull(source.CheckId); // إقفال الإنتاج أنشأ محضراً تمهيدياً تلقائياً.
        Assert.False(source.HasInspectionResults); // لكنه غير مفحوص ويجب أن يبقى متاحاً في إضافة فحص.
        int pendingCheckId = source.CheckId.Value;
        Assert.All(source.Items, item =>
        {
            Assert.DoesNotContain(item.AllowedGrades, g => g.Code == "RT-OK" || g.NameAr == "خرج تام مطابق");
            Assert.Contains(item.AllowedGrades, g => g.Code == "RT-SALEEM");
            Assert.Contains(item.AllowedGrades, g => g.Code == "RT-MONSAM");
        });

        int aggregateTypeId = db.InspectionResultTypes.AsNoTracking().Single(t => t.Code == "RT-OK").Id;
        var duplicateAggregate = new QualityDeliveryCheckDto
        {
            OrderId = orderId,
            Decision = "Passed",
            Rows = source.Items.Select(item => new QualityGradeRowDto
            {
                OrderItemId = item.OrderItemId,
                ProductId = item.ProductId,
                LotId = item.LotId,
                ResultTypeId = aggregateTypeId,
                Cartons = item.ProducedCartons
            }).ToList()
        };
        var rejected = quality.SaveDeliveryCheck(duplicateAggregate);
        Assert.False(rejected.Ok);
        Assert.Contains("إجمالي", rejected.Message);

        var splitRows = new List<QualityGradeRowDto>();
        foreach (var item in source.Items)
        {
            int saleemId = item.AllowedGrades.Single(g => g.Code == "RT-SALEEM").ResultTypeId;
            int monsamId = item.AllowedGrades.Single(g => g.Code == "RT-MONSAM").ResultTypeId;
            int saleem = item.ProducedCartons / 2;
            splitRows.Add(new QualityGradeRowDto { OrderItemId = item.OrderItemId, ProductId = item.ProductId,
                LotId = item.LotId, ResultTypeId = saleemId, Cartons = saleem });
            splitRows.Add(new QualityGradeRowDto { OrderItemId = item.OrderItemId, ProductId = item.ProductId,
                LotId = item.LotId, ResultTypeId = monsamId, Cartons = item.ProducedCartons - saleem });
        }
        var saved = quality.SaveDeliveryCheck(new QualityDeliveryCheckDto
        {
            OrderId = orderId, Decision = "Passed", Rows = splitRows
        });
        Assert.True(saved.Ok, saved.Message);

        var afterSave = Assert.Single(quality.GetDeliverySources(), s => s.OrderId == orderId);
        Assert.Equal(pendingCheckId, saved.Id); // استُكمل المحضر التمهيدي دون إنشاء فحص مكرر.
        Assert.Equal(saved.Id, afterSave.CheckId);
        Assert.True(afterSave.HasInspectionResults);
        Assert.DoesNotContain(quality.GetDeliverySources(), s => s.OrderId == orderId && !s.HasInspectionResults && !s.CheckApproved);

        var legacyType = db.InspectionResultTypes.AsNoTracking().Single(t => t.Code == "RT-OK");
        var historicalItem = source.Items[0];
        db.InspectionResults.Add(new InspectionResult
        {
            CheckId = saved.Id, ProductId = historicalItem.ProductId, LotId = historicalItem.LotId,
            ResultTypeId = legacyType.Id, Qty = historicalItem.ProducedCartons,
            UnitId = legacyType.UnitId, UnitLabel = legacyType.UnitLabel
        });
        db.SaveChanges();
        var historicalSource = Assert.Single(quality.GetDeliverySources(), s => s.OrderId == orderId);
        Assert.Contains(historicalSource.Items[0].AllowedGrades, g => g.Code == "RT-OK");
        var approval = host.Get<IQualityService>().ApproveCheck(saved.Id);
        Assert.False(approval.Ok);
        Assert.Contains("إجمالي", approval.Message);

        const string reason = "فحص تجريبي لإثبات الإلغاء الآمن";
        var cancelled = quality.CancelDeliveryCheck(saved.Id, reason);
        Assert.True(cancelled.Ok, cancelled.Message);
        db.ChangeTracker.Clear();
        var historical = db.QualityChecks.AsNoTracking().Single(c => c.Id == saved.Id);
        Assert.Equal(DocStatuses.Cancelled, historical.Status);
        Assert.Equal(reason, historical.StatusReason);
        Assert.NotEmpty(db.InspectionResults.AsNoTracking().Where(r => r.CheckId == saved.Id));
        Assert.Contains(quality.GetDeliverySources(), s => s.OrderId == orderId && !s.HasInspectionResults && !s.CheckApproved);
    }

    [Theory]
    [InlineData("")][InlineData("NaN")][InlineData("Infinity")][InlineData("-1")][InlineData("3001")][InlineData("2700.5")]
    public void Actual_UI_Row_Does_Not_Coerce_Invalid_Input(string value)
    {
        var row = new ActualProductionRow(new ActualDeliveryItemDto { PlannedCartons = 3000 }, false) { Actual = value };
        Assert.False(row.TryQuantity(out _)); Assert.NotEmpty(row.Error); Assert.Equal("—", row.Difference);
    }
    [Fact]
    public void Arabic_Digits_Are_Actual_Input_Not_Implicit_Planned_Quantity()
    {
        var row = new ActualProductionRow(new ActualDeliveryItemDto { PlannedCartons = 3000 }, false);
        Assert.Empty(row.Actual); row.Actual = "٢٧٠٠"; Assert.True(row.TryQuantity(out var quantity)); Assert.Equal(2700, quantity); Assert.Equal("300", row.Difference);
        Assert.True(ActualProductionRow.TryNonnegative("١٫٥", out var hours)); Assert.Equal(1.5, hours);
    }
    [Fact]
    public void Actual_Rows_Accept_Independent_Quantities_For_Multiple_Products()
    {
        var first = new ActualProductionRow(new ActualDeliveryItemDto { Product = "منتج أول", PlannedCartons = 6000 }, false)
            { Actual = "5400" };
        var second = new ActualProductionRow(new ActualDeliveryItemDto { Product = "منتج ثانٍ", PlannedCartons = 3000 }, false)
            { Actual = "2750" };
        Assert.True(first.TryQuantity(out var firstQty));
        Assert.True(second.TryQuantity(out var secondQty));
        Assert.Equal(5400, firstQty);
        Assert.Equal(2750, secondQty);
    }

    [Fact]
    public void Submission_Cannot_Choose_Customer_Product_Lot_Or_Planned_Quantity()
    {
        var fields = typeof(ActualProductionItemDto).GetProperties().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "ActualCartons", "OrderItemId" }, fields);
    }
}
