using System.IO;
using Xunit;

namespace DatesErp.Tests;

/// <summary>حارس لمنع زر الحفظ المركب (إنشاء/تعديل) من البقاء محجوباً بسبب فحص صلاحية عملية غير موجودة.</summary>
public sealed class QualityToolbarPermissionTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DateERP.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    [Fact]
    public void Composite_Save_Permission_Uses_Create_Or_Edit_And_Clears_Denial()
    {
        var toolbar = File.ReadAllText(Path.Combine(RepoRoot(), "src/DatesErp.Desktop/Views/ErpToolbar.cs"));
        Assert.Contains("private static bool IsOperationAllowed(string module, string operation)", toolbar);
        Assert.Contains("PermissionGate.Can(module, \"Create\") || PermissionGate.Can(module, \"Edit\")", toolbar);
        Assert.Contains("IsOperationAllowed(Module, kv.Value)", toolbar);
        Assert.Contains("IsOperationAllowed(Module, requiresOp)", toolbar);
        Assert.Contains("_denied.Remove(kv.Key)", toolbar);
        Assert.Contains("_denied.Remove(b)", toolbar);
    }

    [Fact]
    public void Quality_Save_Uses_Create_Or_Edit_And_Approval_Waits_For_Saved_Results()
    {
        var quality = File.ReadAllText(Path.Combine(RepoRoot(), "src/DatesErp.Desktop/Views/Screens/QualityView.xaml.cs"));
        Assert.Contains(".WithSaveForCreateOrEdit", quality);
        Assert.Contains("chrome.SetPermissionModule(\"quality\")", quality);
        Assert.Contains("SetSaveEnabled(canEditNow)", quality);
        Assert.Contains("_current?.HasInspectionResults == true && !_current.CheckApproved && canApprove", quality);
        Assert.Contains("Load(dto.OrderId, r.Id, statusAfterLoad:", quality);
        Assert.Contains("Load(orderId, checkId, statusAfterLoad:", quality);
    }

    [Fact]
    public void Quality_Search_Opens_An_Unambiguous_Match_And_Empty_Search_Gives_Feedback()
    {
        var quality = File.ReadAllText(Path.Combine(RepoRoot(), "src/DatesErp.Desktop/Views/Screens/QualityView.xaml.cs"));
        Assert.Contains(".WithSearch((_, _) => SearchSources_Click(this, new RoutedEventArgs())", quality);
        Assert.Contains("if (term.Length == 0)", quality);
        Assert.Contains("else if (matches.Count == 1)", quality);
        Assert.Contains("SourceBox.SelectedItem = matches[0]", quality);
    }
}
