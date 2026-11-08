using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DatesErp.Desktop.Views.Screens;

namespace DatesErp.Desktop.Views;

/// <summary>نافذة مركز التقارير مع زر عودة واضح إلى الواجهة الرئيسية.</summary>
public sealed class NewReportsWindow : Window
{
    public NewReportsWindow()
    {
        Title = "التقارير الجديدة — Date ERP";
        Width = 1450;
        Height = 900;
        MinWidth = 1040;
        MinHeight = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FlowDirection = FlowDirection.RightToLeft;
        Background = Brushes.White;

        var root = new DockPanel { FlowDirection = FlowDirection.RightToLeft };
        var header = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(22, 40, 60)),
            Padding = new Thickness(16, 10, 16, 10)
        };
        var headerContent = new DockPanel { LastChildFill = true };
        var backButton = new Button
        {
            Content = "↩ العودة إلى الواجهة الرئيسية",
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(12, 0, 0, 0),
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(22, 101, 52)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(34, 197, 94)),
            FontWeight = FontWeights.Bold,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center
        };
        backButton.Click += (_, _) => ReturnToMainWindow();
        DockPanel.SetDock(backButton, Dock.Left);
        headerContent.Children.Add(backButton);
        headerContent.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "مركز التقارير", Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeights.Bold },
                new TextBlock { Text = "التقارير والفلاتر والنتائج والتصدير والطباعة", Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225)), FontSize = 11 }
            }
        });
        header.Child = headerContent;
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(new NewReportsView());
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; ReturnToMainWindow(); }
        };
    }

    private void ReturnToMainWindow()
    {
        var main = Owner;
        Close();
        main?.Activate();
    }
}
