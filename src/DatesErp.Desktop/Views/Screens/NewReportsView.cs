using System.Windows;
using System.Windows.Controls;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// مركز التقارير الاحتياطي والمتوافق مع محرك التقارير الحالي.
/// يعرض شاشة ReportsView الكاملة (المعرض والفلاتر والنتائج والتصدير والطباعة)
/// حتى لا يتعطل التطبيق عند غياب واجهة NewReportsView القديمة من نسخة المصدر.
/// </summary>
public sealed class NewReportsView : UserControl
{
    public NewReportsView()
    {
        FlowDirection = FlowDirection.RightToLeft;
        Content = new ReportsView();
    }
}
