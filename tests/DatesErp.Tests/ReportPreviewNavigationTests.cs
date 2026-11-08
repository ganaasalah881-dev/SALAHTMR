using Xunit;

namespace DatesErp.Tests;

public sealed class ReportPreviewNavigationTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DateERP.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string path) => File.ReadAllText(Path.Combine(RepoRoot(), path));

    [Fact]
    public void Operational_Preview_Selects_Modern_Template_And_Renders_Immediately()
    {
        var preview = Read("src/DatesErp.Desktop/Views/PrintPreviewWindow.xaml.cs");
        Assert.Contains("_model = model;", preview);
        Assert.Contains("TemplatePicker.SelectedItem = TemplatePicker.Items.Cast<TemplateOption>()", preview);
        Assert.Contains("option.Template == PrintTemplate.Modern", preview);
        Assert.Contains("SetDocument(PhasePrint.Build(_model, option.Template));", preview);
    }

    [Fact]
    public void Report_And_Customer_Delivery_Use_Template_Aware_Preview()
    {
        var exports = Read("src/DatesErp.Desktop/Services/ExportPrintService.cs");
        Assert.Contains("var model = BuildPrintModel(report);", exports);
        Assert.Contains("new Views.PrintPreviewWindow(model, report.TitleAr)", exports);

        var delivery = Read("src/DatesErp.Desktop/Views/Screens/DeliveryView.xaml.cs");
        Assert.Contains("new PrintPreviewWindow(model,", delivery);
        Assert.DoesNotContain("new PrintPreviewWindow(PhasePrint.Build(model)", delivery);
    }

    [Fact]
    public void New_Reports_Window_Offers_Return_To_Main_Window()
    {
        var window = Read("src/DatesErp.Desktop/Views/NewReportsWindow.cs");
        Assert.Contains("العودة إلى الواجهة الرئيسية", window);
        Assert.Contains("ReturnToMainWindow();", window);
        Assert.Contains("main?.Activate();", window);
        Assert.Contains("root.Children.Add(new NewReportsView());", window);
    }
}
