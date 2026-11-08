using System;
using DatesErp.Application.Services;
using DatesErp.Core.Common;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Views.Screens;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

public class PlanningQuantityAndUnitTests
{
    [Fact]
    public void UiFormat_Parses_Arabic_Indic_And_English_Numerals()
    {
        Assert.True(UiFormat.TryParseInt("٣٠٠٠", out int v1));
        Assert.Equal(3000, v1);

        Assert.True(UiFormat.TryParseInt("5000", out int v2));
        Assert.Equal(5000, v2);

        Assert.True(UiFormat.TryParseDouble("١٢٥٠٠٠٫٥", out double d1));
        Assert.Equal(125000.5, d1);

        Assert.True(UiFormat.TryParseDouble("25.00", out double d2));
        Assert.Equal(25.0, d2);
    }

    [Fact]
    public void LotEditorRow_Accepts_Arabic_Indic_Cartons_Without_Error()
    {
        var row = new LotEditorRow
        {
            ProductId = 1,
            PackWeight = 5.0
        };
        row.CartonsText = "٣٠٠٠";
        Assert.Equal("3000", row.CartonsText);
        Assert.Null(row.QuantityError);
    }

    [Fact]
    public void LotEditorRow_Plans_By_Basket_Unit_Accurately()
    {
        var row = new LotEditorRow
        {
            ProductId = 1,
            PackWeight = 5.0,
            Ctx = new ShipmentQuantityContext
            {
                ReceiptUnit = "سلة",
                UnitWeightKg = 25.0,
                ReceivedUnits = 5000,
                ReceivedKg = 125000,
                AvailableKg = 125000,
                AvailableUnits = 5000
            }
        };
        row.SetContext();
        Assert.Contains("سلة", row.SourceModes);
        Assert.Contains("كجم", row.SourceModes);

        row.SourceMode = "سلة";
        Assert.Equal(25.0, row.EffectiveSourceUnitWeightKg);

        // Planning 100 baskets: 100 * 25 = 2500 kg raw. With 5kg carton weight = 500 cartons.
        row.PlannedSourceUnitsText = "١٠٠";
        Assert.Equal("500", row.CartonsText);
        Assert.Equal(2500, row.RawRequiredKg);
    }

    [Fact]
    public void RawRequirementEngine_Accepts_Any_Raw_Dates_Without_Card_Modification()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        // Finished product 3, raw product 1 (even if not explicitly mapped in ProductRawSources)
        var result = RawRequirementEngine.Calculate(db, 3, 500, selectedRawProductId: 1);
        Assert.True(result.IsConfigured);
        Assert.Equal(1, result.RawProductId);
        Assert.Equal(500m, result.RawRequiredQtyKg);
    }
}
