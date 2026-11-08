using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Views.Screens;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace DatesErp.Tests;
public class PlanningLinkCapacityTests
{
    [Fact]
    public void Full_Master_Raw_Selector_Save_Backend_And_Capacity_Scenario()
    {
        using var host = new TestHost(); host.LoginAsAdmin();
        int steps = 0; PlanningScenarios.Run(host.Services, (ok, label) => { Assert.True(ok, label); steps++; }); Assert.Equal(36, steps);
    }
    [Theory]
    [InlineData(3000, 2000, true, 100)]
    [InlineData(3000, 3500, false, 130)]
    [InlineData(5000, 1, false, 100.02)]
    [InlineData(1000, 1000, true, 40)]
    [InlineData(2500, 2500, true, 100)]
    [InlineData(1, 4999, true, 100)]
    public void Cumulative_Actual_Rate_Budget(int a, int b, bool allowed, double percent)
    {
        using var host = new TestHost(); host.LoginAsAdmin(); using var scope = host.Services.CreateScope();
        var caps = scope.ServiceProvider.GetRequiredService<ICapacityService>(); caps.SetCapacity(3, 1, 5000); caps.SetCapacity(4, 1, 5000);
        var svc = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var result = svc.EvaluateCapacity(new() { Dto(3, a), Dto(4, b) }); Assert.Equal(allowed, result.IsValid); Assert.Equal(percent, result.UsagePercent, 6);
    }
    private static PlanItemDto Dto(int id, int n) => new() { ProductId = id, PlannedCartons = n, ScheduledDate = "01/10/2026", SuggestedShiftId = 1, SuggestedLineId = 1 };
    [Theory]
    [InlineData(2, 6)]
    [InlineData(8, 0)]
    public void Zero_Effective_Hours_Deduct_Real_Downtime(double downtime, double expected)
    {
        using var host = new TestHost(); host.LoginAsAdmin(); using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>(); var sh = db.Shifts.Single(s => s.Id == 1);
        sh.TotalHours = 8; sh.EffectiveProductiveHours = 0; sh.PlannedDowntimeHours = downtime; db.SaveChanges();
        var result = scope.ServiceProvider.GetRequiredService<IPlanningService>().EvaluateCapacity(new() { Dto(3, 1) }); Assert.Equal(expected, result.TotalHours); Assert.Equal(expected > 0, result.IsValid);
    }
    [Theory]
    [InlineData("date")]
    [InlineData("shift")]
    [InlineData("line")]
    public void Independent_Slots_Are_Not_Charged_Twice(string dimension)
    {
        using var host = new TestHost(); host.LoginAsAdmin(); using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>(); var caps = scope.ServiceProvider.GetRequiredService<ICapacityService>(); caps.SetCapacity(3, 1, 5000); caps.SetCapacity(4, 1, 5000);
        var second = Dto(4, 5000);
        if (dimension == "date") second.ScheduledDate = "02/10/2026";
        if (dimension == "shift") { var sh = db.Shifts.Single(s => s.Id == 2); sh.EffectiveProductiveHours = 8; sh.TotalHours = 8; db.SaveChanges(); caps.SetCapacity(4, 2, 5000); second.SuggestedShiftId = 2; }
        if (dimension == "line") { var line = new DatesErp.Core.Domain.Entities.ProductionLine { LineCode = "LTEST", LineNameAr = "خط آخر" }; db.ProductionLines.Add(line); db.SaveChanges(); second.SuggestedLineId = line.Id; }
        var result = scope.ServiceProvider.GetRequiredService<IPlanningService>().EvaluateCapacity(new() { Dto(3, 5000), second }); Assert.True(result.IsValid, result.Error); Assert.Equal(16, result.TotalHours);
    }
    [Fact]
    public void Period_Total_Does_Not_Hide_Overloaded_Day()
    {
        using var host = new TestHost(); host.LoginAsAdmin(); using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICapacityService>().SetCapacity(3, 1, 5000);
        var result = scope.ServiceProvider.GetRequiredService<IPlanningService>().EvaluateCapacity(new() { Dto(3, 6500) }, null, "01/10/2026", "02/10/2026", 1, 1);
        Assert.Equal(65, result.UsagePercent, 6); Assert.False(result.IsValid);
    }
    [Fact]
    public void Raw_Query_Excludes_Inactive_And_Unlinked_Products_And_Invalid_Raw()
    {
        using var host = new TestHost(); host.LoginAsAdmin(); using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>(); var svc = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var p = db.Products.Single(p => p.Id == 3); var raw = p.SourceProductId!.Value; p.IsActive = false; db.SaveChanges();
        Assert.DoesNotContain(svc.GetFinishedProductsForRaw(raw), x => x.Id == p.Id);
        p.IsActive = true; p.SourceProductId = null; db.SaveChanges(); Assert.DoesNotContain(svc.GetFinishedProductsForRaw(raw), x => x.Id == p.Id);
        Assert.Empty(svc.GetFinishedProductsForRaw(-1)); Assert.Empty(svc.GetFinishedProductsForRaw(3));
    }
    [Fact]
    public void Planning_Product_List_Uses_Official_Raw_Links_Across_Packages_And_Suppresses_Legacy_When_Explicit_Rules_Exist()
    {
        using var host = new TestHost(); host.LoginAsAdmin(); using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var rawSugar = master.SaveProductFull(null, "LINK-RAW-S", "خام سكري ربط", "001", "Raw", "كجم", 20, 0, 0, null);
        var rawTah = master.SaveProductFull(null, "LINK-RAW-T", "خام تهامي ربط", "001", "Raw", "كجم", 20, 0, 0, null);
        Assert.True(rawSugar.Ok, rawSugar.Message); Assert.True(rawTah.Ok, rawTah.Message);
        var sugarFinished = master.SaveProductFull(null, "LINK-FG-S", "تام سكري مربوط", "002", "Finished", "كرتون", 5, 0, 0, null, rawSugar.Id);
        var legacyWithOverride = master.SaveProductFull(null, "LINK-FG-O", "تام له قواعد صريحة", "002", "Finished", "كرتون", 5, 0, 0, null, rawSugar.Id);
        Assert.True(sugarFinished.Ok, sugarFinished.Message); Assert.True(legacyWithOverride.Ok, legacyWithOverride.Message);
        var packId = db.PackagingTypes.AsNoTracking().Where(p => p.IsActive).Select(p => p.Id).First();
        Assert.True(master.SaveProductRawSource(null, sugarFinished.Id, rawSugar.Id, 1m, packId).Ok);
        Assert.True(master.SaveProductRawSource(null, legacyWithOverride.Id, rawTah.Id, 1m, packId).Ok);

        var sugarList = planning.GetFinishedProductsForRaw(rawSugar.Id);
        var tahList = planning.GetFinishedProductsForRaw(rawTah.Id);
        Assert.Contains(sugarList, p => p.Id == sugarFinished.Id); // ربط العبوة يؤهّل المنتج بغض النظر عن عبوة التخطيط.
        Assert.DoesNotContain(sugarList, p => p.Id == legacyWithOverride.Id); // القاعدة الصريحة تتقدم على SourceProductId القديم.
        Assert.Contains(tahList, p => p.Id == legacyWithOverride.Id);
        Assert.DoesNotContain(tahList, p => p.Id == sugarFinished.Id);
    }

    [Fact]
    public void Main_Row_Rejects_Quantity_Without_Mutating_Bound_Integer()
    {
        var row = new PlanRowUi { Cartons = 2000, QuantityGuard = q => q > 2000 ? "الحد 2000" : null! };
        Assert.Throws<ArgumentException>(() => row.CartonsText = "3500"); Assert.Equal(2000, row.Cartons); Assert.NotNull(row.QuantityError);
        row.CartonsText = "1000"; Assert.Equal(1000, row.Cartons); Assert.Null(row.QuantityError);
        row.CartonsText = "bad"; Assert.NotNull(row.QuantityError);
    }
}
