using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DatesErp.Desktop.Services;

namespace DatesErp.Desktop.Views;

/// <summary>بوابة موحدة لأزرار الحفظ المحلية التي لا تمر عبر ErpToolbar.</summary>
public static class SaveConfirmationBehavior
{
    private static bool _installed;
    private static readonly DependencyProperty WrappedProperty = DependencyProperty.RegisterAttached(
        "Wrapped", typeof(bool), typeof(SaveConfirmationBehavior), new PropertyMetadata(false));
    private static readonly DependencyProperty BusyProperty = DependencyProperty.RegisterAttached(
        "Busy", typeof(bool), typeof(SaveConfirmationBehavior), new PropertyMetadata(false));

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        EventManager.RegisterClassHandler(typeof(Button), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnButtonLoaded), true);
    }

    public static void MarkHandled(Button button)
    {
        if (button != null) button.SetValue(WrappedProperty, true);
    }

    private static void OnButtonLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || (bool)button.GetValue(WrappedProperty)) return;
        string label = button.Content?.ToString() ?? string.Empty;
        if (label.Contains("حفظ", StringComparison.Ordinal))
        {
            button.PreviewMouseLeftButtonDown += OnMouseDown;
            button.PreviewKeyDown += OnKeyDown;
            button.SetValue(WrappedProperty, true);
            return;
        }
        // الأزرار المحلية (خارج ErpToolbar) تُظهر أن وضع/إجراء الإضافة فُعّل بعد النقر.
        // لا نلمس Tag المستعمل مسبقاً لحمل بيانات الصفوف أو حالات خاصة.
        if (button.Tag == null && (label.Contains("إضافة", StringComparison.Ordinal)
                || label.Contains("جديد", StringComparison.Ordinal)))
        {
            button.Click += (_, _) =>
            {
                if (button.Tag == null)
                {
                    button.Tag = "AddActive";
                    button.Background = new SolidColorBrush(Color.FromRgb(0x8A, 0x5A, 0x24));
                    button.BorderBrush = new SolidColorBrush(Color.FromRgb(0x70, 0x47, 0x19));
                    button.Foreground = Brushes.White;
                }
            };
            button.SetValue(WrappedProperty, true);
        }
    }

    private static void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button button || !button.IsEnabled) return;
        e.Handled = true;
        ConfirmAndRaiseClick(button);
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not Button button || !button.IsEnabled
            || (e.Key != Key.Enter && e.Key != Key.Space)) return;
        e.Handled = true;
        ConfirmAndRaiseClick(button);
    }

    private static void ConfirmAndRaiseClick(Button button)
    {
        if ((bool)button.GetValue(BusyProperty)) return;
        string operation = Regex.Replace(button.Content?.ToString() ?? "حفظ العملية", @"^[^\p{L}\p{N}]+", "").Trim();
        if (string.IsNullOrWhiteSpace(operation)) operation = "حفظ العملية";
        if (!AppContainer.Get<DialogService>().Confirm($"هل تريد تأكيد {operation}؟")) return;

        button.SetValue(BusyProperty, true);
        var chrome = FindChrome(button);
        var previousMode = chrome?.CurrentMode ?? ErpScreenMode.View;
        chrome?.SetScreenMode(ErpScreenMode.Saving, operation);
        var originalContent = button.Content;
        var originalTag = button.Tag;
        var originalBackground = button.Background;
        var originalBorderBrush = button.BorderBrush;
        var originalForeground = button.Foreground;
        button.Tag = "Saving";
        button.Content = "⏳ جارٍ الحفظ…";
        button.Background = new SolidColorBrush(Color.FromRgb(0xB7, 0x79, 0x1F));
        button.BorderBrush = new SolidColorBrush(Color.FromRgb(0x8C, 0x5A, 0x17));
        button.Foreground = Brushes.White;
        bool? saveSucceeded = null;
        var dialogs = AppContainer.Get<DialogService>();
        Action<string, string> feedback = (_, kind) => saveSucceeded = kind == "ok";
        dialogs.Feedback += feedback;
        try { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button)); }
        catch
        {
            saveSucceeded = false;
            throw;
        }
        finally
        {
            dialogs.Feedback -= feedback;
            if (saveSucceeded == true || button.Tag?.ToString() == "Saved")
            {
                button.Tag = "Saved";
                button.Content = button.Content?.ToString()?.Contains("تم الحفظ", StringComparison.Ordinal) == true
                    ? button.Content : "✓ تم الحفظ";
                button.Background = new SolidColorBrush(Color.FromRgb(0x1D, 0x6B, 0x45));
                button.BorderBrush = new SolidColorBrush(Color.FromRgb(0x14, 0x53, 0x2D));
                button.Foreground = Brushes.White;
                button.ToolTip = "تم تأكيد نجاح عملية الحفظ.";
            }
            else
            {
                button.Tag = originalTag;
                button.Content = originalContent;
                button.Background = originalBackground;
                button.BorderBrush = originalBorderBrush;
                button.Foreground = originalForeground;
            }
            if (chrome?.CurrentMode == ErpScreenMode.Saving) chrome.SetScreenMode(previousMode);
            var release = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
            release.Tick += (_, _) => { release.Stop(); button.SetValue(BusyProperty, false); };
            release.Start();
        }
    }

    private static ErpChrome FindChrome(DependencyObject child)
    {
        for (var current = child; current != null; current = VisualTreeHelper.GetParent(current))
            if (current is ErpChrome chrome) return chrome;
        return null;
    }
}
