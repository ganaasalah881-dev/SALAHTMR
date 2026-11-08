using System.Windows;
using System.Windows.Controls;
using DatesErp.Desktop.Services;

namespace DatesErp.Desktop.Views;

/// <summary>§23 — نافذة نموذج عامة (جديد/تعديل) تُبنى ديناميكياً من تعريف الحقول.</summary>
public class FieldDef
{
    public string Key { get; set; }
    public string LabelAr { get; set; }
    public string Kind { get; set; } = "text"; // text | number | combo | check
    public string[] Options { get; set; }
    public string Default { get; set; }
    /// <summary>§B84/V7: حقل إلزامي — يُرفض الحفظ عند فراغه (افتراضي false: لا يتغير سلوك الشاشات القائمة).</summary>
    public bool Required { get; set; }
}

public partial class EntityFormDialog : Window
{
    private readonly List<(FieldDef def, FrameworkElement control)> _controls = new();
    private bool _isDirty;
    private bool _isSubmitting;
    private bool _closeApproved;
    public Dictionary<string, object> Values { get; } = new();

    public EntityFormDialog(string title, IEnumerable<FieldDef> fields, Dictionary<string, object> initialValues = null)
    {
        Title = title;
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 480;
        ResizeMode = ResizeMode.NoResize;
        Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#F4F6F4");

        var panel = new StackPanel { Margin = new Thickness(20) };
        initialValues ??= new Dictionary<string, object>();

        // §الاحتفاظ بالقيم الإضافية الممررة من الشاشة (مثل __id رقم السجل للتعديل)
        // حتى لا يتحول التعديل إلى إضافة جديدة ويعطي «هذا الرقم محجوز»
        foreach (var kv in initialValues) Values[kv.Key] = kv.Value;

        foreach (var f in fields)
        {
            panel.Children.Add(new TextBlock
            {
                Text = f.LabelAr,
                Style = (Style)System.Windows.Application.Current.FindResource("FieldLabel")
            });

            FrameworkElement ctl;
            object initial = initialValues.TryGetValue(f.Key, out var v) ? v : (object)f.Default;

            switch (f.Kind)
            {
                case "combo":
                    var cb = new ComboBox { ItemsSource = f.Options };
                    if (initial != null) cb.SelectedItem = initial.ToString();
                    else if (f.Options?.Length > 0) cb.SelectedIndex = 0;
                    ctl = cb;
                    break;
                case "check":
                    // §إصلاح: القيمة الافتراضية قد تكون نص "true" — نحوله لمنطقي حتى لا تُنشأ
                    // السجلات الجديدة «موقوفة» فتختفي من القوائم المنسدلة
                    bool initialCheck = initial is bool bb
                        ? bb
                        : bool.TryParse(initial?.ToString(), out var parsed) && parsed;
                    // §B84/B6: التسمية كانت تظهر مرتين (TextBlock أعلاه + محتوى الـ CheckBox) — الآن مرة واحدة.
                    var ck = new CheckBox { Content = "", IsChecked = initialCheck };
                    ctl = ck;
                    break;
                case "number":
                    // §B84/B6: نوع number المعلن في التعليق كان يسقط على نص حر — الآن إدخال رقمي
                    // (أرقام + فاصلة عشرية واحدة + ناقص في البداية). اللصق يتحقق منه الـ Backend.
                    var num = new TextBox { Text = initial?.ToString() ?? "" };
                    num.PreviewTextInput += (_, e) => { e.Handled = !IsNumberChar(e.Text, num.Text); };
                    ctl = num;
                    break;
                default:
                    var tb = new TextBox { Text = initial?.ToString() ?? "" };
                    ctl = tb;
                    break;
            }
            _controls.Add((f, ctl));
            switch (ctl)
            {
                case TextBox textBox: textBox.TextChanged += (_, _) => _isDirty = true; break;
                case ComboBox comboBox: comboBox.SelectionChanged += (_, _) => _isDirty = true; break;
                case CheckBox checkBox:
                    checkBox.Checked += (_, _) => _isDirty = true;
                    checkBox.Unchecked += (_, _) => _isDirty = true;
                    break;
            }
            panel.Children.Add(ctl);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        // §B84/K1: Enter للحفظ وEscape للإلغاء.
        var ok = new Button { Content = "💾 حفظ", Style = (Style)System.Windows.Application.Current.FindResource("PrimaryButton"), IsDefault = true };
        SaveConfirmationBehavior.MarkHandled(ok);
        ok.Click += (_, _) =>
        {
            if (_isSubmitting) return;
            // §B84/V7: الحقول الإلزامية تُرفض فارغة مع تركيز الحقل المخطئ (بدل تحويل العبء كله للـ Backend)
            foreach (var (def, ctl) in _controls)
            {
                if (!def.Required) continue;
                string cur = ctl switch
                {
                    TextBox t => t.Text,
                    ComboBox c => c.SelectedItem?.ToString(),
                    _ => "x"
                };
                if (string.IsNullOrWhiteSpace(cur))
                {
                    AppContainer.Get<DialogService>().Error($"الحقل «{def.LabelAr}» إلزامي — أدخل قيمة قبل الحفظ.");
                    ctl.Focus();
                    return;
                }
            }
            string entityTitle = Title.Split('—')[0].Trim();
            string verb = Title.Contains("تعديل", StringComparison.Ordinal) ? "تأكيد حفظ تعديلات" : "تأكيد حفظ";
            if (!AppContainer.Get<DialogService>().Confirm($"هل تريد {verb} {entityTitle}؟")) return;
            _isSubmitting = true;

            // تُحدَّث حقول النموذج فقط — القيم الإضافية (__id وغيره) تبقى محفوظة
            foreach (var (def, ctl) in _controls)
            {
                Values[def.Key] = ctl switch
                {
                    TextBox t => (t.Text ?? "").Trim(),
                    ComboBox c => c.SelectedItem?.ToString(),
                    CheckBox c => c.IsChecked == true,
                    _ => null
                };
            }
            DialogResult = true;
            Close();
        };
        var cancel = new Button { Content = "إلغاء", Style = (Style)System.Windows.Application.Current.FindResource("SecondaryButton") };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = new System.Windows.Controls.ScrollViewer { Content = panel, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto, MaxHeight = 560 };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            e.Handled = true;
            DialogResult = false;
            Close();
        };
        Closing += (_, e) =>
        {
            if (_closeApproved || _isSubmitting || DialogResult == true || !_isDirty) return;
            string entityTitle = Title.Split('—')[0].Trim();
            if (!AppContainer.Get<DialogService>().Confirm($"توجد تغييرات غير محفوظة في {entityTitle}. هل تريد إلغاءها؟"))
            {
                e.Cancel = true;
                return;
            }
            _closeApproved = true;
        };
        SizeToContent = SizeToContent.Height;
    }

    /// <summary>§B84/B6: أحرف الإدخال الرقمي المسموحة (فاصلة واحدة + ناقص في البداية فقط).</summary>
    private static bool IsNumberChar(string input, string current)
    {
        if (string.IsNullOrEmpty(input)) return true;
        current ??= "";
        foreach (char c in input)
        {
            if (char.IsDigit(c)) continue;
            if ((c == '.' || c == ',') && !current.Contains('.') && !current.Contains(',')) continue;
            if (c == '-' && current.Length == 0) continue;
            return false;
        }
        return true;
    }
}
