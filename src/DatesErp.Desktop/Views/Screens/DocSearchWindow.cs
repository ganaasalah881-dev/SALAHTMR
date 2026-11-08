using System.Windows;
using System.Windows.Controls;
using DatesErp.Desktop.Services;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>§7 — حقل في نافذة البحث الموحدة (نص/قائمة/تاريخ).</summary>
public class SearchFieldDef
{
    public string Key { get; set; }
    public string LabelAr { get; set; }
    public string Kind { get; set; } = "text"; // text | combo | date
    public string[] Options { get; set; }
    public string DefaultValue { get; set; }
}

/// <summary>نتيجة البحث الموحدة: أعمدة + صفوف بمعرفاتها.</summary>
public class SearchResult
{
    public List<string> Columns { get; set; } = new();
    public List<(int id, object[] cells)> Rows { get; set; } = new();
}

/// <summary>
/// §7/§8/§9 — نافذة البحث الموحدة لكل شاشات النظام:
/// حقول بحث حسب نوع الشاشة، بحث بدون شروط يعرض كل المستندات المتاحة حسب الصلاحية،
/// النتائج في جدول موحد واضح، ونقرتان متتاليتان على المستند تعيده إلى الواجهة الرئيسية.
/// </summary>
public class DocSearchWindow : Window
{
    /// <summary>معرف المستند الذي اختاره المستخدم بالنقر المزدوج أو زر فتح.</summary>
    public int? SelectedId { get; private set; }

    private readonly List<(SearchFieldDef def, FrameworkElement input)> _fields = new();
    private readonly DataGrid _grid = new() { Height = 380, RowHeight = 32, CanUserSortColumns = true };
    private readonly TextBox _quickSearchBox = new() { Width = 280, ToolTip = "بحث سريع في كل أعمدة النتائج" };
    private readonly TextBlock _state = new() { FontSize = 12, Margin = new Thickness(0, 6, 0, 0), Foreground = System.Windows.Media.Brushes.Gray };
    private readonly Func<Dictionary<string, string>, SearchResult> _search;
    private List<(int id, object[] cells)> _allRows = new();
    private List<(int id, object[] cells)> _rows = new();

    public DocSearchWindow(string title, List<SearchFieldDef> filters, Func<Dictionary<string, string>, SearchResult> search)
    {
        _search = search;
        Title = $"🔍 بحث — {title}";
        Width = 1000; MinWidth = 760; MinHeight = 420; SizeToContent = SizeToContent.Height;
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#F1F5F9");

        var filtersPanel = new WrapPanel();
        foreach (var f in filters)
        {
            FrameworkElement input = f.Kind switch
            {
                "combo" => BuildCombo(f),
                "date" => new DatePicker { Width = 130 },
                _ => new TextBox { Width = 160 }
            };
            if (!string.IsNullOrWhiteSpace(f.DefaultValue))
            {
                if (input is TextBox tb) tb.Text = f.DefaultValue;
                else if (input is ComboBox cb)
                {
                    int option = f.Options?.ToList().FindIndex(x => string.Equals(x, f.DefaultValue, StringComparison.OrdinalIgnoreCase)) ?? -1;
                    if (option >= 0) cb.SelectedIndex = option + 1;
                }
                else if (input is DatePicker dp && DateTime.TryParse(f.DefaultValue, out var date)) dp.SelectedDate = date;
            }
            _fields.Add((f, input));
            var sp = new StackPanel { Margin = new Thickness(0, 0, 14, 6) };
            sp.Children.Add(new TextBlock { Text = f.LabelAr + ":", FontWeight = FontWeights.Bold, FontSize = 11.5, Margin = new Thickness(0, 0, 0, 2) });
            sp.Children.Add(input);
            filtersPanel.Children.Add(sp);
        }

        // §B84/K1: Enter يبحث وEscape يغلق.
        // §B110: حشوات مضمّنة مزالة (الثيم الموحد) + الفتح إجراء رئيسي كحلي + نمط للمحايدين
        var searchBtn = new Button { Content = "🔍 بحث", IsDefault = true };
        searchBtn.Style = (Style)System.Windows.Application.Current.FindResource("ErpPrimaryButton");
        searchBtn.Click += (_, _) => RunSearch();
        var allBtn = new Button { Content = "📋 عرض الكل", Margin = new Thickness(6, 0, 0, 0) };
        allBtn.Style = (Style)System.Windows.Application.Current.FindResource("ErpButton");
        allBtn.Click += (_, _) => { foreach (var (_, inp) in _fields) ClearInput(inp); _quickSearchBox.Clear(); RunSearch(); };
        var refreshBtn = new Button { Content = "🔄 تحديث", Margin = new Thickness(6, 0, 0, 0) };
        refreshBtn.Style = (Style)System.Windows.Application.Current.FindResource("ErpButton");
        refreshBtn.Click += (_, _) => RunSearch();
        var openBtn = new Button { Content = "📂 فتح المحدد", Margin = new Thickness(6, 0, 0, 0) };
        openBtn.Style = (Style)System.Windows.Application.Current.FindResource("ErpPrimaryButton");
        openBtn.Click += (_, _) => OpenSelected();
        var closeBtn = new Button { Content = "✖ إغلاق", Margin = new Thickness(6, 0, 0, 0), IsCancel = true };
        closeBtn.Style = (Style)System.Windows.Application.Current.FindResource("ErpButton");
        closeBtn.Click += (_, _) => Close();

        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var firstBtn = new Button { Content = "الأول", Margin = new Thickness(6, 0, 0, 0) };
        var prevBtn = new Button { Content = "السابق", Margin = new Thickness(3, 0, 0, 0) };
        var nextBtn = new Button { Content = "التالي", Margin = new Thickness(3, 0, 0, 0) };
        var lastBtn = new Button { Content = "الأخير", Margin = new Thickness(3, 0, 0, 0) };
        foreach (var nav in new[] { firstBtn, prevBtn, nextBtn, lastBtn })
            nav.Style = (Style)System.Windows.Application.Current.FindResource("ErpButton");
        firstBtn.Click += (_, _) => Navigate(0);
        prevBtn.Click += (_, _) => Navigate(-1);
        nextBtn.Click += (_, _) => Navigate(1);
        lastBtn.Click += (_, _) => Navigate(int.MaxValue);
        btns.Children.Add(searchBtn); btns.Children.Add(allBtn); btns.Children.Add(refreshBtn);
        btns.Children.Add(openBtn); btns.Children.Add(firstBtn); btns.Children.Add(prevBtn);
        btns.Children.Add(nextBtn); btns.Children.Add(lastBtn); btns.Children.Add(closeBtn);

        _quickSearchBox.TextChanged += (_, _) => ApplyQuickSearch();
        _grid.MouseDoubleClick += (_, _) => OpenSelected();

        var quickPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        quickPanel.Children.Add(new TextBlock { Text = "بحث سريع في النتائج:", FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        quickPanel.Children.Add(_quickSearchBox);

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock
        {
            Text = "أدخل شروط البحث ثم اضغط بحث — أو اضغط عرض الكل لعرض جميع المستندات المتاحة حسب صلاحيتك. نقرتان متتاليتان على أي مستند تفتحانه في الواجهة الرئيسية.",
            FontSize = 11.5, Foreground = System.Windows.Media.Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        });
        panel.Children.Add(filtersPanel);
        panel.Children.Add(btns);
        panel.Children.Add(quickPanel);
        panel.Children.Add(_state);
        panel.Children.Add(_grid);
        Content = panel;

        Loaded += (_, _) => RunSearch(); // §7: بحث بدون شروط = كل المستندات المتاحة
    }

    private static ComboBox BuildCombo(SearchFieldDef f)
    {
        var cb = new ComboBox { Width = 160 };
        cb.Items.Add("— الكل —");
        if (f.Options != null) foreach (var o in f.Options) cb.Items.Add(o);
        cb.SelectedIndex = 0;
        return cb;
    }

    private static void ClearInput(FrameworkElement inp)
    {
        switch (inp)
        {
            case TextBox t: t.Text = ""; break;
            case ComboBox c: c.SelectedIndex = 0; break;
            case DatePicker d: d.SelectedDate = null; break;
        }
    }

    public string CriteriaValue(string key)
    {
        var criteria = Collect();
        return criteria.TryGetValue(key, out var value) ? value : null;
    }

    private Dictionary<string, string> Collect()
    {
        var values = new Dictionary<string, string>();
        foreach (var (def, inp) in _fields)
        {
            values[def.Key] = inp switch
            {
                TextBox t => t.Text?.Trim() ?? "",
                ComboBox { SelectedIndex: > 0 } c => c.SelectedItem?.ToString() ?? "",
                DatePicker d => d.SelectedDate?.ToString(DatesErp.Core.Common.UiFormat.DatePattern) ?? "",
                _ => ""
            };
        }
        return values;
    }

    private void RunSearch()
    {
        try
        {
            var result = _search(Collect());
            _allRows = result.Rows ?? new List<(int id, object[] cells)>();
            ApplyQuickSearch(result.Columns);
        }
        catch (Exception ex)
        {
            _allRows.Clear();
            _rows.Clear();
            _grid.ItemsSource = null;
            _state.Text = "تعذر الاتصال بمصدر البيانات أو تنفيذ البحث. تحقق من الاتصال والصلاحيات ثم أعد المحاولة.";
            AppContainer.Get<DialogService>().HandleException(ex, "DocSearch.Run");
        }
    }

    private void ApplyQuickSearch(List<string> columns = null)
    {
        string term = ScreenSearch.Normalize(_quickSearchBox.Text ?? string.Empty);
        var filtered = string.IsNullOrEmpty(term) ? _allRows : _allRows.Where(row =>
            row.cells.Any(cell => ScreenSearch.Normalize(cell?.ToString()).Contains(term))).ToList();
        // سقف عرض موحد مع إبقاء مجموعة النتائج كاملة للبحث السريع ضمن النتائج المعادة.
        _rows = filtered.Count > 500 ? filtered.Take(500).ToList() : filtered;
        columns ??= (_grid.ItemsSource as System.Data.DataView)?.Table.Columns.Cast<System.Data.DataColumn>()
            .Where(c => c.ColumnName != "__SelectedId").Select(c => c.ColumnName).ToList() ?? new List<string>();
        var dt = new System.Data.DataTable();
        dt.Columns.Add("__SelectedId", typeof(int));
        foreach (var c in columns) dt.Columns.Add(c);
        foreach (var (id, cells) in _rows)
        {
            var values = new object[cells.Length + 1];
            values[0] = id;
            Array.Copy(cells, 0, values, 1, cells.Length);
            dt.Rows.Add(values);
        }
        _grid.AutoGenerateColumns = true;
        _grid.ItemsSource = dt.DefaultView;
        if (_grid.Columns.Count > 0) _grid.Columns[0].Visibility = Visibility.Collapsed;
        _state.Text = filtered.Count == 0
            ? "لا توجد نتائج مطابقة — عدّل شروط البحث أو اضغط عرض الكل."
            : $"النتائج: {_rows.Count} من {filtered.Count} مستنداً" +
              (filtered.Count > 500 ? " — يُعرض أول 500؛ استخدم البحث السريع لتضييقها." : "") +
              " — اختر صفاً ثم اضغط فتح، أو انقر عليه نقرتين.";
    }

    private void Navigate(int move)
    {
        if (_grid.Items.Count == 0) return;
        int current = _grid.SelectedIndex;
        int next = move == 0 ? 0 : move == int.MaxValue ? _grid.Items.Count - 1
            : Math.Clamp(current < 0 ? 0 : current + move, 0, _grid.Items.Count - 1);
        _grid.SelectedIndex = next;
        if (_grid.SelectedItem != null) _grid.ScrollIntoView(_grid.SelectedItem);
    }

    private void OpenSelected()
    {
        if (_rows.Count == 0) { AppContainer.Get<DialogService>().Error("لا توجد نتائج — نفذ البحث أولاً."); return; }
        if (_grid.SelectedIndex < 0) { AppContainer.Get<DialogService>().Error("اختر مستنداً من النتائج ثم اضغط فتح (أو انقر عليه نقرتين متتاليتين)."); return; }
        if (_grid.SelectedItem is System.Data.DataRowView row
            && row.Row.Table.Columns.Contains("__SelectedId"))
            SelectedId = Convert.ToInt32(row["__SelectedId"]);
        else if (_grid.SelectedIndex < _rows.Count)
            SelectedId = _rows[_grid.SelectedIndex].id;
        if (SelectedId == null) return;
        DialogResult = true;
        Close();
    }
}
