using System.IO;
using Xunit;

namespace DatesErp.Tests;

public sealed class FGReceiveScreenWorkflowTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DateERP.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    [Fact]
    public void ReceiptSearch_Is_A_Separate_Selector_That_Opens_The_Selected_Receipt_In_The_Main_Form()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src/DatesErp.Desktop/Views/Screens/FGReceiveView.xaml"));
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "src/DatesErp.Desktop/Views/Screens/FGReceiveView.xaml.cs"));
        Assert.DoesNotContain("RecordsGrid", xaml);
        Assert.DoesNotContain("سندات الاستلام (نقر مزدوج للفتح)", xaml);
        Assert.Contains("new DocSearchWindow(\"سندات استلام الإنتاج\"", code);
        Assert.Contains("if (win.ShowDialog() == true && win.SelectedId is int id)", code);
        Assert.Contains("OpenReceipt(id)", code);
        Assert.Contains("receipt.DocumentNumber", code);
        Assert.Contains("receipt.ReceiptNumber", code);
    }

    [Fact]
    public void Finished_Goods_Use_Carton_Input_While_Secondary_Outputs_Retain_Kilogram_Input()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src/DatesErp.Desktop/Views/Screens/FGReceiveView.xaml"));
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "src/DatesErp.Desktop/Views/Screens/FGReceiveView.xaml.cs"));
        Assert.Contains("Text=\"{Binding ReceiptPackages, UpdateSourceTrigger=PropertyChanged}\"", xaml);
        Assert.Contains("Text=\"{Binding ReceiveNow, UpdateSourceTrigger=PropertyChanged}\"", xaml);
        Assert.Contains("المخرجات الثانوية بالكيلوجرام", xaml);
        Assert.DoesNotContain("الاستلام الآن (كجم)", xaml);
        Assert.Contains("PackageCount = l.IsByProduct ? 0 : l.ReceiptPackages, NetWeightKg = l.ReceiveNow", code);
        Assert.Contains("private int _receiptPackages", code);
        Assert.Contains("_receiptPackages * CartonWeightKg", code);
    }

    [Fact]
    public void Approval_Button_Is_Gated_By_Finished_Goods_Approval_Permission()
    {
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "src/DatesErp.Desktop/Views/Screens/FGReceiveView.xaml.cs"));
        Assert.Contains(".WithApprove((_, _) => ApproveReceipt()", code);
        Assert.Contains("finishedgoods\", \"Approve\"", code);
        Assert.Contains("SetApproveEnabled(!r.IsApproved && r.Status == DocStatuses.Submitted)", code);
        Assert.Contains("svc.ApproveReceipt(_currentId)", code);
    }
}
