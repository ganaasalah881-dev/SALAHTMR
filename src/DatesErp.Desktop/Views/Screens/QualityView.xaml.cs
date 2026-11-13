using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>لون حالة المطابقة للمعايير.</summary>
public class MatchStatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && s.Contains("غير مطابق") ? Brushes.Red : Brushes.Green;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

/// <summary>قاعدة صفوف قابلة للتحرير: تغيير الحقل ينعكس فوراً على المشتقات.</summary>
public abstract class EditableRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;
    protected void Raise([CallerMemberName] string name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(name); return true;
    }
}

public partial class QualityView : UserControl
{
    /// <summary>
    /// §v1.50.29: صف المصفوفة — بنفس شكل خطة الإنتاج: عميل × صنف × وزن × عبوة × المستلم للفحص،
    /// ثم عمود لكل صفة جودة (GradeQtys[typeId]) وعمود الإجمالي المحسوب. الصفات ليست أصنافاً.
    /// </summary>
    private class ItemRowUi : EditableRow
    {
        public int ProductId { get; set; }
        public int? OrderItemId { get; set; }
        public int? LotId { get; set; }
        public string LotCode { get; set; } = "";
        public string CustomerName { get; set; } = "";
        public string ProductName { get; set; } = "";
        public string CartonWeight { get; set; } = "";
        public string PackageName { get; set; } = "";
        public double ReceivedCartons { get; set; }
        public List<AllowedResultType> Grades { get; set; } = new();
        public Dictionary<int, double> GradeQtys { get; } = new();
        public double Total => GradeQtys.Values.Sum();
        public bool IsInvalid => !double.IsFinite(ReceivedCartons) || !double.IsFinite(Total)
            || Math.Abs(Total - ReceivedCartons) > 0.001;
        public string ValidationError => IsInvalid
            ? $"مجموع الصفات {Total:N0} لا يساوي المستلم {ReceivedCartons:N0} أو يحتوي قيمة غير صالحة"
            : null;
        /// <summary>بعد تحرير خلية صفة: يُحدّث الإجمالي.</summary>
        public void CellEdited() { Raise("Item[]"); Raise(nameof(Total)); Raise(nameof(IsInvalid)); Raise(nameof(ValidationError)); }
    }

    /// <summary>صف معيار معتمد بقيمته المسجلة وحالة المطابقة.</summary>
    private class StandardUi : EditableRow
    {
        public int StandardId { get; set; }
        public string Key { get; set; }
        public string Name { get; set; }
        public string UnitLabel { get; set; } = "%";
        public double Min { get; set; } = double.MinValue;
        public double Max { get; set; } = double.MaxValue;
        public string MinText => Min == double.MinValue ? "—" : Min.ToString("0.##");
        public string MaxText => Max == double.MaxValue ? "—" : Max.ToString("0.##");
        private double _value;
        public double Value
        {
            get => _value;
            set
            {
                if (!Set(ref _value, value)) return;
                Raise(nameof(IsInvalid));
                Raise(nameof(ValidationError));
                Raise(nameof(StatusAr));
            }
        }
        public bool IsInvalid => !double.IsFinite(Value) || Value < Min || Value > Max;
        public string ValidationError => IsInvalid ? $"القيمة {Value} خارج [{MinText} - {MaxText}] أو غير صالحة" : null;
        public string StatusAr => !IsInvalid ? "مطابق ✓" : "خارج الحدود ✗";
    }

    private List<QualitySourceDto> _sources = new();
    private QualitySourceDto _current;
    private readonly ObservableCollection<ItemRowUi> _rows = new();
    private readonly ObservableCollection<StandardUi> _standards = new();
    private List<AllowedResultType> _gradeColumns = new();
    private bool _loading;
    private Views.ErpToolbar _toolbar;
    private bool _newMode;
    private bool _editMode;
    private int _loadGeneration;
    private static bool QualityCan(string action)
        => DatesErp.Desktop.Views.PermissionGate.Can("quality", action);

    public QualityView() { InitializeComponent(); ResultsGrid.ItemsSource = _rows; CriteriaGrid.ItemsSource = _standards; Loaded += (_, _) => Load(); }
    public void AttachChrome(Views.ErpChrome chrome)
    {
        chrome.SetModule("فحص وتأكيد جودة التمور — المواصفة القياسية");
        chrome.SetScreenCode("MRPQC1002");
        var toolbar = new Views.ErpToolbar()
            .ForModule("quality")
            .WithNew((_, _) => StartNew_Click(this, new RoutedEventArgs()), "➕ إضافة فحص")
            .WithSearch((_, _) => SearchSources_Click(this, new RoutedEventArgs()),
                "اكتب رقم التسليم/المحضر ثم اضغط بحث لتنفيذ البحث")
            .WithEdit((_, _) => Edit_Click(this, new RoutedEventArgs()))
            .WithDelete((_, _) => Delete_Click(this, new RoutedEventArgs()))
            .WithSaveForCreateOrEdit((_, _) => Save_Click(this, new RoutedEventArgs()))
            .WithUndo((_, _) => Undo_Click(this, new RoutedEventArgs()))
            .WithApprove((_, _) => Approve_Click(this, new RoutedEventArgs()))
            .WithPrint((_, _) => Print_Click(this, new RoutedEventArgs()), "طباعة محضر الفحص المحفوظ")
            .WithRefresh((_, _) => Load(_current?.OrderId, _current?.CheckId))
            .WithExit((_, _) => (Window.GetWindow(this) as MainWindow)?.OpenPreviousScreen());
        _toolbar = toolbar;
        chrome.SetPermissionModule("quality");
        chrome.SetToolbar(toolbar);
        chrome.SetBody(this);
        chrome.CloseRequested += (_, _) => (Window.GetWindow(this) as MainWindow)?.OpenPreviousScreen();
    }
    private T WithInsp<T>(Func<IInspectionService, T> action)
    {
        using var scope = AppContainer.NewScope();
        return action(scope.ServiceProvider.GetRequiredService<IInspectionService>());
    }

    /// <summary>تحميل المصادر دون اختيار تلقائي؛ تظهر المصادر غير المفحوصة في وضع الإضافة فقط.</summary>
    private async void Load(int? keepOrderId = null, int? keepCheckId = null, string statusAfterLoad = null)
    {
        int generation = System.Threading.Interlocked.Increment(ref _loadGeneration);
        _loading = true;
        SourceBox.IsEnabled = false;
        try
        {
            ErrorLog.WriteInfo($"Quality.Load STEP=ENTER KeepOrderId={keepOrderId?.ToString() ?? "<null>"}");
            var sources = await Task.Run(() => WithInsp(s => s.GetDeliverySources(true)));
            if (generation != _loadGeneration) return;

            _sources = sources ?? new List<QualitySourceDto>();
            ErrorLog.WriteInfo($"Quality.Load STEP=RETURN_SOURCES Count={_sources.Count}");
            SourceBox.DisplayMemberPath = nameof(QualitySourceDto.Label);
            int? pendingCheckId = DatesErp.Desktop.Views.MainWindow.PendingCheckIdToOpen;
            DatesErp.Desktop.Views.MainWindow.PendingCheckIdToOpen = null;
            var target = _sources.FirstOrDefault(s2 => pendingCheckId.HasValue && s2.CheckId == pendingCheckId.Value)
                ?? _sources.FirstOrDefault(s2 => keepCheckId.HasValue && s2.CheckId == keepCheckId.Value)
                ?? _sources.FirstOrDefault(s2 => keepOrderId.HasValue && s2.OrderId == keepOrderId.Value);

            if (target != null)
            {
                _newMode = false;
                SourceBox.ItemsSource = _sources;
                SourceBox.SelectedItem = target;
            }
            else if (_newMode)
            {
                SourceBox.ItemsSource = _sources.Where(s2 => !s2.HasInspectionResults && !s2.CheckApproved).ToList();
                SourceBox.SelectedIndex = -1;
            }
            else
            {
                SourceBox.ItemsSource = _sources;
                SourceBox.SelectedIndex = _sources.Count > 0 ? 0 : -1;
            }

            _loading = false;
            Source_Changed(SourceBox, new SelectionChangedEventArgs(ComboBox.SelectionChangedEvent,
                new List<object>(), SourceBox.SelectedItem is null ? new List<object>() : new List<object> { SourceBox.SelectedItem }));
            if (!string.IsNullOrWhiteSpace(statusAfterLoad)) StatusLabel.Text = statusAfterLoad;
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration) return;
            _loading = false;
            WriteQualityExceptionTrace("Quality.Load", ex);
            DisableQualityActions();
            ItemsCountChip.Text = "تعذر تحديث البنود";
            StatusLabel.Text = "تعذر تحميل مصادر الفحص حالياً. تم تسجيل السبب في سجل النظام؛ اضغط «تحديث» بعد التحقق من الاتصال والصلاحيات.";
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                _loading = false;
                SourceBox.IsEnabled = true;
            }
        }
    }

    private void SetToolbarActionEnabled(Button button, bool enabled)
    {
        if (button != null) button.IsEnabled = enabled && !(_toolbar?.IsDenied(button) ?? false);
    }

    private void SetSaveEnabled(bool enabled) => SetToolbarActionEnabled(_toolbar?.SaveBtn, enabled);
    private void SetApproveEnabled(bool enabled) => SetToolbarActionEnabled(_toolbar?.ApproveBtn, enabled);
    private void SetPrintEnabled(bool enabled) => SetToolbarActionEnabled(_toolbar?.PrintBtn, enabled);

    private void DisableQualityActions()
    {
        SetSaveEnabled(false);
        SetApproveEnabled(false);
        SetPrintEnabled(false);
        ResultsGrid.IsReadOnly = true;
        CriteriaGrid.IsReadOnly = true;
        DecisionPassed.IsEnabled = false;
        DecisionQuarantine.IsEnabled = false;
        DecisionRejected.IsEnabled = false;
    }

    private void StartNew_Click(object sender, RoutedEventArgs e)
    {
        _newMode = true;
        _editMode = false;
        SourceSearchBox.Clear();
        StatusLabel.Text = "جارٍ تحميل تسليمات الإنتاج التي لم يسبق فحصها…";
        Load();
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        _newMode = false;
        _editMode = false;
        SourceSearchBox.Clear();
        Load();
    }

    private void SearchSources_Click(object sender, RoutedEventArgs e)
    {
        var term = ScreenSearch.Normalize(SourceSearchBox.Text ?? "");
        if (term.Length == 0)
        {
            OpenQualitySearch(null);
            return;
        }
        var pool = _sources?.Count > 0 ? _sources : (WithInsp(s => s.GetDeliverySources(true)) ?? new List<QualitySourceDto>());
        var matches = pool.Where(src =>
            ScreenSearch.Normalize(src.Label).Contains(term)
            || ScreenSearch.Normalize(src.OrderNumber).Contains(term)
            || ScreenSearch.Normalize(src.CheckNumber).Contains(term)
            || ScreenSearch.Normalize(src.OrderId.ToString()).Contains(term)
            || (src.Items ?? new List<QualitySourceItemDto>()).Any(item =>
                ScreenSearch.Normalize(item.ProductName).Contains(term)
                || ScreenSearch.Normalize(item.CustomerName).Contains(term))).ToList();
        if (matches.Count == 0)
        {
            OpenQualitySearch(SourceSearchBox.Text);
        }
        else if (matches.Count == 1)
        {
            _newMode = false;
            _editMode = false;
            SourceBox.ItemsSource = pool;
            SourceBox.SelectedItem = matches[0];
            Source_Changed(SourceBox, new SelectionChangedEventArgs(ComboBox.SelectionChangedEvent,
                new List<object>(), new List<object> { matches[0] }));
        }
        else
        {
            OpenQualitySearch(SourceSearchBox.Text);
        }
    }

    private void OpenQualitySearch(string initialTerm = null)
    {
        try
        {
            List<QualitySourceDto> matches = new();
            var win = new DocSearchWindow("مصادر ومحاضر الجودة",
                new List<SearchFieldDef> { new() { Key = "term", LabelAr = "رقم الأمر أو العميل أو الصنف أو محضر الفحص", DefaultValue = initialTerm } }, filters =>
                {
                    _sources = WithInsp(s => s.GetDeliverySources(true)) ?? new List<QualitySourceDto>();
                    var term = ScreenSearch.Normalize(filters.GetValueOrDefault("term") ?? "");
                    matches = _sources.Where(src => string.IsNullOrEmpty(term)
                        || ScreenSearch.Normalize(src.Label).Contains(term)
                        || ScreenSearch.Normalize(src.OrderNumber).Contains(term)
                        || ScreenSearch.Normalize(src.CheckNumber).Contains(term)
                        || ScreenSearch.Normalize(src.OrderId.ToString()).Contains(term)
                        || (src.Items ?? new List<QualitySourceItemDto>()).Any(item =>
                            ScreenSearch.Normalize(item.ProductName).Contains(term)
                            || ScreenSearch.Normalize(item.CustomerName).Contains(term)))
                        .ToList();
                    return new SearchResult
                    {
                        Columns = new List<string> { "رقم أمر الإنتاج", "المصدر / المحضر", "رقم محضر الفحص", "حالة الفحص", "الاعتماد", "عدد البنود", "الكمية (كرتون)" },
                        Rows = matches.Select((src, index) => (index + 1, new object[]
                        {
                            src.OrderNumber ?? "—", src.Label, src.CheckNumber ?? "—", QualityCheckStatuses.ToArabic(src.CheckStatus),
                            src.CheckApproved ? "معتمد" : src.HasInspectionResults ? "محفوظ" : "غير مفحوص",
                            src.Items?.Count ?? 0, src.TotalProducedCartons
                        })).ToList()
                    };
                }) { Owner = Window.GetWindow(this) };
            if (win.ShowDialog() == true && win.SelectedId is int index && index > 0 && index <= matches.Count)
            {
                _newMode = false;
                _editMode = false;
                SourceBox.DisplayMemberPath = nameof(QualitySourceDto.Label);
                SourceBox.ItemsSource = _sources;
                var selected = matches[index - 1];
                SourceBox.SelectedItem = selected;
                Source_Changed(SourceBox, new SelectionChangedEventArgs(ComboBox.SelectionChangedEvent,
                    new List<object>(), new List<object> { selected }));
                SourceSearchBox.Text = initialTerm ?? "";
            }
            else if (matches.Count == 0)
            {
                StatusLabel.Text = "لا توجد نتائج مطابقة في مصادر الجودة أو محاضرها المحفوظة.";
            }
        }
        catch (Exception ex)
        {
            WriteQualityExceptionTrace("Quality.Search", ex);
            StatusLabel.Text = "تعذر البحث في مصادر الجودة ومحاضرها؛ تحقق من الاتصال والصلاحيات ثم أعد المحاولة.";
            AppContainer.Get<DialogService>().HandleException(ex, "Quality.Search");
        }
    }

    private void SourceSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SearchSources_Click(sender, new RoutedEventArgs());
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_current?.HasInspectionResults != true)
        {
            StatusLabel.Text = "هذا المصدر لم يُسجّل له فحص بعد؛ أدخل النتائج بصلاحية quality/Create.";
            return;
        }
        if (_current.CheckApproved)
        {
            StatusLabel.Text = "الفحص معتمد؛ لا يُعدّل مباشرة. استخدم مسار التصحيح المعتمد مع سبب موثق.";
            return;
        }
        if (_current.Items.Any(item => item.AllowedGrades.Any(g =>
                string.Equals(g.Code, "RT-OK", StringComparison.OrdinalIgnoreCase)
                || string.Equals(g.NameAr?.Trim(), "خرج تام مطابق", StringComparison.Ordinal))))
        {
            StatusLabel.Text = "هذا محضر تاريخي يتضمن «خرج تام مطابق»؛ يُعرض ويُطبع كما حُفظ، ولا يُحرر لتجنب تغيير دلالته. يمكن إلغاؤه بسبب موثق ثم إنشاء فحص صحيح إذا لم يرتبط باستلام.";
            return;
        }
        if (!QualityCan("Edit"))
        {
            StatusLabel.Text = "التعديل غير متاح: لا توجد صلاحية quality/Edit لهذا المستخدم.";
            return;
        }
        _editMode = true;
        ApplyEditability();
        StatusLabel.Text = $"تم فتح محضر الفحص {_current.CheckNumber} للتعديل. احفظ التغييرات بعد مراجعة الكميات.";
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_current?.CheckId == null)
        {
            StatusLabel.Text = "اختر محضر فحص محفوظاً من البحث قبل الحذف.";
            return;
        }
        if (_current.CheckApproved)
        {
            StatusLabel.Text = "لا يمكن حذف فحص معتمد؛ استخدم إجراء التصحيح المعتمد مع سبب مسجل.";
            return;
        }
        if (!QualityCan("Delete"))
        {
            StatusLabel.Text = "الحذف غير متاح: لا توجد صلاحية quality/Delete لهذا المستخدم.";
            return;
        }
        string reason = PromptCancellationReason();
        if (reason == null) return;
        if (!AppContainer.Get<DialogService>().ConfirmImpact(
                $"سيُلغى محضر الفحص {_current.CheckNumber} دون حذف سجله التدقيقي. متابعة؟",
                "لا حذف فعلي للسجل أو النتائج.",
                "يُمنع الإجراء إذا كان الفحص معتمداً أو مرتبطاً باستلام مخزني.",
                "بعد الإلغاء يعود أمر التسليم إلى قائمة إضافة الفحوص.")) return;
        var result = WithInsp(service => service.CancelDeliveryCheck(_current.CheckId.Value, reason));
        if (result.Ok)
        {
            _newMode = true;
            _editMode = false;
            Load(statusAfterLoad: "✅ " + result.Message);
        }
        else StatusLabel.Text = "❌ " + result.Message;
    }

    private string PromptCancellationReason()
    {
        var input = new TextBox { MinWidth = 440, MinHeight = 64, TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 12) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        var ok = new Button { Content = "تأكيد السبب", MinWidth = 110, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "رجوع", MinWidth = 90, IsCancel = true };
        var panel = new StackPanel { Margin = new Thickness(18), FlowDirection = FlowDirection.RightToLeft };
        panel.Children.Add(new TextBlock { Text = "سبب حذف/إلغاء محضر الفحص (مطلوب للتدقيق):", FontWeight = FontWeights.Bold });
        panel.Children.Add(input);
        buttons.Children.Add(ok); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        var dialog = new Window { Title = "سبب إلغاء فحص الجودة", Width = 520, Height = 235,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this), Content = panel, FlowDirection = FlowDirection.RightToLeft };
        string answer = null;
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(input.Text))
            {
                AppContainer.Get<DialogService>().Info("اكتب سبب الإلغاء قبل المتابعة.");
                input.Focus();
                return;
            }
            answer = input.Text.Trim();
            dialog.DialogResult = true;
        };
        dialog.ShowDialog();
        return answer;
    }

    private void ClearCurrentDetails()
    {
        _current = null;
        _editMode = false;
        _rows.Clear(); _standards.Clear();
        ItemsCountChip.Text = "0 بند محمّل";
        CheckNoChip.Text = "— لا فحص محفوظ بعد";
        ItemsPanel.Children.Clear();
        ItemsPanel.Children.Add(new TextBlock { Text = "الأصناف المحمّلة للفحص من المصدر المحدد:",
            FontWeight = FontWeights.Bold, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0) });
        BuildGradeColumns();
        DisableQualityActions();
    }

    private void ApplyEditability()
    {
        bool editable = _current != null && !_current.CheckApproved && _editMode
            && (_current.HasInspectionResults ? QualityCan("Edit") : QualityCan("Create"));
        SetSaveEnabled(editable && _rows.Count > 0 && _gradeColumns.Count > 0);
        ResultsGrid.IsReadOnly = !editable;
        CriteriaGrid.IsReadOnly = !editable;
        DecisionPassed.IsEnabled = editable;
        DecisionQuarantine.IsEnabled = editable;
        DecisionRejected.IsEnabled = editable;
    }

    private void Source_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsGrid == null || _loading) return;
        _current = SourceBox.SelectedItem as QualitySourceDto;
        _editMode = _current != null && !_current.HasInspectionResults && !_current.CheckApproved;
        _rows.Clear(); _standards.Clear();
        ItemsCountChip.Text = _current == null ? "0 بند محمّل" : "جارٍ تحميل الأصناف…";
        GradeInputsHint.Text = "بعد اختيار التسليم تظهر البنود تلقائياً؛ أدخل التصنيفات دون تكرار الكمية.";
        GradeInputsHint.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#14532D"));
        // إعادة تحميل المصدر يجب ألا تضاعف شرائح الأصناف في كل تغيير أو تحديث.
        ItemsPanel.Children.Clear();
        ItemsPanel.Children.Add(new TextBlock
        {
            Text = "الأصناف المحمّلة للفحص من المصدر المحدد:", FontWeight = FontWeights.Bold, FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0)
        });
        ErrorLog.WriteInfo($"Quality.Source_Changed STEP=SELECT OrderId={_current?.OrderId.ToString() ?? "<null>"} CheckId={_current?.CheckId?.ToString() ?? "<null>"}");
        CheckNoChip.Text = _current?.HasInspectionResults == true
            ? $"🧾 فحص {_current.CheckNumber} — {QualityCheckStatuses.ToArabic(_current.CheckStatus)}{(_current.CheckApproved ? " (معتمد — للقراءة)" : "")}"
            : _current?.CheckId != null
                ? $"🧾 محضر تمهيدي {_current.CheckNumber} — بانتظار إدخال الفحص"
                : "— لا فحص محفوظ بعد";
        bool canChange = _current?.HasInspectionResults == true ? QualityCan("Edit") : QualityCan("Create");
        bool canApprove = QualityCan("Approve");
        bool canPrint = QualityCan("Print");
        if (_current == null || _current.CheckApproved)
        {
            GradeInputsHint.Text = _current == null
                ? "اضغط إضافة لاختيار تسليم لم يُفحص، أو ابحث عن محضر محفوظ."
                : "الفحص معتمد؛ النتائج معروضة للقراءة والطباعة فقط.";
            GradeInputsHint.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#536970"));
        }
        else if (!canChange || (_current.HasInspectionResults && !_editMode))
        {
            GradeInputsHint.Text = _current.HasInspectionResults && !_editMode
                ? "المحضر للقراءة فقط؛ اضغط تعديل ولديك صلاحية quality/Edit لفتح الحقول."
                : $"تم تحميل الأصناف، لكن لا تتوفر صلاحية {(_current.HasInspectionResults ? "quality/Edit" : "quality/Create")}.";
            GradeInputsHint.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B91C1C"));
        }
        else
        {
            GradeInputsHint.Text = "أدخل الكمية ضمن صفات الجودة الفعلية؛ تحقق أن مجموع التصنيفات يساوي المستلم للفحص.";
            GradeInputsHint.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#14532D"));
        }
        bool canEditNow = _current != null && !_current.CheckApproved && _editMode && canChange;
        SetSaveEnabled(canEditNow);
        bool hasHistoricalAggregate = _current?.Items.Any(item => item.AllowedGrades.Any(g =>
            string.Equals(g.Code, "RT-OK", StringComparison.OrdinalIgnoreCase)
            || string.Equals(g.NameAr?.Trim(), "خرج تام مطابق", StringComparison.Ordinal))) == true;
        SetApproveEnabled(_current?.HasInspectionResults == true && !_current.CheckApproved && canApprove && !hasHistoricalAggregate);
        SetPrintEnabled(_current?.HasInspectionResults == true && canPrint);
        ResultsGrid.IsReadOnly = !canEditNow;
        CriteriaGrid.IsReadOnly = !canEditNow;
        DecisionPassed.IsEnabled = canEditNow;
        DecisionQuarantine.IsEnabled = canEditNow;
        DecisionRejected.IsEnabled = canEditNow;
        StatusLabel.Text = _current == null
            ? (_sources.Count == 0
                ? "لا توجد تسليمات إنتاج مكتملة بعد؛ بعد إقفال الإنتاج سيظهر أمر التسليم هنا."
                : _newMode
                    ? "اختر تسليماً غير مفحوص؛ إذا لم تظهر مصادر فقد فُحصت كلها مسبقاً."
                    : "الشاشة فارغة. اضغط إضافة لفحص جديد أو بحث لاستعراض محضر محفوظ.")
            : $"مصدر الفحص: تسليم الإنتاج رقم {_current.OrderNumber} — إجمالي المنتَج {_current.TotalProducedCartons:N0} كرتون"
              + (_current.CheckApproved ? " — الفحص معتمد، للقراءة والطباعة."
                  : _current.HasInspectionResults && !_editMode ? " — للقراءة؛ اضغط تعديل لفتح الإدخال."
                  : " — البنود جاهزة للإدخال.");
        if (_current != null)
        {
            if (hasHistoricalAggregate && !_current.CheckApproved)
                StatusLabel.Text += " — بيانات تاريخية تحتوي إجمالي «خرج تام مطابق»: عرض وطباعة فقط، ولا اعتماد مباشر.";
            if (!canChange && !_current.CheckApproved)
                StatusLabel.Text += $" — الحفظ غير متاح: تنقص صلاحية {(_current.HasInspectionResults ? "quality/Edit" : "quality/Create")}.";
            if (!canApprove && !_current.CheckApproved)
                StatusLabel.Text += " — الاعتماد غير متاح: لا توجد صلاحية اعتماد الجودة لهذا المستخدم.";
            if (!canPrint)
                StatusLabel.Text += " — الطباعة غير متاحة: لا توجد صلاحية الطباعة لهذا المستخدم.";
        }
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            if (_current?.CheckId != null)
            {
                try
                {
                    var savedDecision = db.QualityChecks.AsNoTracking()
                        .Where(c => c.Id == _current.CheckId)
                        .Select(c => c.Decision)
                        .FirstOrDefault();
                    DecisionQuarantine.IsChecked = savedDecision == "Quarantine";
                    DecisionRejected.IsChecked = savedDecision == "Rejected";
                    DecisionPassed.IsChecked = savedDecision is not ("Quarantine" or "Rejected");
                }
                catch
                {
                    DecisionPassed.IsChecked = true;
                }
            }
            else
            {
                DecisionPassed.IsChecked = true;
                DecisionQuarantine.IsChecked = false;
                DecisionRejected.IsChecked = false;
            }
            List<InspectionResult> savedResults;
            try
            {
                savedResults = _current?.CheckId != null
                    ? db.InspectionResults.AsNoTracking().Where(r2 => r2.CheckId == _current.CheckId).ToList()
                    : new List<InspectionResult>();
            }
            catch
            {
                // قاعدة قديمة بلا جدول نتائج: المصدر ما زال قابلاً للعرض، وستظهر
                // الكمية كقيمة افتراضية غير محفوظة إلى أن يكتمل الترحيل.
                savedResults = new List<InspectionResult>();
            }
            foreach (var item in _current?.Items ?? new List<QualitySourceItemDto>())
            {
                // §التدفق المعتمد: الأصناف المنتَجة كلها تنزل بكمياتها — بلا إعادة اختيار الصنف،
                // وكل عميل بصفه كما في خطة الإنتاج (عميل × صنف × وزن × عبوة).
                var chip = new Border { Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#EDF3EE")),
                    BorderBrush = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#CADCCF")),
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(11), Padding = new Thickness(9,3,9,3), Margin = new Thickness(0,0,8,0) };
                chip.Child = new TextBlock { FontWeight = FontWeights.Bold, FontSize = 12, Foreground = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#14532D")),
                    Text = $"{item.CustomerName ?? "غير محدد"} — {item.ProductName} — {item.ProducedCartons:N0}" };
                ItemsPanel.Children.Add(chip);
                var row = new ItemRowUi { ProductId = item.ProductId, OrderItemId = item.OrderItemId, LotId = item.LotId,
                    CustomerName = item.CustomerName ?? "غير محدد", ProductName = item.ProductName, LotCode = item.LotCode ?? "—",
                    CartonWeight = item.CartonWeightKg > 0 ? item.CartonWeightKg.ToString("0.##") : "—",
                    PackageName = string.IsNullOrWhiteSpace(item.PackageName) ? "—" : item.PackageName,
                    ReceivedCartons = item.ProducedCartons, Grades = item.AllowedGrades };
                var mine = savedResults.Where(r2 => r2.ProductId == item.ProductId && (item.LotId == null || r2.LotId == item.LotId)).ToList();
                if (mine.Count > 0)
                    foreach (var r2 in mine)
                        row.GradeQtys[r2.ResultTypeId] = (double)r2.Qty;
                else if (item.AllowedGrades.FirstOrDefault(g => g.ResultKind == InspectionResultType.KindAccepted) is { } acceptedGrade)
                    row.GradeQtys[acceptedGrade.ResultTypeId] = item.ProducedCartons;
                _rows.Add(row);
            }
            // المعايير المعتمدة: تظهر تلقائياً بقيمها المحفوظة أو الافتراضية
            Dictionary<int, double> savedStd;
            try
            {
                savedStd = _current?.CheckId != null
                    ? db.QualityStandardRecords.AsNoTracking().Where(r2 => r2.CheckId == _current.CheckId).ToDictionary(r2 => r2.StandardId, r2 => r2.Value)
                    : new Dictionary<int, double>();
            }
            catch { savedStd = new Dictionary<int, double>(); }
            try
            {
                foreach (var st in db.QualityStandards.AsNoTracking().Where(s2 => s2.IsActive).OrderBy(s2 => s2.SortNo))
                    _standards.Add(new StandardUi { StandardId = st.Id, Key = st.Code, Name = st.NameAr, UnitLabel = st.UnitLabel,
                        Min = st.MinValue ?? double.MinValue, Max = st.MaxValue ?? double.MaxValue,
                        Value = savedStd.TryGetValue(st.Id, out var v) ? v : st.DefaultValue });
            }
            catch
            {
                // المعايير الإضافية لا تمنع استعراض مصدر الفحص في قاعدة قديمة.
            }
            BuildGradeColumns();
            Recalc();
            ItemsCountChip.Text = $"{_rows.Count:N0} بند محمّل";
            var itemsWithoutGrades = _rows.Where(r2 => r2.Grades.Count == 0).ToList();
            if (_rows.Count == 0)
            {
                SetSaveEnabled(false);
                GradeInputsHint.Text = _current == null
                    ? "اختر تسليماً غير مفحوص أو محضراً محفوظاً."
                    : "لا توجد أصناف بكمية فعلية في المصدر المحدد؛ لا يمكن إدخال نتائج قبل ظهور بند فحص.";
                GradeInputsHint.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_current == null ? "#536970" : "#B91C1C"));
                if (_current != null)
                    StatusLabel.Text += " — لا توجد بنود فعلية لهذا المصدر؛ تحقق من تسجيل وإقفال إنتاج الأصناف أولاً.";
            }
            else if (itemsWithoutGrades.Count > 0 || _gradeColumns.Count == 0)
            {
                SetSaveEnabled(false);
                GradeInputsHint.Text = "لا تظهر حقول نتائج قابلة للتحرير: لا توجد أنواع نتائج (مقبول/مرفوض) فعّالة ومربوطة بهذه الأصناف. راجع إعدادات الجودة ثم حدّث الشاشة.";
                GradeInputsHint.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B91C1C"));
                StatusLabel.Text += $" — ⛔ لا توجد صفات جودة (مقبول/مرفوض) معرفة لـ {itemsWithoutGrades.Count} بنداً؛ عرّفها أولاً من إعدادات الجودة.";
            }
            // §1.50.72 P4-3: تمييز القيم المعبأة مسبقاً (كل المنتَج «مقبول») حتى لا تُحفظ دون تدقيق
            else if (_current != null && !_current.HasInspectionResults && _rows.Count > 0)
                StatusLabel.Text += " — ⚠ القيم المبدئية تضع كامل الكمية في أول تصنيف مقبول؛ وزّعها على «تمر سليم/منسم» قبل الحفظ.";
        }
        catch (Exception ex)
        {
            WriteQualityExceptionTrace("Quality.Source", ex);
            DisableQualityActions();
            StatusLabel.Text = "تعذر تحميل تفاصيل مصدر الفحص حالياً. تم تسجيل السبب؛ حدّث الشاشة بعد التحقق من إعدادات الجودة.";
        }
    }

    /// <summary>§v1.50.29: أعمدة الشبكة = الثابتة (عميل/صنف/وزن/عبوة/المستلم/الدفعة) + عمود لكل صفة + الإجمالي.</summary>
    private void BuildGradeColumns()
    {
        _gradeColumns = _rows.SelectMany(r2 => r2.Grades).GroupBy(g => g.ResultTypeId).Select(g => g.First())
            .OrderByDescending(g => g.ResultKind == InspectionResultType.KindAccepted).ThenBy(g => g.ResultTypeId).ToList();
        ResultsGrid.Columns.Clear();

        var wrapStyle = new Style(typeof(TextBlock));
        wrapStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        wrapStyle.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(6, 4, 6, 4)));
        wrapStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        wrapStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, 12.5));

        var boldCenterStyle = new Style(typeof(TextBlock));
        boldCenterStyle.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center));
        boldCenterStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
        boldCenterStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        boldCenterStyle.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(6, 4, 6, 4)));
        boldCenterStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, 12.5));

        void AddCol(string header, string path, double width, Style style = null, bool ro = true, string format = null)
        {
            var binding = new Binding(path);
            if (!string.IsNullOrWhiteSpace(format)) binding.StringFormat = format;
            var col = new DataGridTextColumn { Header = header, Binding = binding, IsReadOnly = ro, Width = width };
            if (style != null) col.ElementStyle = style;
            ResultsGrid.Columns.Add(col);
        }

        AddCol("العميل", nameof(ItemRowUi.CustomerName), 220, wrapStyle);
        AddCol("الصنف", nameof(ItemRowUi.ProductName), 240, wrapStyle);
        AddCol("وزن الكرتون", nameof(ItemRowUi.CartonWeight), 100, boldCenterStyle);
        AddCol("العبوة", nameof(ItemRowUi.PackageName), 120, wrapStyle);
        AddCol("المستلم للفحص", nameof(ItemRowUi.ReceivedCartons), 120, boldCenterStyle, format: "{0:N0}");
        foreach (var g in _gradeColumns)
        {
            var col = new DataGridTextColumn
            {
                Header = g.NameAr,
                Binding = new Binding($"GradeQtys[{g.ResultTypeId}]") { UpdateSourceTrigger = PropertyChangedTrigger, Mode = BindingModeTwoWay },
                Width = 115,
                IsReadOnly = string.Equals(g.Code, "RT-OK", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(g.NameAr?.Trim(), "خرج تام مطابق", StringComparison.Ordinal),
                ElementStyle = boldCenterStyle
            };
            ResultsGrid.Columns.Add(col);
        }
        AddCol("الدفعة", nameof(ItemRowUi.LotCode), 120, wrapStyle);
        AddCol("الإجمالي", nameof(ItemRowUi.Total), 110, boldCenterStyle, format: "{0:N0}");
    }
    private const System.Windows.Data.UpdateSourceTrigger PropertyChangedTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged;
    private const System.Windows.Data.BindingMode BindingModeTwoWay = System.Windows.Data.BindingMode.TwoWay;

    private void Grid_PreparingCellForEdit(object sender, DataGridPreparingCellForEditEventArgs e)
    {
        if (e.EditingElement is TextBox tb)
        {
            tb.Focus();
            tb.SelectAll();
        }
    }

    /// <summary>بعد تحرير خلية صفة: تحديث الإجماليات ومعادلة الحفظ الحية.</summary>
    private void Results_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit)
        {
            (e.Row.Item as ItemRowUi)?.CellEdited();
            Dispatcher.BeginInvoke(new Action(Recalc));
        }
    }

    /// <summary>معادلة الحفظ الحية لكل صف: مجموع الصفات مقابل الكمية المستلمة، والنسب لكل صفة.</summary>
    private void Recalc()
    {
        foreach (var row in _rows) row.CellEdited();
        bool allOk = _rows.Count > 0 && _rows.All(IsRowBalanced);
        var lines = new List<string>();
        foreach (var row in _rows)
        {
            double sum = row.Total;
            bool ok = double.IsFinite(sum) && double.IsFinite(row.ReceivedCartons)
                && Math.Abs(sum - row.ReceivedCartons) <= 0.001;
            string pcts = string.Join(" · ", _gradeColumns
                .Where(g => row.GradeQtys.TryGetValue(g.ResultTypeId, out var q) && row.ReceivedCartons > 0 && q > 0)
                .Select(g => $"{g.NameAr} {row.GradeQtys[g.ResultTypeId] / row.ReceivedCartons * 100:N2}٪"));
            lines.Add($"{row.CustomerName}/{row.ProductName}: {sum:N0} / {row.ReceivedCartons:N0} {(ok ? "✓" : "✗")}"
                      + (pcts.Length > 0 ? $" ({pcts})" : ""));
        }
        var perGrade = _gradeColumns
            .Select(g => (g.NameAr, Q: _rows.Sum(r2 => r2.GradeQtys.TryGetValue(g.ResultTypeId, out var q) ? q : 0)))
            .Where(x => x.Q > 0).ToList();
        double tot = _rows.Sum(r2 => r2.Total);
        TotalsLabel.Text = "الإجمالي: "
            + (perGrade.Count > 0 ? string.Join(" · ", perGrade.Select(x => $"{x.NameAr} {x.Q:N0}")) + " — " : "")
            + $"{tot:N0} من {_current?.TotalProducedCartons ?? 0:N0}";
        EqLabel.Text = _rows.Count == 0
            ? "معادلة الحفظ: مجموع صفات كل صنف = كميته المستلمة للفحص."
            : (allOk ? "✓ مجموع الصفات يطابق الكمية المستلمة لكل صف — يُسمح بالحفظ.  "
                     : "✗ لا يُسمح بالحفظ — الصفات لا تطابق الكمية المستلمة:  ") + string.Join("  •  ", lines);
        EqLabel.Foreground = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString(allOk ? "#14532D" : "#B91C1C"));
    }
    private static bool IsRowBalanced(ItemRowUi row)
        => double.IsFinite(row.Total) && double.IsFinite(row.ReceivedCartons)
           && Math.Abs(row.Total - row.ReceivedCartons) <= 0.001;

    private void Grid_SingleClickEdit(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || grid.IsReadOnly) return;
        var dep = e.OriginalSource as DependencyObject;
        while (dep is not null and not DataGridCell) dep = VisualTreeHelper.GetParent(dep);
        if (dep is DataGridCell { IsReadOnly: false, IsEditing: false } cell)
        {
            grid.CommitEdit(DataGridEditingUnit.Row, true);
            grid.CurrentCell = new DataGridCellInfo(cell);
            grid.BeginEdit();
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null)
        {
            ErrorLog.WriteInfo("Quality.Save_Click STEP=EXIT Reason=NoSource");
            StatusLabel.Text = "⛔ لا يمكن الحفظ: اختر مصدر فحص من تسليمات الإنتاج أولاً.";
            return;
        }
        if (_current.CheckApproved)
        {
            ErrorLog.WriteInfo($"Quality.Save_Click STEP=EXIT Reason=AlreadyApproved CheckId={_current.CheckId}");
            StatusLabel.Text = "⛔ لا يمكن تعديل فحص معتمد — النتائج للقراءة والطباعة.";
            return;
        }
        if (_current.HasInspectionResults && !_editMode)
        {
            StatusLabel.Text = "⛔ اضغط تعديل أولاً؛ الفحص المحفوظ يفتح للقراءة افتراضياً.";
            return;
        }
        string requiredPermission = _current.HasInspectionResults ? "Edit" : "Create";
        if (!QualityCan(requiredPermission))
        {
            StatusLabel.Text = $"⛔ لا يمكن الحفظ: تنقص صلاحية quality/{requiredPermission}.";
            return;
        }
        if (_current.Items.Any(item => item.AllowedGrades.Any(g =>
                string.Equals(g.Code, "RT-OK", StringComparison.OrdinalIgnoreCase)
                || string.Equals(g.NameAr?.Trim(), "خرج تام مطابق", StringComparison.Ordinal))))
        {
            StatusLabel.Text = "⛔ هذا محضر تاريخي يستخدم «خرج تام مطابق»؛ اعرضه أو اطبعه فقط، أو ألغِه بسبب موثق ثم أنشئ فحصاً جديداً.";
            return;
        }
        try
        {
            ErrorLog.WriteInfo($"Quality.Save_Click STEP=ENTER OrderId={_current.OrderId} CheckId={_current.CheckId?.ToString() ?? "<new>"}");
            ResultsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            ResultsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            CriteriaGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            CriteriaGrid.CommitEdit(DataGridEditingUnit.Row, true);
            ErrorLog.WriteInfo($"Quality.Save_Click STEP=READ_INPUT_ROWS OrderId={_current.OrderId} Rows={_rows.Count} Standards={_standards.Count}");
            if (!_rows.All(IsRowBalanced))
            {
                StatusLabel.Text = "⛔ الحفظ مرفوض: " + EqLabel.Text;
                return;
            }
            if (_standards.Any(s2 => s2.IsInvalid))
            {
                StatusLabel.Text = "⛔ الحفظ مرفوض: توجد قيمة مخبرية خارج حدود المواصفة أو غير صالحة.";
                return;
            }
            // §1.50.77: التحقق من تناسب القرار مع نتائج الفحص:
            // - إذا كانت كل الكمية مرفوضة بلا أي مقبول، لا يصح قرار «مطابق — إفراج».
            // - إذا وجد مقبول ومرفوض معاً (رفض جزئي)، يُسمح بقرار «مطابق — إفراج للمقبول فقط» بعد تأكيد صريح من الفاحص.
            bool hasAccepted = _rows.Any(r2 => r2.GradeQtys.Any(kv =>
                double.IsFinite(kv.Value) && kv.Value > 0
                && r2.Grades.Any(g => g.ResultTypeId == kv.Key && g.ResultKind == InspectionResultType.KindAccepted)));
            bool hasRejected = _rows.Any(r2 => r2.GradeQtys.Any(kv =>
                double.IsFinite(kv.Value) && kv.Value > 0
                && r2.Grades.Any(g => g.ResultTypeId == kv.Key && g.ResultKind == InspectionResultType.KindRejected)));
            if (!hasAccepted && hasRejected && DecisionPassed.IsChecked == true)
            {
                StatusLabel.Text = "⛔ كل الكمية المفحوصة مرفوضة (لا يوجد مقبول للإفراج) — اختر قرار «تحريز مؤقت» أو «مرفوض» قبل الحفظ.";
                return;
            }
            if (hasAccepted && hasRejected && DecisionPassed.IsChecked == true)
            {
                if (!AppContainer.Get<DialogService>().Confirm(
                    "يحتوي الفحص على كراتين مرفوضة جزئياً إلى جانب الكمية المقبولة.\n\n" +
                    "هل تؤكد حفظ القرار «مطابق — إفراج للكمية المقبولة فقط» (مع حجب الكمية المرفوضة تلقائياً عن التسليم)؟"))
                {
                    StatusLabel.Text = "لم يتم الحفظ — اختر القرار المطلوب (مطابق للمقبول / تحريز مؤقت / مرفوض) ثم احفظ.";
                    return;
                }
            }
            ErrorLog.WriteInfo($"Quality.Save_Click STEP=BUILD_DTO OrderId={_current.OrderId}");
            var dto = new QualityDeliveryCheckDto
            {
                OrderId = _current.OrderId,
                CheckDate = DateTime.Now.ToString(DatesErp.Core.Common.UiFormat.DatePattern),
                Decision = DecisionRejected.IsChecked == true ? "Rejected" : DecisionQuarantine.IsChecked == true ? "Quarantine" : "Passed",
                InspectorNotes = null,
                Rows = _rows.SelectMany(r2 => r2.GradeQtys
                        .Where(kv => kv.Key > 0 && double.IsFinite(kv.Value) && kv.Value > 0)
                        .Select(kv => new QualityGradeRowDto { OrderItemId = r2.OrderItemId, ProductId = r2.ProductId, LotId = r2.LotId, ResultTypeId = kv.Key, Cartons = kv.Value }))
                    .ToList(),
                Standards = _standards.Select(s2 => new QualityStandardValueDto { StandardId = s2.StandardId, Value = s2.Value }).ToList(),
            };
            if (dto.Rows.Count == 0) { StatusLabel.Text = "⛔ أدخل كمية صفة واحدة على الأقل."; return; }
            ErrorLog.WriteInfo($"Quality.Save_Click STEP=CALL_SAVE_DELIVERY_CHECK OrderId={dto.OrderId} Rows={dto.Rows.Count}");
            var r = WithInsp(s => s.SaveDeliveryCheck(dto));
            ErrorLog.WriteInfo($"Quality.Save_Click STEP=RETURN_SAVE_DELIVERY_CHECK OrderId={dto.OrderId} Ok={r.Ok} ResultId={r.Id} Message={r.Message}");
            if (r.Ok)
            {
                _newMode = false;
                _editMode = false;
                // أبقِ المحضر الذي حُفظ محدداً بعد إعادة التحميل؛ وإلا يختفي من
                // قائمة «إضافة فحص» ويبقى زر الاعتماد معطلاً بلا مصدر محدد.
                Load(dto.OrderId, r.Id, statusAfterLoad: "✅ " + r.Message);
                AppContainer.Get<DialogService>().Info(r.Message, "تم حفظ فحص الجودة");
            }
            else StatusLabel.Text = "⛔ " + r.Message;
        }
        catch (Exception ex)
        {
            WriteQualityExceptionTrace("Quality.Save", ex);
            StatusLabel.Text = $"Quality.Save: {ex.GetType().FullName}: {ex.Message}";
        }
    }

    private void Approve_Click(object sender, RoutedEventArgs e)
    {
        if (!QualityCan("Approve"))
        {
            StatusLabel.Text = "⛔ لا يمكن الاعتماد: لا توجد صلاحية اعتماد الجودة لهذا المستخدم.";
            return;
        }
        if (_current == null)
        {
            ErrorLog.WriteInfo("Quality.Approve_Click STEP=EXIT Reason=NoSource");
            StatusLabel.Text = "⛔ لا يمكن الاعتماد: اختر مصدر فحص من تسليمات الإنتاج أولاً.";
            return;
        }
        if (!_current.HasInspectionResults || _current.CheckId == null)
        {
            ErrorLog.WriteInfo($"Quality.Approve_Click STEP=EXIT Reason=NoSavedCheck OrderId={_current.OrderId}");
            StatusLabel.Text = "⛔ لا يمكن الاعتماد: احفظ نتائج الفحص أولاً.";
            return;
        }
        if (_current.CheckApproved)
        {
            ErrorLog.WriteInfo($"Quality.Approve_Click STEP=EXIT Reason=AlreadyApproved CheckId={_current.CheckId}");
            StatusLabel.Text = "الفحص معتمد مسبقاً — النتائج للقراءة والطباعة.";
            return;
        }
        try
        {
            ErrorLog.WriteInfo($"Quality.Approve_Click STEP=ENTER OrderId={_current.OrderId} CheckId={_current.CheckId}");
            if (!AppContainer.Get<DialogService>().Confirm($"اعتماد فحص الجودة {_current.CheckNumber}؟ الاعتماد يُقفل النتائج."))
            {
                ErrorLog.WriteInfo($"Quality.Approve_Click STEP=EXIT Reason=UserCancelled CheckId={_current.CheckId}");
                StatusLabel.Text = "لم يتم اعتماد الفحص — أُلغي التأكيد.";
                return;
            }
            using var scope = AppContainer.NewScope();
            var qc = scope.ServiceProvider.GetRequiredService<IQualityService>();
            ErrorLog.WriteInfo($"Quality.Approve_Click STEP=CALL_APPROVE CheckId={_current.CheckId}");
            var r = qc.ApproveCheck(_current.CheckId.Value);
            ErrorLog.WriteInfo($"Quality.Approve_Click STEP=RETURN_APPROVE CheckId={_current.CheckId} Ok={r.Ok} Message={r.Message}");
            if (r.Ok)
            {
                int orderId = _current.OrderId;
                int checkId = _current.CheckId.Value;
                _newMode = false;
                _editMode = false;
                Load(orderId, checkId, statusAfterLoad: "✅ " + r.Message);
                AppContainer.Get<DialogService>().Info(r.Message, "تم اعتماد فحص الجودة");
            }
            else StatusLabel.Text = "⛔ " + r.Message;
        }
        catch (Exception ex)
        {
            WriteQualityExceptionTrace("Quality.Approve", ex);
            StatusLabel.Text = $"Quality.Approve: {ex.GetType().FullName}: {ex.Message}";
        }
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        if (!QualityCan("Print"))
        {
            StatusLabel.Text = "⛔ لا يمكن الطباعة: لا توجد صلاحية طباعة محضر الجودة لهذا المستخدم.";
            return;
        }
        if (_current?.HasInspectionResults != true || _current.CheckId == null)
        {
            ErrorLog.WriteInfo("Quality.Print_Click STEP=EXIT Reason=NoSavedCheck");
            StatusLabel.Text = "الطباعة بعد حفظ الفحص — احفظ النتائج أولاً.";
            return;
        }
        try
        {
            ErrorLog.WriteInfo($"Quality.Print_Click STEP=ENTER CheckId={_current.CheckId}");
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var m = Printing.StoredPrintModels.Quality(db, _current.CheckId.Value);
            if (m == null)
            {
                StatusLabel.Text = "⛔ تعذر الطباعة: نموذج الفحص غير موجود.";
                return;
            }
            new PrintPreviewWindow(PhasePrint.Build(m), $"{m.DocTitle} {m.DocNo}") { Owner = Window.GetWindow(this) }.ShowDialog();
            ErrorLog.WriteInfo($"Quality.Print_Click STEP=SUCCESS CheckId={_current.CheckId}");
            StatusLabel.Text = "✅ تم فتح محضر الفحص للطباعة.";
        }
        catch (Exception ex)
        {
            WriteQualityExceptionTrace("Quality.Print", ex);
            StatusLabel.Text = $"Quality.Print: {ex.GetType().FullName}: {ex.Message}";
        }
    }

    private static void WriteQualityExceptionTrace(string source, Exception ex)
    {
        var parts = new List<string>();
        for (var cur = ex; cur != null; cur = cur.InnerException)
            parts.Add($"{cur.GetType().FullName}: {cur.Message}\n{cur.StackTrace}");
        ErrorLog.WriteInfo($"{source} EXCEPTION\n{string.Join(Environment.NewLine + "--- INNER ---" + Environment.NewLine, parts)}");
        ErrorLog.Write(ex, source);
    }
}
