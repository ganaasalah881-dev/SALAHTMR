using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DatesErp.Core.Domain.Entities;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Views;

public partial class ShipmentPickerWindow : Window
{
    public int? SelectedShipmentId { get; private set; }

    public class ShipmentRow
    {
        public int Id { get; set; }
        public string DocumentNumber { get; set; }
        public string CustomerName { get; set; }
        public DateTime? ReceivedDate { get; set; }
        public double TotalWeightKg { get; set; }
        public int TotalCartons { get; set; }
        public int ItemsCount { get; set; }
        public string ContainerNumber { get; set; }
        public string VesselName { get; set; }
    }

    private List<ShipmentRow> _allRows = new();

    public ShipmentPickerWindow(List<Shipment> shipments)
    {
        InitializeComponent();
        LoadRows(shipments);
    }

    private void LoadRows(List<Shipment> shipments)
    {
        try
        {
            Dictionary<int, string> customerNames = new();
            using (var scope = AppContainer.NewScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                customerNames = db.Customers.AsNoTracking().ToDictionary(c => c.Id, c => c.CustomerName ?? c.CustomerCode);
            }

            _allRows = shipments.Select(s => new ShipmentRow
            {
                Id = s.Id,
                DocumentNumber = s.DocumentNumber,
                CustomerName = customerNames.TryGetValue(s.CustomerId, out var name) ? name : $"عميل #{s.CustomerId}",
                ReceivedDate = s.ReceivedDate,
                TotalWeightKg = s.TotalWeightKg,
                TotalCartons = s.TotalCartons,
                ItemsCount = s.Items?.Count ?? s.ItemCount,
                ContainerNumber = s.ContainerNumber ?? "—",
                VesselName = s.VesselName ?? "—"
            }).ToList();

            ApplyFilter();
        }
        catch
        {
            _allRows = shipments.Select(s => new ShipmentRow
            {
                Id = s.Id,
                DocumentNumber = s.DocumentNumber,
                CustomerName = $"عميل #{s.CustomerId}",
                ReceivedDate = s.ReceivedDate,
                TotalWeightKg = s.TotalWeightKg,
                TotalCartons = s.TotalCartons,
                ItemsCount = s.Items?.Count ?? s.ItemCount,
                ContainerNumber = s.ContainerNumber ?? "—",
                VesselName = s.VesselName ?? "—"
            }).ToList();
            ApplyFilter();
        }
    }

    private void ApplyFilter()
    {
        var term = SearchBox?.Text?.Trim().ToLower() ?? "";
        var filtered = string.IsNullOrWhiteSpace(term)
            ? _allRows
            : _allRows.Where(r =>
                (r.DocumentNumber?.ToLower().Contains(term) ?? false) ||
                (r.CustomerName?.ToLower().Contains(term) ?? false) ||
                (r.ContainerNumber?.ToLower().Contains(term) ?? false) ||
                (r.VesselName?.ToLower().Contains(term) ?? false)).ToList();

        Grid.ItemsSource = filtered;
        CountText.Text = $"عدد الإيصالات: {filtered.Count} من أصل {_allRows.Count}";
        if (filtered.Count > 0 && Grid.SelectedItem == null)
            Grid.SelectedIndex = 0;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void Grid_DoubleClick(object sender, MouseButtonEventArgs e) => ConfirmSelection();

    private void Ok_Click(object sender, RoutedEventArgs e) => ConfirmSelection();

    private void ConfirmSelection()
    {
        if (Grid.SelectedItem is ShipmentRow r)
        {
            SelectedShipmentId = r.Id;
            DialogResult = true;
            Close();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
