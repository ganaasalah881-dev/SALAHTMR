using System.IO;
using Xunit;

namespace DatesErp.Tests;

public class CustomerDeliveryWarehouseVisibilityTests
{
    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "DateERP.sln"))) d = d.Parent;
        return d?.FullName ?? "";
    }

    [Fact]
    public void Customer_Delivery_Is_Registered_Under_Warehouses_And_Inventory()
    {
        string root = FindRepoRoot();
        string catalogCs = File.ReadAllText(Path.Combine(root, "src/DatesErp.Desktop/Screens/ScreenCatalog.cs"));

        // شاشة تسليم العملاء (MRPINV1005) يجب أن تكون مسجلة تحت قسم "المخازن والاستلام"
        Assert.Contains("new(\"delivery\", \"المخازن والاستلام\", \"تسليم العملاء", catalogCs);
        Assert.Contains("MRPINV1005", catalogCs);
    }

    [Fact]
    public void Warehouses_Window_Has_Quick_Action_To_Customer_Delivery()
    {
        string root = FindRepoRoot();
        string genericListCs = File.ReadAllText(Path.Combine(root, "src/DatesErp.Desktop/Views/Screens/GenericListView.xaml.cs"));

        // شاشة قائمة المخازن يجب أن تتضمن زر إجراء مخصص لفتح شاشة تسليم العملاء
        Assert.Contains("تسليم العملاء (MRPINV1005)", genericListCs);
        Assert.Contains("OpenScreen(\"delivery\")", genericListCs);
    }

    [Fact]
    public void FG_Receive_Window_Has_Quick_Button_To_Customer_Delivery()
    {
        string root = FindRepoRoot();
        string fgReceiveCs = File.ReadAllText(Path.Combine(root, "src/DatesErp.Desktop/Views/Screens/FGReceiveView.xaml.cs"));

        // شاشة استلام الإنتاج التام في المخازن تحتوي زراً للتحويل إلى تسليم العملاء
        Assert.Contains("تسليم العملاء", fgReceiveCs);
        Assert.Contains("OpenScreen(\"delivery\")", fgReceiveCs);
    }

    [Fact]
    public void Dashboard_Warehouse_Box_Has_Customer_Delivery_Button()
    {
        string root = FindRepoRoot();
        string dashXaml = File.ReadAllText(Path.Combine(root, "src/DatesErp.Desktop/Views/Screens/DashboardView.xaml"));
        string dashCs = File.ReadAllText(Path.Combine(root, "src/DatesErp.Desktop/Views/Screens/DashboardView.xaml.cs"));

        // صندوق الاستلامات بالمخزن في لوحة التحكم التنفيذية يحتوي زر فتح تسليم العملاء
        Assert.Contains("OpenDelivery_Click", dashXaml);
        Assert.Contains("OpenDelivery_Click", dashCs);
        Assert.Contains("OpenScreen(\"delivery\")", dashCs);
    }
}
