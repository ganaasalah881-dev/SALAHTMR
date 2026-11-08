using Xunit;

namespace DatesErp.Tests;

/// <summary>Regression guard for the customer delivery picker activation sequence.</summary>
public sealed class DeliveryPickerEnablementTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DateERP.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void Selecting_Customer_Refreshes_Picker_Enablement_After_Loading_Balances()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DatesErp.Desktop", "Views", "Screens", "DeliveryView.xaml.cs"));
        var changedStart = source.IndexOf("private void Customer_Changed(", StringComparison.Ordinal);
        Assert.True(changedStart >= 0);
        var changedEnd = source.IndexOf("private void LoadCustomerBalances(", changedStart, StringComparison.Ordinal);
        Assert.True(changedEnd > changedStart);
        var handler = source[changedStart..changedEnd];

        Assert.Contains("LoadCustomerBalances(newId);", handler);
        Assert.Contains("ApplyUiState();", handler);
        Assert.Contains("ChooseBatchesButton.IsEnabled = _isEditing && !_locked && _currentCustomerId > 0;", source);
    }

    [Fact]
    public void Picker_And_Delivery_Grids_Show_Carton_Quantities_Only_And_Fit_Checkbox()
    {
        var pickerXaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DatesErp.Desktop", "Views", "DeliveryBatchPickerWindow.xaml"));
        var deliveryXaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DatesErp.Desktop", "Views", "Screens", "DeliveryView.xaml"));
        Assert.Contains("Header=\"اختيار\" Width=\"50\"", pickerXaml);
        Assert.Contains("Header=\"التسليم (كرتون)\"", pickerXaml);
        Assert.DoesNotContain("كجم", pickerXaml);
        Assert.DoesNotContain("كجم", deliveryXaml);
    }

    [Fact]
    public void Picker_Uses_Actual_Balance_Weight_And_Does_Not_Trust_Unmatched_Qc_Label()
    {
        var service = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DatesErp.Application", "Services", "DeliveryPickService.cs"));
        var picker = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DatesErp.Desktop", "Views", "DeliveryBatchPickerWindow.xaml.cs"));
        Assert.Contains("ctnW = b.QtyKg / b.PackageCount;", service);
        Assert.Contains("string grade = qcReady", service);
        Assert.DoesNotContain("decisions.TryGetValue", service);
        Assert.Contains("var selected = _rows.Where(r => r.RequestedCartons > 0).ToList();", picker);
    }

}
