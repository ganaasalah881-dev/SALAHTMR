using System.IO;
using Xunit;

namespace DatesErp.Tests;

/// <summary>حواجز لتوحيد نقطة إدخال الفعلي ومنع إعادة إتاحة شاشة التسليم الإنتاجية القديمة.</summary>
public sealed class ProductionActualCloseConsolidationTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DateERP.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string Read(string relativePath) => File.ReadAllText(Path.Combine(RepoRoot(), relativePath));

    [Fact]
    public void Production_Catalog_And_Factory_Expose_Only_Actual_Close()
    {
        var catalog = Read("src/DatesErp.Desktop/Screens/ScreenCatalog.cs");
        var factory = Read("src/DatesErp.Desktop/Views/Screens/ScreenFactory.cs");
        var taskWindow = Read("src/DatesErp.Desktop/Views/Screens/TaskWindow.cs");
        var oldScreen = Read("src/DatesErp.Desktop/Views/Screens/ProductionDeliveryView.xaml.cs");

        Assert.Contains("new(\"actualclose\", \"الإنتاج\", \"إقفال الإنتاج وتسجيل الفعلي\"", catalog);
        Assert.DoesNotContain("new(\"proddelivery\"", catalog);
        Assert.DoesNotContain("\"proddelivery\" =>", factory);
        Assert.DoesNotContain("private static UIElement ProdDeliveryScreen", factory);
        Assert.DoesNotContain("OpenScreen(\"proddelivery\")", oldScreen);
        Assert.Contains("WorkflowDocTypes.ProductionDelivery => null", taskWindow);
        Assert.Contains("ReceiptWindow.FromDelivery(_task.DocumentId)", taskWindow);
    }

    [Fact]
    public void Actual_Close_Provides_Explicit_Search_Save_Print_And_Byproduct_Source_Entry()
    {
        var xaml = Read("src/DatesErp.Desktop/Views/Screens/ProductionActualCloseView.xaml");
        var code = Read("src/DatesErp.Desktop/Views/Screens/ProductionActualCloseView.xaml.cs");
        var rows = Read("src/DatesErp.Desktop/Mvvm/ActualProductionRows.cs");

        Assert.Contains("x:Name=\"OrderSearchBox\"", xaml);
        Assert.Contains("Click=\"SearchOrders_Click\"", xaml);
        Assert.Contains("x:Name=\"ItemsGrid\"", xaml);
        Assert.Contains("x:Name=\"SaveActualButton\"", xaml);
        Assert.Contains("x:Name=\"WarehouseRouteBox\"", xaml);
        Assert.Contains("إرسال مباشر إلى مخزن التام", code);
        Assert.Contains("إرسال إلى الفحص أولاً", code);
        Assert.Contains("WarehouseRoute = WarehouseRouteBox.SelectedIndex == 0 ? ProductionWarehouseRoutes.Direct : ProductionWarehouseRoutes.Quality", code);
        Assert.Contains("WarehouseRouteBox.SelectedIndex is not (0 or 1)", code);
        Assert.Contains("validStockIds.Contains(productId)", Read("src/DatesErp.Application/Services/ProductionDeliveryService.Actual.cs"));
        Assert.Contains("x:Name=\"SecondaryTotalsLabel\"", xaml);
        Assert.Contains("Binding Source, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged", xaml);
        Assert.Contains("<ItemsControl x:Name=\"SecondaryGrid\">", xaml);
        Assert.Contains("Text=\"{Binding Quantity, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"", xaml);
        Assert.DoesNotContain("<DataGrid x:Name=\"SecondaryGrid\"", xaml);
        Assert.Contains("loadActiveOrders: true, selectFirst: false", code);
        Assert.Contains("searchText: query, selectFirst: false", code);
        Assert.Contains("OrderBox.SelectedItem = selectFirst", code);
        Assert.Contains("الفعلي — اكتب في الخانة الصفراء", xaml);
        Assert.Contains("x:Name=\"RawEstimateHint\"", xaml);
        Assert.Contains("IsReadOnly=\"True\"", xaml);
        Assert.Contains("ConsumedRawKg = 0", code);
        Assert.Contains("RawMaterialBasisKg", code);
        Assert.Contains("SourceOrderItemId = row.Source.OrderItemId", code);
        Assert.Contains("RawProductId = row.Source.RawProductId", code);
        Assert.Contains("RawLotId = row.Source.RawLotId", code);
        Assert.Contains("CustomerId = row.Source.CustomerId", code);
        Assert.Contains("Printing.StoredPrintModels.Execution", code);
        Assert.Contains("TryQuantity", rows);
        Assert.DoesNotContain("OpenDeliveryButton", xaml);
        Assert.DoesNotContain("OpenProductionDelivery_Click", code);
        Assert.DoesNotContain("فتح أمر التسليم", xaml);
        Assert.DoesNotContain("📥 سند الاستلام المخزني", xaml);
    }

    [Fact]
    public void Production_Navigation_No_Longer_Offers_Delivery_Screen_And_Receipt_Belongs_To_Warehouse()
    {
        var orderWindows = Read("src/DatesErp.Desktop/Views/Screens/OrdersWindows.cs");
        var finishedXaml = Read("src/DatesErp.Desktop/Views/Screens/FinishedGoodsView.xaml");
        var actualService = Read("src/DatesErp.Application/Services/ProductionDeliveryService.Actual.cs");
        var executionService = Read("src/DatesErp.Application/Services/ExecutionService.cs");

        Assert.Contains("ProductionActualCloseView.OpenForOrder(_orderId", orderWindows);
        Assert.DoesNotContain("ProductionDeliveryView.OpenForOrder", orderWindows);
        Assert.DoesNotContain("Click=\"OpenProductionDelivery_Click\"", finishedXaml);
        Assert.Contains("أوامر استلام الإنتاج", Read("src/DatesErp.Desktop/Screens/ScreenCatalog.cs"));
        Assert.Contains("ByProductSources = orderItems.SelectMany", actualService);
        Assert.Contains("SourceOrderItemId = bp.SourceOrderItemId", executionService);
        Assert.Contains("RawProductId = bp.RawProductId", executionService);
        Assert.Contains("RawLotId = bp.RawLotId", executionService);
        Assert.Contains("CustomerId = bp.CustomerId", executionService);
    }
}
