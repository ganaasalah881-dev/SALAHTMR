using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using DatesErp.Infrastructure.Security;
using DatesErp.Infrastructure.Session;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>§11 — تسجيل الدخول وتحميل الأدوار والصلاحيات في الجلسة.</summary>
public class AuthService : IAuthService
{
    private readonly DatesErpDbContext _db;
    private readonly SessionContext _session;
    private readonly IAuditService _audit;

    public AuthService(DatesErpDbContext db, SessionContext session, IAuditService audit)
    {
        _db = db;
        _session = session;
        _audit = audit;
    }

    public LoginResult Login(string userName, string password)
    {
        // §الدخول بالرقم: يقبل رقم الدخول (UserCode) أو رقم الموظف أو اسم المستخدم
        var loginKey = (userName ?? "").Trim();
        var user = _db.Users.Include(u => u.UserRoles).FirstOrDefault(u => u.UserName == loginKey)
            ?? _db.Users.Include(u => u.UserRoles).FirstOrDefault(u => u.UserCode == loginKey)
            ?? _db.Users.Include(u => u.UserRoles).FirstOrDefault(u =>
                u.EmployeeId != null && u.EmployeeId == _db.Employees.Where(e => e.EmployeeCode == loginKey).Select(e => (int?)e.Id).FirstOrDefault());
        if (user != null)
        {
            _db.Entry(user).Reload();
            _db.Entry(user).Collection(u => u.UserRoles).Query().Load();
        }
        // §R6 — رسالة واحدة موحدة للمجهول/المعطّل/كلمة الخطأ: لا كشف لحالة الحسابات (منع الاستنتاج)
        if (user == null || !user.IsActive)
            return new LoginResult { Success = false, Message = "رقم الدخول أو كلمة المرور غير صحيحة." };
        // §إلغاء القفل الآلي نهائياً: فك أي قفل سابق تلقائياً — النظام يعمل بلا أي قفل آلي مهما كانت المحاولات
        if (user.IsLocked || user.FailedLoginCount > 0 || user.LockoutDate != null)
        {
            user.IsLocked = false;
            user.FailedLoginCount = 0;
            user.LockoutDate = null;
            _db.SaveChanges();
        }

        if (!PasswordHasher.Verify(password ?? "", user.PasswordHash, user.PasswordSalt))
        {
            // لا قفل آلي نهائياً مهما تكررت المحاولات الخاطئة
            return new LoginResult { Success = false, Message = "رقم الدخول أو كلمة المرور غير صحيحة." };
        }

        user.FailedLoginCount = 0;
        var previousLogin = user.LastLoginDate;
        user.LastLoginDate = DateTime.Now;

        // §لمسة مؤسسية (قرار #47): انتهاء صلاحية كلمة المرور بعد مدة قابلة للضبط (افتراضي 90 يوماً).
        int maxAge = 90;
        var ageSetting = _db.SystemSettings.AsNoTracking().FirstOrDefault(x => x.SettingKey == "PasswordMaxAgeDays");
        if (ageSetting != null && int.TryParse(ageSetting.SettingValue, out var parsedAge) && parsedAge > 0)
            maxAge = parsedAge;
        int ageDays = user.PasswordChangedDate != null
            ? (int)(DateTime.Now - user.PasswordChangedDate.Value).TotalDays
            : int.MaxValue;   // لم تُغيَّر قط (حساب مبذوق) → تُعتبر منتهية
        bool expired = ageDays >= maxAge;
        if (expired) user.MustChangePassword = true;

        // تعبئة الجلسة: الأدوار + المصفوفة المركزية + التفويض الساري — مسار واحد مشترك (§R2)
        _session.UserId = user.Id;
        _session.UserName = user.UserName;
        new PermissionService(_db, _session).RefreshSessionCache(user.Id);

        _db.SaveChanges();
        _audit.Log("Login", "Login", "Users", user.UserName, user.Id);
        return new LoginResult
        {
            Success = true,
            UserId = user.Id,
            FullName = user.FullName,
            Roles = _session.Roles.ToList(),
            MustChangePassword = user.MustChangePassword,
            PasswordExpired = expired,
            PasswordAgeDays = ageDays == int.MaxValue ? -1 : ageDays,
            LastLoginDate = previousLogin,
            Message = "تم تسجيل الدخول."
        };
    }

    public void Logout()
    {
        if (_session.UserId > 0)
            _audit.Log("Logout", "Logout", "Users", _session.UserName, _session.UserId);
        _session.UserId = 0;
        _session.UserName = null;
        _session.Roles.Clear();
        _session.PermissionCache.Clear();
    }

    /// <summary>§v1.50.30: طوارئ المدير — نفس إجراء ملف reset_admin.flag لكن من زر في شاشة الدخول.</summary>
        /// <summary>فك قفل حساب المدير وإعادة كلمة المرور فوراً وبدون أي ملفات أو اشتراطات معقدة.</summary>
    public OpResult EmergencyUnlockAdmins()
    {
        int n = AdminRecovery.ResetAdminAccounts(_db);
        if (n == 0) return OpResult.Fail("لا يوجد حساب مدير نشط لفك قفله.");
        _audit.Log("Login", "EmergencyUnlock", "Users", $"فك قفل {n} حساب مدير (طوارئ)", null);
        return OpResult.Success($"تم فك قفل حساب المدير وإعادة كلمة المرور إلى {AdminRecovery.TempPassword} بنجاح.");
    }
}