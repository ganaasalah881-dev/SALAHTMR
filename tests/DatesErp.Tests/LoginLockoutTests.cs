using DatesErp.Application.Services;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §v1.50.84 — إلغاء القفل الآلي نهائياً:
/// النظام يعمل بدون أي قفل آلي مهما كانت المحاولات الخاطئة.
/// أي حساب مقفل سابقاً يُفك تلقائياً بمجرد محاولة الدخول أو الإقلاع،
/// وزر طوارئ المدير يفك القفل ويعيد كلمة [REDACTED-DEFAULT-CREDENTIAL] فوراً بدون أي اشتراطات.
/// </summary>
public class LoginLockoutTests
{
    private static (IAuthService auth, DatesErpDbContext db) Svc(TestHost host)
    {
        host.LoginAsAdmin();
        return (host.Get<IAuthService>(), host.Get<DatesErpDbContext>());
    }

    [Fact]
    public void Previously_Locked_User_Is_Auto_Unlocked_On_Login_Attempt()
    {
        using var host = new TestHost();
        var (auth, db) = Svc(host);
        var admin = db.Users.First(u => u.UserName == "admin");
        admin.IsLocked = true;
        admin.LockoutDate = DateTime.Now;
        admin.FailedLoginCount = 5;
        db.SaveChanges();

        // محاولة الدخول بكلمة خاطئة: لا تعرض «الحساب مقفل» بل «غير صحيحة» وتفك القفل فوراً
        var r = auth.Login("admin", "wrong");
        Assert.False(r.Success);
        Assert.Contains("غير صحيحة", r.Message);
        Assert.DoesNotContain("الحساب مقفل", r.Message);

        var after = db.Users.AsNoTracking().First(u => u.Id == admin.Id);
        Assert.False(after.IsLocked);
        Assert.Equal(0, after.FailedLoginCount);
        Assert.Null(after.LockoutDate);
    }

    [Fact]
    public void Wrong_Password_Multiple_Times_Never_Locks_Account()
    {
        using var host = new TestHost();
        var (auth, db) = Svc(host);

        // محاولات متكررة خاطئة (أكثر من 5 مرات)
        for (int i = 0; i < 10; i++)
        {
            var r = auth.Login("admin", $"wrong-{i}");
            Assert.False(r.Success);
            Assert.Contains("غير صحيحة", r.Message);
        }

        var u = db.Users.AsNoTracking().First(x => x.UserName == "admin");
        Assert.False(u.IsLocked, "النظام بدون أي قفل آلي مهما كانت المحاولات الخاطئة");
        Assert.Null(u.LockoutDate);
        Assert.Equal(0, u.FailedLoginCount);

        // الدخول بالكلمة الصحيحة ينجح فوراً
        var valid = auth.Login("admin", "[REDACTED-DEFAULT-CREDENTIAL]");
        Assert.True(valid.Success, valid.Message);
    }

    [Fact]
    public void Emergency_Unlock_Resets_Admin_Password_Directly_Without_Flags()
    {
        using var host = new TestHost();
        var (auth, db) = Svc(host);
        var admin = db.Users.First(u => u.UserName == "admin");
        admin.IsLocked = true; admin.FailedLoginCount = 5; admin.LockoutDate = DateTime.Now;
        db.SaveChanges();

        // فك الطوارئ ينجح فوراً وبدون الحاجة لأي ملف reset_admin.flag
        var r = auth.EmergencyUnlockAdmins();
        Assert.True(r.Ok, r.Message);
        Assert.Contains("[REDACTED-DEFAULT-CREDENTIAL]", r.Message);

        var after = db.Users.AsNoTracking().First(x => x.Id == admin.Id);
        Assert.False(after.IsLocked);
        Assert.Equal(0, after.FailedLoginCount);
        Assert.Null(after.LockoutDate);

        // الدخول بكلمة [REDACTED-DEFAULT-CREDENTIAL] ينجح مباشرة
        var login = auth.Login("admin", "[REDACTED-DEFAULT-CREDENTIAL]");
        Assert.True(login.Success, login.Message);
    }
}
