namespace DatesErp.Desktop.Printing;

public enum PrintTemplate
{
    Previous = 1,
    Modern = 2
}

public static class PrintTemplateNames
{
    public static string Display(PrintTemplate template) => template switch
    {
        PrintTemplate.Previous => "النموذج المعتمد — مطابق للمرفقات",
        PrintTemplate.Modern => "نموذج بديل — التصميم الحديث",
        _ => throw new ArgumentOutOfRangeException(nameof(template))
    };
}
