using System.IO;
using Xunit;

namespace DatesErp.Tests;

/// <summary>Regression checks for the explicit production-order workflow: blank start, plan preview, explicit save.</summary>
public class OrdersScreenToolbarTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DateERP.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string Read(string rel) => File.ReadAllText(Path.Combine(RepoRoot(), rel));

    [Fact]
    public void Screen_Starts_Empty_And_Requires_Explicit_Add_And_Save()
    {
        string xaml = Read("src/DatesErp.Desktop/Views/Screens/OrdersView.xaml");
        string screen = Read("src/DatesErp.Desktop/Views/Screens/OrdersView.xaml.cs");
        Assert.Contains("Loaded += (_, _) =>", screen);
        Assert.Contains("ResetWorkspace();", screen);
        int loadedStart = screen.IndexOf("Loaded +=", StringComparison.Ordinal);
        int loadedEnd = screen.IndexOf("Unloaded +=", loadedStart, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshToday()", screen[loadedStart..loadedEnd]);
        Assert.Contains("TodayGrid.ItemsSource = null;", screen);
        Assert.Contains("PromptAddFromPlan(Window.GetWindow(this), PreviewSelectedGroup)", screen);
        Assert.Contains("IssuePlanMultiCustomerGroup(_selectedGroup.PlanId", screen);
        Assert.Contains("Visibility=\"Collapsed\"", xaml);
        Assert.Contains("WorkflowStateText", xaml);
        Assert.Contains("GridEmptyHint", xaml);
        Assert.Contains(".WithNew", screen);
        Assert.Contains("WithSaveForCreateOrEdit", screen);
    }

    [Fact]
    public void Plan_Chooser_Shows_Full_Group_And_Line_Details_Without_Issuing()
    {
        string windows = Read("src/DatesErp.Desktop/Views/Screens/OrdersWindows.cs");
        string dto = Read("src/DatesErp.Core/Interfaces/Services/TodayProductionDto.cs");
        Assert.Contains("GetPendingPlanGroups()", windows);
        Assert.Contains("PlanTitle", windows);
        Assert.Contains("Customers", windows);
        Assert.Contains("RawName", windows);
        Assert.Contains("LotCode", windows);
        Assert.Contains("ShipmentNo", windows);
        Assert.Contains("WarehouseName", windows);
        Assert.Contains("PackName", windows);
        Assert.Contains("selectedCallback?.Invoke(selected)", windows);
        Assert.Contains("PlanTitle", dto);
        Assert.Contains("CustomerCount", dto);
    }

    [Fact]
    public void Previously_Issued_Group_Is_Excluded_And_Service_Rejects_Partial_Duplicate()
    {
        string today = Read("src/DatesErp.Application/Services/ProductionOrderService.Today.cs");
        Assert.Contains(".Where(g => g.All(e => !existingIds.Contains(e.Item.Id)))", today);
        Assert.Contains("if (existing.Count > 0)", today);
        Assert.Contains("لا يُنشأ أمر متعدد العملاء جزئياً", today);
        Assert.Contains("IssuePlanMultiCustomerGroup", today);
    }

    [Fact]
    public void Layout_Matches_Reference_Structure_And_Chooser_Opens_With_Details()
    {
        string xaml = Read("src/DatesErp.Desktop/Views/Screens/OrdersView.xaml");
        string windows = Read("src/DatesErp.Desktop/Views/Screens/OrdersWindows.cs");
        string screen = Read("src/DatesErp.Desktop/Views/Screens/OrdersView.xaml.cs");
        Assert.Contains("#16283C", xaml);
        Assert.Contains("⚖ إجماليات المجموعة", xaml);
        Assert.Contains("x:Name=\"TodayGrid\"", xaml);
        Assert.Contains("x:Name=\"DetailsHint\"", xaml);
        Assert.Contains("Width = 1060", windows);
        Assert.Contains("Height = 112", windows);
        Assert.Contains("Height = 190", windows);
        Assert.Contains("groups.SelectedIndex = 0", windows);
        Assert.Contains("ShowPendingRowDetails", screen);
        Assert.DoesNotContain("ProductionDeliveryView.OpenForOrder", screen);
    }

    [Fact]
    public void Add_Plan_Shows_Historical_Approved_Schedules_And_Explains_Blocked_Rows()
    {
        string today = Read("src/DatesErp.Application/Services/ProductionOrderService.Today.cs");
        string windows = Read("src/DatesErp.Desktop/Views/Screens/OrdersWindows.cs");
        Assert.Contains("&& (p.IsApproved || (p.ApprovedBy != null && p.ApprovedDate != null))", today);
        Assert.Contains("return BuildPendingGroups(ApprovedScheduledEntries());", today);
        Assert.DoesNotContain(".Where(e => PlanRawRequirementError(e.Item) == null)", today);
        Assert.Contains("CanIssue = rawError == null", today);
        Assert.Contains("Binding(\"AvailabilityStatus\")", windows);
        Assert.Contains("selected.BlockReason", windows);
        Assert.Contains("الخطط السابقة", windows);
    }

    [Fact]
    public void Planning_Item_Pickers_Show_Only_Finished_Products_Linked_To_Selected_Raw()
    {
        string service = Read("src/DatesErp.Application/Services/PlanningService.cs");
        string view = Read("src/DatesErp.Desktop/Views/Screens/PlanningView.xaml.cs");
        string window = Read("src/DatesErp.Desktop/Views/Screens/PlanningWindows.cs");
        string picker = Read("src/DatesErp.Desktop/Views/ProductPickerWindow.xaml.cs");
        Assert.Contains("GetFinishedProductsForRaw(rawId)", service);
        Assert.Contains("row.LoadFinishedProducts = sourceRawId => svc.GetFinishedProductsForRaw(sourceRawId)", view);
        Assert.Contains("GetFinishedProductsForRaw(linkedRawId)", view);
        Assert.Contains("linkedProducts.Select(p => p.Id)", view);
        Assert.Contains("IEnumerable<int> allowedProductIds = null", picker);
        Assert.Contains("allowedIds.Contains(p.Id)", picker);
        Assert.Contains("مرتبطة بهذا الخام", window);
        Assert.Contains("اختر صنفاً تاماً مرتبطاً بالخام المحدد", window);
    }

    [Fact]
    public void Toolbar_Communicates_Available_Actions_And_Active_Step()
    {
        string screen = Read("src/DatesErp.Desktop/Views/Screens/OrdersView.xaml.cs");
        string toolbar = Read("src/DatesErp.Desktop/Views/ErpToolbar.cs");
        Assert.Contains("MarkToolbarAction", screen);
        Assert.Contains("▶ ", screen);
        Assert.Contains("_toolbar.EditBtn.IsEnabled = canEdit && _panel?.CanEdit == true", screen);
        Assert.Contains("_toolbar.DeleteBtn.IsEnabled = canDelete && _panel?.CanDelete == true", screen);
        Assert.Contains("WithSaveForCreateOrEdit", toolbar);
        Assert.Contains("PermissionGate.Can(Module, \"Create\") || PermissionGate.Can(Module, \"Edit\")", toolbar);
    }
}
