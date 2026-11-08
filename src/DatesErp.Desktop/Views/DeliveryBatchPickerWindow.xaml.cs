using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Views;

public sealed record DeliveryBatchSelection(DeliveryPickRow Source, int Cartons);

public sealed class DeliveryBatchCandidate : INotifyPropertyChanged
{
    private bool _isSelected;
    private int _requestedCartons;
    public DeliveryPickRow Source { get; }
    public DeliveryBatchCandidate(DeliveryPickRow source) => Source = source;
    public int ProductId => Source.ProductId;
    public string ProductName => Source.ProductName;
    public string ProductCode => Source.ProductCode;
    public string LotCode => Source.LotCode;
    public string PackName => Source.PackName;
    public string WarehouseName => Source.WarehouseName;
    public string ProductionDate => Source.ProductionDate;
    public int ReceivedCartons => Source.ReceivedCartons;
    public int DeliveredCartons => Source.PreviouslyDeliveredCartons;
    public int AvailableCartons => Source.AvailableCartons;
    public string Grade => Source.GradeAr;
    public bool IsSelectable => Source.QcReady && Source.AvailableCartons > 0 && Source.CartonWeightKg > 0;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; Changed(nameof(IsSelected)); RefreshSelection(); }
    }
    public int RequestedCartons
    {
        get => _requestedCartons;
        set { var v = Math.Max(0, value); if (_requestedCartons == v) return; _requestedCartons = v; Changed(nameof(RequestedCartons)); RefreshSelection(); }
    }
    public event PropertyChangedEventHandler PropertyChanged;
    public event Action ChangedSelection;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void RefreshSelection()
    {
        if (_requestedCartons > 0 && !_isSelected) { _isSelected = true; Changed(nameof(IsSelected)); }
        ChangedSelection?.Invoke();
    }
    public void SelectAllAvailable() { if (!IsSelectable) return; _requestedCartons = AvailableCartons; _isSelected = true; Changed(nameof(RequestedCartons)); Changed(nameof(IsSelected)); ChangedSelection?.Invoke(); }
}

public partial class DeliveryBatchPickerWindow : Window
{
    private readonly ObservableCollection<DeliveryBatchCandidate> _rows = new();
    private readonly ICollectionView _view;
    public List<DeliveryBatchSelection> Selections { get; private set; } = new();

    public DeliveryBatchPickerWindow(int customerId)
    {
        InitializeComponent();
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = FilterRow;
        BatchGrid.ItemsSource = _view;
        foreach (var row in LoadRows(customerId))
        {
            var candidate = new DeliveryBatchCandidate(row);
            candidate.ChangedSelection += RefreshSelectionSummary;
            _rows.Add(candidate);
        }
        string customerName = LoadCustomerName(customerId);
        CustomerLabel.Text = $"العميل: {customerName} — تظهر أرصدته الفعلية القابلة للتسليم في المخازن التامة فقط.";
        int physicalCartons = _rows.Sum(x => x.AvailableCartons);
        int qcCartons = _rows.Where(x => x.IsSelectable).Sum(x => x.AvailableCartons);
        BalanceSummary.Text = $"الرصيد الفعلي: {physicalCartons:N0} كرتون  |  القابل للاختيار بعد الجودة: {qcCartons:N0} كرتون";
        EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshSelectionSummary();
    }

    private static List<DeliveryPickRow> LoadRows(int customerId)
    {
        using var scope = AppContainer.NewScope();
        return scope.ServiceProvider.GetRequiredService<DeliveryPickService>().GetPickList(customerId, DateTime.Now);
    }

    private static string LoadCustomerName(int customerId)
    {
        using var scope = AppContainer.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErp.Infrastructure.Persistence.DatesErpDbContext>();
        return db.Customers.Where(c => c.Id == customerId).Select(c => c.CustomerName).FirstOrDefault() ?? $"#{customerId}";
    }

    private bool FilterRow(object obj)
    {
        if (obj is not DeliveryBatchCandidate row) return false;
        var term = SearchBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(term)) return true;
        return row.ProductName.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || row.ProductCode.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || row.LotCode.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || row.WarehouseName.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || row.PackName.Contains(term, StringComparison.CurrentCultureIgnoreCase);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => _view?.Refresh();

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.SelectAllAvailable();
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) { row.RequestedCartons = 0; row.IsSelected = false; }
    }

    private void RefreshSelectionSummary()
    {
        if (SelectionSummary == null) return;
        var selected = _rows.Where(r => r.IsSelected && r.RequestedCartons > 0).ToList();
        SelectionSummary.Text = selected.Count == 0
            ? "حدد الدفعة وأدخل الكمية بالكرتون"
            : $"الدفعات: {selected.Count}  |  إجمالي التسليم: {selected.Sum(r => r.RequestedCartons):N0} كرتون";
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BatchGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            BatchGrid.CommitEdit(DataGridEditingUnit.Row, true);
        }
        catch { }
        // Only a positive carton quantity is an actual delivery request; a checked but empty row
        // must not prevent adding another eligible batch.
        var selected = _rows.Where(r => r.RequestedCartons > 0).ToList();
        if (selected.Count == 0)
        {
            AppContainer.Get<DialogService>().Info("حدد دفعة قابلة للتسليم وأدخل الكمية المطلوبة بالكرتون.");
            return;
        }
        foreach (var row in selected)
        {
            if (!row.IsSelectable)
            {
                string reason = !row.Source.QcReady
                    ? $"إفراج الجودة غير مكتمل ({row.Grade})"
                    : row.AvailableCartons <= 0 ? "لا يوجد رصيد كرتوني متاح" : "وزن الكرتون غير معرّف في بيانات الرصيد";
                AppContainer.Get<DialogService>().Error($"الدفعة «{row.LotCode}» غير متاحة: {reason}.");
                return;
            }
            if (row.RequestedCartons <= 0 || row.RequestedCartons > row.AvailableCartons)
            {
                AppContainer.Get<DialogService>().Error($"كمية «{row.ProductName} / {row.LotCode}» يجب أن تكون بين 1 و{row.AvailableCartons:N0} كرتون.");
                return;
            }
        }
        Selections = selected.Select(r => new DeliveryBatchSelection(r.Source, r.RequestedCartons)).ToList();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void BatchGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Apply_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }
}
