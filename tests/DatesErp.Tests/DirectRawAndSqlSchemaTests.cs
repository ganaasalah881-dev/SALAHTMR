using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

public class DirectRawAndSqlSchemaTests
{
    [Fact]
    public void Plan_WithoutProductYieldFactor_SavesMoldWeightKg_Approves_Orders_And_Executes()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();

        var db = host.Get<DatesErpDbContext>();
        var whAux = db.Warehouses.Single(w => w.WarehouseCode == "WAUX").Id;
        db.StockBalances.AddRange(
            new StockBalance { WarehouseId = whAux, MaterialId = 1, QtyKg = 50000 },
            new StockBalance { WarehouseId = whAux, MaterialId = 2, QtyKg = 50000 });
        db.SaveChanges();

        var customer = db.Customers.First(c => c.IsActive);
        var rawProd = db.Products.First(p => p.ItemType == "Raw" && p.IsActive);
        var finProd = db.Products.First(p => p.ItemType == "Finished" && p.SourceProductId == rawProd.Id && p.IsActive);
        finProd.YieldFactor = null;
        finProd.MoldsCount = 1;
        finProd.MoldWeightKg = 0.5;
        finProd.CartonWeightKg = 5.0;
        db.ProductRawSources.RemoveRange(db.ProductRawSources.Where(x => x.FinishedProductId == finProd.Id));
        db.SaveChanges();

        var pack5 = db.PackagingTypes.First(p => p.PackageCode == "CT5");
        pack5.UnitWeightKg = 5.0;
        pack5.MoldsCount = 10;
        pack5.MoldWeightKg = 0.5;
        db.SaveChanges();

        var shift = db.Shifts.First(s => s.IsActive);
        var line = db.ProductionLines.First(l => l.IsActive);
        host.Get<ICapacityService>().SetCapacity(finProd.Id, shift.Id, 5000, pack5.Id);

        const string planDay = "25/10/2026";
        var receiving = host.Get<IReceivingService>();
        var shipRes = receiving.SaveShipment(customer.Id, planDay, planDay, new List<ShipmentItemDto>
        {
            new()
            {
                TreatmentRequired = false,
                ProductId = rawProd.Id,
                PackagingTypeId = pack5.Id,
                PackageCount = 3000,
                UnitWeightKg = 5.0,
                QtyKg = 15000
            }
        });
        Assert.True(shipRes.Ok, shipRes.Message);
        Assert.True(receiving.ApproveShipment(shipRes.Id).Ok);

        var lot = db.Lots.AsNoTracking().Single(l => l.ShipmentId == shipRes.Id);
        var rawWh = db.Warehouses.AsNoTracking().First(w => w.WarehouseCode == "WRM");

        // نفس سيناريو المستخدم حرفياً: 2000 كرتون × 5 كجم = 10,000 كجم تام + تخصيص خام 10,000 كجم دون أي معامل إنتاج على الصنف
        var planning = host.Get<IPlanningService>();
        var savePlan = planning.SavePlan(
            "خطة اختبار 1.50.76",
            "Daily",
            planDay,
            planDay,
            shift.Id,
            line.Id,
            new List<PlanItemDto>
            {
                new()
                {
                    SourceType = "FromReceiving",
                    LotId = lot.Id,
                    ShipmentId = shipRes.Id,
                    CustomerId = customer.Id,
                    ProductId = finProd.Id,
                    PackagingTypeId = pack5.Id,
                    PlannedCartons = 2000,
                    PlannedQtyKg = 10000,
                    RawRequiredQtyKg = 10000,
                    ScheduledDate = planDay,
                    SuggestedShiftId = shift.Id,
                    SuggestedLineId = line.Id,
                    PriorityNo = 1,
                    Allocations = new List<PlanItemAllocationDto>
                    {
                        new() { WarehouseId = rawWh.Id, LotId = lot.Id, CustomerId = customer.Id, AllocatedQtyKg = 10000 }
                    }
                }
            },
            notes: null,
            scopeMode: "Single",
            singleCustomerId: customer.Id);
        Assert.True(savePlan.Ok, savePlan.Message);

        var savedItem = db.ProductionPlanItems.AsNoTracking().Single(i => i.PlanId == savePlan.Id);
        Assert.Equal(10, savedItem.MoldsCount);
        Assert.Equal(0.5m, savedItem.MoldWeightKg);
        Assert.Equal(10000m, savedItem.RawRequiredQtyKg);

        var appPlan = planning.ApprovePlan(savePlan.Id);
        Assert.True(appPlan.Ok, appPlan.Message);

        host.SetBusinessDate("2026-10-25");
        var orders = host.Get<IProductionOrderService>();
        var saveOrd = orders.SaveOrder("FromPlan", savePlan.Id, customer.Id, "2026-10-25", shift.Id, line.Id,
            TestOrderFixture.Items(host, savePlan.Id, shift.Id, "2026-10-25"));
        Assert.True(saveOrd.Ok, saveOrd.Message);

        var appOrd = orders.ApproveOrder(saveOrd.Id);
        Assert.True(appOrd.Ok, appOrd.Message);

        var execution = host.Get<IExecutionService>();
        var closeRes = execution.CloseProductionDay(saveOrd.Id, 10000, 2000, 0, 0, 0,
            carryToNextDay: false, downtimes: new List<DowntimeDto>(), sendToQuality: true, consumedRawKg: 10000);
        Assert.True(closeRes.Ok, closeRes.Message);
        Assert.Contains("التصافي الفعلي للجلسة", closeRes.Message ?? "");

        // التحقق من دورة فحص الجودة الكاملة (بما في ذلك قبول جزء ورفض جزء بقرار Passed)
        var inspection = host.Get<IInspectionService>();
        var qSources = inspection.GetDeliverySources();
        var qSrc = Assert.Single(qSources, s => s.OrderId == saveOrd.Id);
        var qItem = Assert.Single(qSrc.Items);
        var accGrade = qItem.AllowedGrades.First(g => g.ResultKind == InspectionResultType.KindAccepted);
        var rejGrade = qItem.AllowedGrades.First(g => g.ResultKind == InspectionResultType.KindRejected);

        var saveQc = inspection.SaveDeliveryCheck(new QualityDeliveryCheckDto
        {
            OrderId = saveOrd.Id,
            CheckDate = planDay,
            Decision = "Passed",
            Rows = new List<QualityGradeRowDto>
            {
                new() { OrderItemId = qItem.OrderItemId, ProductId = qItem.ProductId, LotId = qItem.LotId, ResultTypeId = accGrade.ResultTypeId, Cartons = 1990 },
                new() { OrderItemId = qItem.OrderItemId, ProductId = qItem.ProductId, LotId = qItem.LotId, ResultTypeId = rejGrade.ResultTypeId, Cartons = 10 },
            }
        });
        Assert.True(saveQc.Ok, saveQc.Message);

        var appQc = host.Get<IQualityService>().ApproveCheck(saveQc.Id);
        Assert.True(appQc.Ok, appQc.Message);
    }

    [Fact]
    public void AllDesktopXamlFiles_HaveValidGridLinesAndScrollBarVisibilities()
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/DatesErp.Desktop"));
        var xamlFiles = Directory.GetFiles(dir, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                     && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .ToList();
        Assert.NotEmpty(xamlFiles);
        foreach (var file in xamlFiles)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("GridLinesVisibility=\"Both\"", text);
            Assert.DoesNotContain("HeadersVisibility=\"Both\"", text);
            Assert.DoesNotContain("ScrollBarVisibility=\"Both\"", text);
        }
    }

    [Fact]
    public void SchemaMigrator_BackfillsPlanItemMoldWeightKg_And_FormatsSqlServerTypes()
    {
        using var host = new TestHost();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        var fin = db.Products.First(p => p.ItemType == "Finished");
        fin.MoldsCount = 4;
        fin.MoldWeightKg = 0.5;
        var plan = new ProductionPlan
        {
            DocumentNumber = "PLN-BF-76",
            PlanTitle = "Backfill Test",
            Status = DocStatuses.Draft
        };
        plan.Items.Add(new ProductionPlanItem
        {
            ProductId = fin.Id,
            PlannedCartons = 10,
            PlannedQtyKg = 20,
            MoldsCount = 0,
            MoldWeightKg = 0m
        });
        db.ProductionPlans.Add(plan);
        db.SaveChanges();

        var report = SchemaMigrator.Migrate(db);
        Assert.DoesNotContain(report, r => r.StartsWith("خطأ"));

        var reloaded = db.ProductionPlanItems.AsNoTracking().Single(i => i.PlanId == plan.Id);
        Assert.Equal(4, reloaded.MoldsCount);
        Assert.Equal(0.5m, reloaded.MoldWeightKg);

        Assert.Equal("nvarchar(max) COLLATE Arabic_CI_AS",
            SchemaMigrator.BuildSqlServerTypeDefinition("nvarchar", -1, 0, 0, "Arabic_CI_AS"));
        Assert.Equal("nvarchar(100) COLLATE Arabic_CI_AS",
            SchemaMigrator.BuildSqlServerTypeDefinition("nvarchar", 200, 0, 0, "Arabic_CI_AS"));
        Assert.Equal("decimal(18,4)",
            SchemaMigrator.BuildSqlServerTypeDefinition("decimal", 9, 18, 4));
        Assert.Equal("datetime2(7)",
            SchemaMigrator.BuildSqlServerTypeDefinition("datetime2", 8, 27, 7));
        Assert.Equal("float",
            SchemaMigrator.BuildSqlServerTypeDefinition("float", 8, 53, 0));
        Assert.Equal("0", SchemaMigrator.SafeSqlDefaultLiteral("decimal"));
        Assert.Equal("N''", SchemaMigrator.SafeSqlDefaultLiteral("nvarchar"));
    }
}
