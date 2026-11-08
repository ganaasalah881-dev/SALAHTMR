using System.Windows;
using DatesErp.Core.Exceptions;

namespace DatesErp.Desktop.Services;

/// <summary>§28 — حوارات موحدة: لا تظهر مكدسات الاستثناءات للمستخدم أبداً.</summary>
public class DialogService
{
    /// <summary>إشارة واجهة للعملية الحالية؛ تستخدمها أزرار الحفظ لإظهار نجاح/فشل حقيقي لا مجرد محاولة.</summary>
    public event Action<string, string> Feedback;

    public void Info(string msg, string title = "نظام Date ERP")
    {
        if (IsSuccessMessage(msg)) Feedback?.Invoke(msg, "ok");
        MessageBox.Show(msg, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public void Error(string msg, string title = "نظام Date ERP")
    {
        Feedback?.Invoke(msg, "error");
        MessageBox.Show(msg, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static bool IsSuccessMessage(string msg)
    {
        if (string.IsNullOrWhiteSpace(msg)) return false;
        var text = msg.TrimStart();
        return new[] { "تم حفظ", "تم الحفظ", "تم إنشاء", "تم انشاء", "تم إصدار", "تم اصدار",
                "تم تسجيل", "تم اعتماد", "تم إقفال", "تم الإقفال", "تم إغلاق", "تم ترحيل", "تم الترحيل",
                "تم تعديل", "تم تحديث", "تم إضافة", "تم تصدير", "تم استيراد", "تمت إضافة", "تم التحديث", "نجح", "حُفظ" }
            .Any(prefix => text.StartsWith(prefix, StringComparison.Ordinal));
    }

    public bool Confirm(string msg, string title = "تأكيد العملية")
        => MessageBox.Show(msg, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>رسالة نجاح الحفظ الموحدة؛ تحفظ تفاصيل رقم السند في النص الإضافي إن توفر.</summary>
    public void Saved(string details = null)
    {
        string message = "تم حفظ العملية بنجاح";
        if (!string.IsNullOrWhiteSpace(details)) message += " — " + details.Trim();
        Toast(message, "ok");
    }

    /// <summary>§44 — نجاح غير حاجب: Toast بدل نافذة معلومات تقطع تدفق الإدخال.</summary>
    public void Toast(string msg, string kind = "ok")
    {
        if (kind is "ok" or "success") Feedback?.Invoke(msg, "ok");
        else if (kind is "error" or "danger") Feedback?.Invoke(msg, "error");
        UiToast.Show(msg, kind);
    }

    /// <summary>§44 — تأكيد فعل هدّام بعرض أثره على المخزون/المستندات قبل الإتمام.</summary>
    public bool ConfirmImpact(string msg, params string[] impacts)
        => Confirm(msg + "\n\nأثر هذه العملية:\n- " + string.Join("\n- ", impacts), "تأكيد عملية مؤثرة");

    /// <summary>ترجمة أي استثناء إلى رسالة عربية آمنة + تسجيل التفاصيل في السجل.</summary>
    public void HandleException(Exception ex, string operation)
    {
        ErrorLog.Write(ex, operation);
        string msg = ex switch
        {
            DomainException d => d.Message,
            Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException => new ConcurrencyConflictException().Message,
            _ => "حدث خطأ أثناء تنفيذ العملية.\nالتفاصيل: " + (ex.InnerException?.Message ?? ex.Message) + "\nتم تسجيل الخطأ في سجل النظام."
        };
        Error(msg);
    }
}
