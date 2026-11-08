using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DatesErp.Application.Services;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UiFormat = DatesErp.Core.Common.UiFormat;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>سطر واحد لصنف/دفعة/عبوة/مخزن بعينه في سند تسليم العملاء.</summary>
public sealed class DelivBalanceRow : INotifyPropertyChanged
{
    private int _packages;
    private double _qty;
    private bool _validationEnabled;
    private int _rowIndex;
    public int RowIndex { get => _rowIndex; set { _rowIndex = value; Changed(nameof(RowIndex)); } }
    public int ProductId { get; init; }
    public int? LotId { get; init; }
    public int? PackagingTypeId { get; init; }
    public int? WarehouseId { get; init; }
    public string WarehouseName { get; init; } = "—";
    public string ProductName { get; init; } = "—";
    public string Code { get; init; } = "—";
    public string LotCode { get; init; } = "—";
    public string PackName { get; init; } = "—";
    public string Production { get; init; } = "—";
    public string QcStatus { get; init; } = "—";
    public string Grade { get; init; } = "—";
    public double CartonWeight { get; init; }
    public int ReceivedCartons { get; init; }
    public int DeliveredCartons { get; init; }
    public int AvailablePackages { get; init; }
    public double AvailableQty { get; init; }
    public double UnitWeight { get; init; }
    public bool QcReady { get; init; }

    public int Packages
    {
        get => _packages;
        set
        {
            _packages = value;
            _qty = value * (UnitWeight > 0 ? UnitWeight : CartonWeight);
            Changed(nameof(Packages)); Changed(nameof(Qty)); Changed(nameof(IsInvalid)); Changed(nameof(ValidationError));
        }
    }
    public double Qty
    {
        get => _qty;
        set { _qty = value; Changed(nameof(Qty)); Changed(nameof(IsInvalid)); Changed(nameof(ValidationError)); }
    }
    public bool ValidationEnabled
    {
        get => _validationEnabled;
        set { _validationEnabled = value; Changed(nameof(IsInvalid)); Changed(nameof(ValidationError)); }
    }
    public bool IsInvalid => ValidationEnabled && (Packages <= 0 || Packages > AvailablePackages
        || Qty <= 0 || (AvailableQty > 0.001 && Qty > AvailableQty + 0.001) || !QcReady);
    public string ValidationError => !ValidationEnabled ? null
        : !QcReady ? $"لا يسمح بالتسليم قبل اجتياز الجودة: {QcStatus}"
        : Packages <= 0 ? "أدخل كمية أكبر من صفر بالكرتون"
        : Packages > AvailablePackages ? $"التجاوز: المطلوب {Packages:N0} كرتون والمتاح {AvailablePackages:N0}"
        : (AvailableQty > 0.001 && Qty > AvailableQty + 0.001) ? "الكراتين المطلوبة تتجاوز الرصيد الفعلي لهذه الدفعة"
        : null;

    public event PropertyChangedEventHandler PropertyChanged;
    private void Changed(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

/// <summary>شاشة تسليم المنتجات للعملاء: البحث والاختيار داخل نفس الشاشة، والمسودة لا تخصم المخزون.</summary>
public partial class DeliveryView : UserControl
{
    private readonly ObservableCollection<DelivBalanceRow> _items = new();
    private readonly List<DeliveryPickRow> _balanceRows = new();
    private List<int> _deliveryIds = new();
    private int _currentId;
    private int _currentCustomerId;
    private bool _locked;
    private bool _isEditing;
    private bool _isDirty;
    private bool _loadingDocument;
    private bool _initialized;
    private string _reservedDocumentNumber;
    private Views.ErpToolbar _toolbar;

    public DeliveryView()
    {
        InitializeComponent();
        ItemsGrid.ItemsSource = _items;
        _items.CollectionChanged += (_, _) =>
        {
            if (!_loadingDocument && _isEditing) _isDirty = true;
            RenumberRows();
            UpdateSummary();
            UpdateActions();
        };
        ItemsGrid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isEditing && !_loadingDocument) _isDirty = true;
            ItemsGrid.Items.Refresh();
            UpdateSummary();
            UpdateActions();
        }));
        Loaded += (_, _) =>
        {
            Services.ComboBoxAutoShowHelper.Apply(this);
            if (!_initialized) { _initialized = true; Load(); }
        };
    }

    public void AttachChrome(Views.ErpChrome chrome)
    {
        chrome.SetModule("المخازن والاستلام");
        chrome.SetScreenCode("MRPINV1005");
        chrome.SetPermissionModule("delivery");
        _toolbar = new Views.ErpToolbar()
            .WithNew((_, _) => StartNewDelivery(), "سند تسليم جديد (F2)")
            .WithEdit((_, _) => EditCurrentDraft())
            .WithSaveForCreateOrEdit((_, _) => Save(), "💾 حفظ المسودة")
            .WithSearch((_, _) => OpenDeliverySearch(), "بحث سندات التسليم — يفتح في نافذة اختيار")
            .WithUndo((_, _) => UndoSmart(), "تراجع عن التعديلات غير المحفوظة")
            .WithApprove((_, _) => Approve(), "اعتماد ذري وصرف فعلي من رصيد العميل")
            .WithUnapprove((_, _) => Unapprove(), "فك اعتماد السند مع الحركة العكسية")
            .WithDelete((_, _) => DeleteDraft(), "حذف المسودة فقط — لا تخصم مخزوناً")
            .WithPrint((_, _) => Print(), "طباعة النسخة المحفوظة")
            .WithExcel((_, _) => Export())
            .WithNavigation((_, _) => Nav(0), (_, _) => Nav(-1), (_, _) => Nav(1), (_, _) => Nav(int.MaxValue))
            .WithExit((_, _) => (Window.GetWindow(this) as MainWindow)?.OpenPreviousScreen());
        chrome.SetToolbar(_toolbar);
        chrome.SetBody(this);
        chrome.CloseRequested += (_, _) => (Window.GetWindow(this) as MainWindow)?.OpenPreviousScreen();
        UpdateActions();
    }

    private void Load()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            CustomerBox.ItemsSource = db.Customers.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.CustomerName).ToList();
            RefreshList();
            if (MainWindow.PendingDeliveryIdToOpen is int pendingId)
            {
                MainWindow.PendingDeliveryIdToOpen = null;
                OpenDelivery(pendingId);
            }
            else if (PermissionGate.Can("delivery", "Create"))
                NewForm();
            else if (_deliveryIds.Count > 0)
                OpenDelivery(_deliveryIds[0]);
            else
                ResetBlankView();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Delivery.Load"); }
    }

    private void RefreshList()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            _deliveryIds = db.CustomerDeliveries.AsNoTracking().OrderByDescending(d => d.Id).Select(d => d.Id).ToList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Delivery.List"); }
    }

    private void ResetBlankView()
    {
        _loadingDocument = true;
        _currentId = 0; _currentCustomerId = 0; _locked = false; _isEditing = false; _isDirty = false;
        _reservedDocumentNumber = null;
        CustomerBox.SelectedIndex = -1;
        DateBox.SelectedDate = DateTime.Today;
        ContainerBox.Clear(); DriverBox.Clear(); VehicleBox.Clear(); NotesBox.Clear();
        _items.Clear(); _balanceRows.Clear();
        DocNoBox.Text = "—"; StatusBadge.Text = "عرض فقط — لا توجد مستندات";
        _loadingDocument = false;
        LoadCustomerBalances(0);
        ApplyUiState();
    }

    private void StartNewDelivery()
    {
        if (!ConfirmDiscardIfDirty()) { UpdateActions(); return; }
        if (!PermissionGate.Can("delivery", "Create"))
        { AppContainer.Get<DialogService>().Error("لا تملك صلاحية إنشاء سند تسليم."); return; }
        NewForm();
    }

    private void NewForm()
    {
        try
        {
            _loadingDocument = true;
            _currentId = 0; _currentCustomerId = 0; _locked = false; _isEditing = true; _isDirty = false;
            CustomerBox.SelectedIndex = -1;
            DateBox.SelectedDate = DateTime.Today;
            ContainerBox.Clear(); DriverBox.Clear(); VehicleBox.Clear(); NotesBox.Clear();
            _items.Clear(); _balanceRows.Clear();
            using (var scope = AppContainer.NewScope())
                _reservedDocumentNumber = scope.ServiceProvider.GetRequiredService<INumberingService>().Reserve("CD");
            DocNoBox.Text = _reservedDocumentNumber;
            StatusBadge.Text = "جديد — مسودة غير محفوظة";
            LoadCustomerBalances(0);
            _loadingDocument = false;
            ApplyUiState();
        }
        catch (Exception ex)
        {
            _loadingDocument = false;
            _reservedDocumentNumber = null;
            DocNoBox.Text = "تعذر حجز رقم";
            ApplyUiState();
            AppContainer.Get<DialogService>().HandleException(ex, "Delivery.ReserveNumber");
        }
    }

    private void UndoSmart()
    {
        if (!ConfirmDiscardIfDirty()) { UpdateActions(); return; }
        if (_currentId > 0) OpenDelivery(_currentId);
        else if (PermissionGate.Can("delivery", "Create")) NewForm();
        else ResetBlankView();
    }

    private bool ConfirmDiscardIfDirty()
    {
        if (!_isDirty) return true;
        return AppContainer.Get<DialogService>().Confirm("توجد تغييرات غير محفوظة. هل تريد تجاهلها والمتابعة؟", "تجاهل التغييرات");
    }

    private void Header_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loadingDocument && _isEditing) { _isDirty = true; UpdateActions(); }
    }

    private void Customer_Changed(object sender, SelectionChangedEventArgs e)
    {
        int newId = CustomerBox.SelectedValue is int id ? id : 0;
        if (_loadingDocument) { _currentCustomerId = newId; return; }
        if (_isEditing && newId != _currentCustomerId && _items.Count > 0)
        {
            if (!AppContainer.Get<DialogService>().Confirm("سيؤدي تغيير العميل إلى مسح بنود الدفعات المختارة حتى لا تختلط أرصدة عميل بآخر. متابعة؟", "تغيير العميل"))
            {
                _loadingDocument = true; CustomerBox.SelectedValue = _currentCustomerId; _loadingDocument = false;
                return;
            }
            _items.Clear();
        }
        _currentCustomerId = newId;
        if (_isEditing) _isDirty = true;
        LoadCustomerBalances(newId);
        // The customer change happens after ApplyUiState disabled the picker for the blank form.
        // Refresh control enablement now, or balances appear while the batch-selection button stays disabled.
        ApplyUiState();
    }

    private void LoadCustomerBalances(int customerId)
    {
        _balanceRows.Clear();
        if (customerId <= 0)
        {
            BalanceChip.Text = "اختر العميل لعرض أرصدته الفعلية.";
            return;
        }
        try
        {
            using var scope = AppContainer.NewScope();
            var pick = scope.ServiceProvider.GetRequiredService<DeliveryPickService>();
            _balanceRows.AddRange(pick.GetPickList(customerId, DateTime.Now));
            int cartons = _balanceRows.Sum(r => r.AvailableCartons);
            int batches = _balanceRows.Count;
            BalanceChip.Text = $"{batches:N0} دفعة متاحة  |  {cartons:N0} كرتون  |  تفاصيل الدفعات والكميات بالكرتون في نافذة الاختيار.";
        }
        catch (Exception ex)
        {
            BalanceChip.Text = "تعذر قراءة أرصدة العميل.";
            AppContainer.Get<DialogService>().HandleException(ex, "Delivery.Balance");
        }
    }

    private void OpenProductPicker_Click(object sender, RoutedEventArgs e)
    {
        if (!_isEditing || _locked) return;
        if (_currentCustomerId <= 0)
        { AppContainer.Get<DialogService>().Info("اختر العميل أولاً."); CustomerBox.Focus(); return; }
        try
        {
            var dialog = new Views.DeliveryBatchPickerWindow(_currentCustomerId) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return;
            var selections = dialog.Selections;
            // تحقّق كل الإضافات قبل تطبيق أيٍّ منها: لا إضافة جزئية ولا مضاعفة صامتة.
            foreach (var s in selections)
            {
                var row = FindLine(s.Source);
                int combined = (row?.Packages ?? 0) + s.Cartons;
                if (combined > s.Source.AvailableCartons)
                {
                    AppContainer.Get<DialogService>().Error($"تكرار الاختيار تجاوز الرصيد: {s.Source.ProductName} / {s.Source.LotCode}. الموجود في السند مع الإضافة {combined:N0} كرتون والمتاح {s.Source.AvailableCartons:N0}.");
                    return;
                }
            }
            _loadingDocument = true;
            foreach (var s in selections)
            {
                var row = FindLine(s.Source);
                if (row == null)
                {
                    row = CreateLine(s.Source, 0);
                    row.ValidationEnabled = _isEditing;
                    _items.Add(row);
                    SubscribeRow(row);
                }
                row.Packages += s.Cartons;
            }
            _loadingDocument = false;
            _isDirty = true;
            ItemsGrid.Items.Refresh();
            UpdateSummary(); UpdateActions();
        }
        catch (Exception ex)
        {
            _loadingDocument = false;
            AppContainer.Get<DialogService>().HandleException(ex, "Delivery.BatchPicker");
        }
    }

    private DelivBalanceRow FindLine(DeliveryPickRow source) => _items.FirstOrDefault(i =>
        i.ProductId == source.ProductId && i.LotId == source.LotId && i.PackagingTypeId == source.PackagingTypeId
        && i.WarehouseId == source.WarehouseId);

    private static DelivBalanceRow CreateLine(DeliveryPickRow source, int cartons) => new()
    {
        ProductId = source.ProductId, LotId = source.LotId, PackagingTypeId = source.PackagingTypeId,
        WarehouseId = source.WarehouseId, WarehouseName = source.WarehouseName,
        ProductName = source.ProductName, Code = source.ProductCode, LotCode = source.LotCode,
        PackName = source.PackName, Production = source.ProductionDate, QcStatus = source.GradeAr,
        Grade = source.GradeAr, CartonWeight = source.CartonWeightKg, UnitWeight = source.CartonWeightKg,
        ReceivedCartons = source.ReceivedCartons,
        DeliveredCartons = source.PreviouslyDeliveredCartons,
        AvailablePackages = source.AvailableCartons, AvailableQty = source.AvailableKg,
        QcReady = source.QcReady, Packages = cartons
    };

    private void SubscribeRow(DelivBalanceRow row)
    {
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DelivBalanceRow.Packages) or nameof(DelivBalanceRow.Qty))
            {
                if (_isEditing && !_loadingDocument) _isDirty = true;
                UpdateSummary(); UpdateActions();
            }
        };
    }

    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (!_isEditing || _locked) return;
        if (ItemsGrid.SelectedItem is DelivBalanceRow row)
        {
            _items.Remove(row); _isDirty = true; UpdateSummary(); UpdateActions();
        }
        else AppContainer.Get<DialogService>().Info("حدد سطر التسليم أولاً.");
    }

    private void ClearBasket_Click(object sender, RoutedEventArgs e)
    {
        if (!_isEditing || _locked || _items.Count == 0) return;
        if (!AppContainer.Get<DialogService>().Confirm("مسح جميع بنود التسليم من الشاشة؟ لن يتغير المخزون ما لم تعتمد السند.", "مسح البنود")) return;
        _items.Clear(); _isDirty = true; UpdateSummary(); UpdateActions();
    }

    private void RenumberRows()
    {
        for (int i = 0; i < _items.Count; i++) _items[i].RowIndex = i + 1;
    }

    private void UpdateSummary()
    {
        var rows = _items.ToList();
        TotProdsDisp.Text = rows.Select(i => i.ProductId).Distinct().Count().ToString("N0");
        TotLotsDisp.Text = rows.Where(i => i.LotId != null).Select(i => i.LotId).Distinct().Count().ToString("N0");
        TotCartonsDisp.Text = $"{rows.Sum(i => i.Packages):N0} كرتون";
    }

    private void ApplyUiState()
    {
        CustomerBox.IsEnabled = _isEditing && !_locked;
        DateBox.IsEnabled = _isEditing && !_locked;
        ContainerBox.IsReadOnly = !_isEditing || _locked;
        DriverBox.IsReadOnly = !_isEditing || _locked;
        VehicleBox.IsReadOnly = !_isEditing || _locked;
        NotesBox.IsReadOnly = !_isEditing || _locked;
        ItemsGrid.IsReadOnly = !_isEditing || _locked;
        ChooseBatchesButton.IsEnabled = _isEditing && !_locked && _currentCustomerId > 0;
        RemoveLineButton.IsEnabled = _isEditing && !_locked;
        ClearLinesButton.IsEnabled = _isEditing && !_locked && _items.Count > 0;
        foreach (var row in _items) row.ValidationEnabled = _isEditing && !_locked;

        if (_locked)
        {
            StatusBadge.Text = "معتمد — تم الصرف";
            StatusBadge.Foreground = System.Windows.Media.Brushes.Green;
        }
        else if (_currentId == 0)
        {
            StatusBadge.Text = _isEditing ? "جديد — مسودة غير محفوظة" : "لا يوجد سند";
            StatusBadge.Foreground = System.Windows.Media.Brushes.DarkGoldenrod;
        }
        else if (_isEditing)
        {
            StatusBadge.Text = _isDirty ? "مسودة — تعديلات غير محفوظة" : "مسودة — وضع التعديل";
            StatusBadge.Foreground = System.Windows.Media.Brushes.DarkOrange;
        }
        else
        {
            StatusBadge.Text = "مسودة محفوظة — لم تخصم المخزون";
            StatusBadge.Foreground = System.Windows.Media.Brushes.SteelBlue;
        }
        UpdateActions();
    }

    private void UpdateActions()
    {
        if (_toolbar == null) return;
        _toolbar.SetMode(_isEditing ? (_currentId == 0 ? Views.ErpScreenMode.Add : Views.ErpScreenMode.Edit) : Views.ErpScreenMode.View);
        bool createAllowed = PermissionGate.Can("delivery", "Create");
        bool editAllowed = PermissionGate.Can("delivery", "Edit");
        bool draft = _currentId > 0 && !_locked;
        bool valid = _currentCustomerId > 0 && _items.Count > 0 && _items.All(i => !i.IsInvalid);
        if (_toolbar.NewBtn != null) _toolbar.NewBtn.IsEnabled = createAllowed && !_toolbar.IsDenied(_toolbar.NewBtn);
        if (_toolbar.EditBtn != null) _toolbar.EditBtn.IsEnabled = draft && !_isEditing && editAllowed && !_toolbar.IsDenied(_toolbar.EditBtn);
        if (_toolbar.SaveBtn != null)
        {
            bool allowed = _currentId == 0 ? createAllowed : editAllowed;
            _toolbar.SaveBtn.IsEnabled = _isEditing && !_locked && allowed && !_toolbar.IsDenied(_toolbar.SaveBtn)
                && (_currentId > 0 || !string.IsNullOrWhiteSpace(_reservedDocumentNumber));
        }
        if (_toolbar.DeleteBtn != null) _toolbar.DeleteBtn.IsEnabled = draft && !_isEditing && !_toolbar.IsDenied(_toolbar.DeleteBtn);
        if (_toolbar.ApproveBtn != null) _toolbar.ApproveBtn.IsEnabled = draft && !_isEditing && !_isDirty && valid && !_toolbar.IsDenied(_toolbar.ApproveBtn);
        if (_toolbar.UnapproveBtn != null) _toolbar.UnapproveBtn.IsEnabled = _locked && !_toolbar.IsDenied(_toolbar.UnapproveBtn);
        if (_toolbar.PrintBtn != null) _toolbar.PrintBtn.IsEnabled = _currentId > 0 && !_toolbar.IsDenied(_toolbar.PrintBtn);
        if (_toolbar.ExcelBtn != null) _toolbar.ExcelBtn.IsEnabled = _items.Count > 0 && !_toolbar.IsDenied(_toolbar.ExcelBtn);
        if (_toolbar.UndoBtn != null) _toolbar.UndoBtn.IsEnabled = _isEditing && !_locked && !_toolbar.IsDenied(_toolbar.UndoBtn);
    }

    private void EditCurrentDraft()
    {
        if (_currentId <= 0 || _locked) { AppContainer.Get<DialogService>().Info("افتح مسودة محفوظة وغير معتمدة أولاً."); return; }
        if (!PermissionGate.Can("delivery", "Edit")) { AppContainer.Get<DialogService>().Error("لا تملك صلاحية تعديل سند التسليم."); return; }
        _isEditing = true; _isDirty = false;
        ApplyUiState();
    }

    private void Save()
    {
        if (!_isEditing || _locked) { AppContainer.Get<DialogService>().Info("اختر «تعديل» لمسودة محفوظة، أو ابدأ سنداً جديداً."); return; }
        if (_currentCustomerId <= 0) { AppContainer.Get<DialogService>().Error("اختر العميل قبل حفظ السند."); CustomerBox.Focus(); return; }
        if (_items.Count == 0) { AppContainer.Get<DialogService>().Error("أضف صنفاً واحداً على الأقل من نافذة اختيار الأصناف والدفعات."); return; }
        ItemsGrid.CommitEdit(DataGridEditingUnit.Cell, true); ItemsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        foreach (var row in _items) row.ValidationEnabled = true;
        if (_items.Any(i => i.IsInvalid))
        {
            var invalid = _items.First(i => i.IsInvalid);
            AppContainer.Get<DialogService>().Error(invalid.ValidationError ?? "راجع الكمية المتاحة وحالة الجودة قبل الحفظ.");
            return;
        }
        try
        {
            var dt = DateBox.SelectedDate ?? DateTime.Today;
            string date = dt.ToString(UiFormat.DatePattern);
            var lines = _items.Select(i => new CustomerDeliveryItemDto
            {
                ProductId = i.ProductId, LotId = i.LotId, PackagingTypeId = i.PackagingTypeId,
                WarehouseId = i.WarehouseId, PackageCount = i.Packages, QtyKg = i.Qty
            }).ToList();
            string notes = PackNotes(VehicleBox.Text, DriverBox.Text, ContainerBox.Text, NotesBox.Text);
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<ICustomerDeliveryService>();
            var result = _currentId == 0
                ? svc.Save(_currentCustomerId, date, null, lines, notes, _reservedDocumentNumber)
                : svc.Update(_currentId, _currentCustomerId, date, null, lines, notes);
            if (!result.Ok) { AppContainer.Get<DialogService>().Error(result.Message); return; }
            AppContainer.Get<DialogService>().Info(result.Message);
            int id = result.Id > 0 ? result.Id : _currentId;
            _reservedDocumentNumber = null;
            RefreshList();
            OpenDelivery(id);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Delivery.SaveDraft"); }
    }

    private void Approve()
    {
        if (_currentId <= 0 || _locked) { AppContainer.Get<DialogService>().Error("احفظ المسودة أولاً ثم اعتمدها."); return; }
        if (_isEditing || _isDirty) { AppContainer.Get<DialogService>().Error("احفظ التعديلات أولاً. الاعتماد ينفّذ نسخة السند المحفوظة فقط."); return; }
        if (!_items.Any() || _items.Any(i => i.IsInvalid)) { AppContainer.Get<DialogService>().Error("لا يمكن اعتماد سند بلا بنود صالحة ومتاحة."); return; }
        if (!AppContainer.Get<DialogService>().ConfirmImpact("اعتماد سند التسليم الآن؟ سيتم صرف الكميات من أرصدة العميل، ولا تُنفذ العملية إلا إذا نجحت كل البنود.",
            $"الكراتين: {_items.Sum(i => i.Packages):N0}", $"الدفعات: {_items.Where(i => i.LotId != null).Select(i => i.LotId).Distinct().Count():N0}", "سيُعاد التحقق من العميل والدفعة والمخزن والرصيد داخل معاملة ذرية")) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var result = scope.ServiceProvider.GetRequiredService<ICustomerDeliveryService>().Approve(_currentId);
            if (!result.Ok) { AppContainer.Get<DialogService>().Error(result.Message); return; }
            AppContainer.Get<DialogService>().Info(result.Message);
            RefreshList(); OpenDelivery(_currentId);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Delivery.Approve"); }
    }

    private void Unapprove()
    {
        if (_currentId <= 0 || !_locked) return;
        if (!AppContainer.Get<DialogService>().ConfirmImpact("فك اعتماد سند التسليم؟ ستُسجل حركة عكسية لإرجاع الكميات، ويُفتح السند للتعديل.", "لن يُحذف سجل الحركة الأصلي", "يتطلب صلاحية إلغاء الاعتماد")) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var result = scope.ServiceProvider.GetRequiredService<ICustomerDeliveryService>().Unapprove(_currentId);
            if (!result.Ok) { AppContainer.Get<DialogService>().Error(result.Message); return; }
            AppContainer.Get<DialogService>().Info(result.Message);
            RefreshList(); OpenDelivery(_currentId);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Delivery.Unapprove"); }
    }

    private void DeleteDraft()
    {
        if (_currentId <= 0 || _locked || _isEditing) { AppContainer.Get<DialogService>().Info("احفظ المسودة وافتحها في وضع العرض قبل حذفها."); return; }
        if (!AppContainer.Get<DialogService>().Confirm("حذف المسودة؟ لا أثر مخزنياً لها لأنها لم تُعتمد.", "حذف مسودة التسليم")) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var result = scope.ServiceProvider.GetRequiredService<ICustomerDeliveryService>().DeleteDraft(_currentId);
            if (!result.Ok) { AppContainer.Get<DialogService>().Error(result.Message); return; }
            AppContainer.Get<DialogService>().Info(result.Message);
            RefreshList();
            if (PermissionGate.Can("delivery", "Create")) NewForm(); else if (_deliveryIds.Count > 0) OpenDelivery(_deliveryIds[0]); else ResetBlankView();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Delivery.DeleteDraft"); }
    }

    private void OpenDeliverySearch()
    {
        if (!ConfirmDiscardIfDirty()) { UpdateActions(); return; }
        try
        {
            var win = new DocSearchWindow("سندات تسليم العملاء",
                new List<SearchFieldDef>
                {
                    new() { Key = "doc", LabelAr = "رقم السند" },
                    new() { Key = "customer", LabelAr = "العميل" },
                    new() { Key = "from", LabelAr = "من تاريخ", Kind = "date" },
                    new() { Key = "to", LabelAr = "إلى تاريخ", Kind = "date" },
                    new() { Key = "minCartons", LabelAr = "إجمالي الكراتين من" },
                    new() { Key = "maxCartons", LabelAr = "إجمالي الكراتين إلى" },
                    new() { Key = "status", LabelAr = "الحالة", Kind = "combo", Options = new[] { "مسودة", "معتمد", "معلق" } }
                }, criteria =>
                {
                    using var scope = AppContainer.NewScope();
                    var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                    var docs = db.CustomerDeliveries.AsNoTracking().OrderByDescending(d => d.Id).ToList();
                    var customers = db.Customers.AsNoTracking().ToDictionary(c => c.Id, c => c.CustomerName ?? "—");
                    string doc = criteria.GetValueOrDefault("doc")?.Trim();
                    string customer = criteria.GetValueOrDefault("customer")?.Trim();
                    string status = criteria.GetValueOrDefault("status")?.Trim();
                    if (!string.IsNullOrWhiteSpace(doc)) docs = docs.Where(d => (d.DocumentNumber ?? "").Contains(doc, StringComparison.CurrentCultureIgnoreCase)).ToList();
                    if (!string.IsNullOrWhiteSpace(customer)) docs = docs.Where(d => customers.TryGetValue(d.CustomerId, out var name) && name.Contains(customer, StringComparison.CurrentCultureIgnoreCase)).ToList();
                    if (TryDate(criteria.GetValueOrDefault("from"), out var from)) docs = docs.Where(d => (d.DeliveryDate ?? DateTime.MinValue).Date >= from.Date).ToList();
                    if (TryDate(criteria.GetValueOrDefault("to"), out var to)) docs = docs.Where(d => (d.DeliveryDate ?? DateTime.MaxValue).Date <= to.Date).ToList();
                    if (TryNumber(criteria.GetValueOrDefault("minCartons"), out var minCartons)) docs = docs.Where(d => d.TotalCartons >= minCartons).ToList();
                    if (TryNumber(criteria.GetValueOrDefault("maxCartons"), out var maxCartons)) docs = docs.Where(d => d.TotalCartons <= maxCartons).ToList();
                    if (status == "معتمد") docs = docs.Where(d => d.IsApproved).ToList();
                    else if (status == "مسودة") docs = docs.Where(d => !d.IsApproved && d.Status != "Hold").ToList();
                    else if (status == "معلق") docs = docs.Where(d => !d.IsApproved && d.Status == "Hold").ToList();
                    var result = new SearchResult
                    {
                        Columns = new List<string> { "رقم السند", "التاريخ", "العميل", "الحالة", "الإجمالي (كرتون)" }
                    };
                    foreach (var d in docs)
                    {
                        string name = customers.TryGetValue(d.CustomerId, out var customerName) ? customerName : "—";
                        string state = d.IsApproved ? "معتمد" : d.Status == "Hold" ? "معلق" : "مسودة";
                        result.Rows.Add((d.Id, new object[] { d.DocumentNumber, UiFormat.D(d.DeliveryDate), name, state, d.TotalCartons }));
                    }
                    return result;
                }) { Owner = Window.GetWindow(this) };
            if (win.ShowDialog() == true && win.SelectedId is int id) OpenDelivery(id);
            else UpdateActions();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Delivery.Search"); }
    }

    private static bool TryDate(string text, out DateTime date)
        => UiFormat.TryParseDate(text, out date);

    private static bool TryNumber(string text, out int value)
        => int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.CurrentCulture, out value)
            || int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value);

    private void OpenDelivery(int id)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var delivery = db.CustomerDeliveries.AsNoTracking().Include(d => d.Items).FirstOrDefault(d => d.Id == id);
            if (delivery == null) { AppContainer.Get<DialogService>().Error("سند التسليم غير موجود."); return; }
            _loadingDocument = true;
            _currentId = delivery.Id; _currentCustomerId = delivery.CustomerId; _locked = delivery.IsApproved;
            _isEditing = false; _isDirty = false; _reservedDocumentNumber = null;
            CustomerBox.SelectedValue = delivery.CustomerId;
            DateBox.SelectedDate = delivery.DeliveryDate;
            DocNoBox.Text = delivery.DocumentNumber;
            var (vehicle, driver, container, notes) = UnpackNotes(delivery.Notes);
            VehicleBox.Text = vehicle; DriverBox.Text = driver; ContainerBox.Text = container; NotesBox.Text = notes;
            LoadCustomerBalances(delivery.CustomerId);
            _items.Clear();
            var productIds = delivery.Items.Select(i => i.ProductId).Distinct().ToList();
            var lotIds = delivery.Items.Where(i => i.LotId != null).Select(i => i.LotId!.Value).Distinct().ToList();
            var warehouseIds = delivery.Items.Where(i => i.WarehouseId != null).Select(i => i.WarehouseId!.Value).Distinct().ToList();
            var products = db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionary(p => p.Id);
            var lots = db.Lots.AsNoTracking().Where(l => lotIds.Contains(l.Id)).ToDictionary(l => l.Id);
            var warehouseNames = db.Warehouses.AsNoTracking().Where(w => warehouseIds.Contains(w.Id)).ToDictionary(w => w.Id, w => w.WarehouseNameAr);
            var packs = db.PackagingTypes.AsNoTracking().ToDictionary(p => p.Id, p => p.PackageNameAr);
            foreach (var item in delivery.Items)
            {
                var source = _balanceRows.FirstOrDefault(r => r.ProductId == item.ProductId && r.LotId == item.LotId
                    && r.PackagingTypeId == item.PackagingTypeId
                    && (item.WarehouseId == null || r.WarehouseId == item.WarehouseId));
                var product = products.GetValueOrDefault(item.ProductId);
                var lot = item.LotId is int lotId && lots.TryGetValue(lotId, out var foundLot) ? foundLot : null;
                double cartonWeight = item.CartonWeightKg > 0 ? item.CartonWeightKg : UnitsPolicy.CartonWeight(db, item.ProductId, item.PackagingTypeId);
                int? warehouseId = item.WarehouseId;
                var row = source != null ? CreateLine(source, item.PackageCount) : new DelivBalanceRow
                {
                    ProductId = item.ProductId, LotId = item.LotId, PackagingTypeId = item.PackagingTypeId,
                    WarehouseId = warehouseId, WarehouseName = warehouseId != null && warehouseNames.TryGetValue(warehouseId.Value, out var whName) ? whName : "—",
                    ProductName = product?.ProductNameAr ?? $"#{item.ProductId}", Code = product?.ProductCode ?? "—",
                    LotCode = lot?.LotCode ?? "—", PackName = item.PackagingTypeId is int packId && packs.TryGetValue(packId, out var packName) ? packName : "—",
                    Production = lot?.LotDate?.ToString("dd/MM/yyyy") ?? "—", CartonWeight = cartonWeight, UnitWeight = cartonWeight,
                    QcStatus = delivery.IsApproved ? "اعتماد محفوظ" : "غير موجود ضمن الرصيد المتاح حالياً",
                    QcReady = delivery.IsApproved, AvailablePackages = 0, AvailableQty = 0
                };
                row.ValidationEnabled = false;
                row.Packages = item.PackageCount;
                row.Qty = item.QtyKg;
                _items.Add(row); SubscribeRow(row);
            }
            _loadingDocument = false;
            ApplyUiState(); UpdateSummary();
            RefreshList();
        }
        catch (Exception ex) { _loadingDocument = false; AppContainer.Get<DialogService>().HandleException(ex, "Delivery.Open"); }
    }

    private void Nav(int direction)
    {
        if (!ConfirmDiscardIfDirty()) { UpdateActions(); return; }
        if (_deliveryIds.Count == 0) return;
        int index = _deliveryIds.IndexOf(_currentId);
        index = direction switch
        {
            0 => 0,
            int.MaxValue => _deliveryIds.Count - 1,
            _ when index < 0 => 0,
            _ => Math.Clamp(index + direction, 0, _deliveryIds.Count - 1)
        };
        OpenDelivery(_deliveryIds[index]);
    }

    private void Print()
    {
        if (_currentId <= 0) { AppContainer.Get<DialogService>().Error("احفظ السند أولاً؛ الطباعة تعرض النسخة المحفوظة فقط."); return; }
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var model = Printing.StoredPrintModels.CustomerDelivery(db, _currentId);
            new PrintPreviewWindow(model, $"{model.DocTitle} {model.DocNo}") { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Delivery.Print"); }
    }

    private void Export()
    {
        var report = new ReportResult
        {
            TitleAr = $"إذن تسليم عميل {DocNoBox.Text}",
            Columns = new List<string> { "الصنف", "الدفعة", "المخزن", "التسليم (كرتون)" },
            Rows = _items.Select(i => new object[] { i.ProductName, i.LotCode, i.WarehouseName, i.Packages }).ToList()
        };
        AppContainer.Get<ExportPrintService>().ExportExcel(report);
    }

    private static string PackNotes(string vehicle, string driver, string container, string userNotes)
    {
        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(vehicle)) meta.Add($"شاحنة: {vehicle.Trim()}");
        if (!string.IsNullOrWhiteSpace(driver)) meta.Add($"سائق: {driver.Trim()}");
        if (!string.IsNullOrWhiteSpace(container)) meta.Add($"حاوية: {container.Trim()}");
        string header = meta.Count > 0 ? $"[{string.Join(" | ", meta)}] " : "";
        return (header + (userNotes ?? "")).Trim();
    }

    private static (string vehicle, string driver, string container, string userNotes) UnpackNotes(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ("", "", "", "");
        string vehicle = "", driver = "", container = "", notes = raw;
        if (raw.StartsWith("[") && raw.Contains(']'))
        {
            int end = raw.IndexOf(']');
            string meta = raw.Substring(1, end - 1);
            notes = raw[(end + 1)..].Trim();
            foreach (string part in meta.Split('|'))
            {
                var keyValue = part.Split(':', 2);
                if (keyValue.Length != 2) continue;
                string key = keyValue[0].Trim(), value = keyValue[1].Trim();
                if (key.Contains("شاحنة") || key.Contains("لوحة")) vehicle = value;
                else if (key.Contains("سائق")) driver = value;
                else if (key.Contains("حاوية") || key.Contains("بوليصة")) container = value;
            }
        }
        return (vehicle, driver, container, notes);
    }
}
