using System.Windows;
using System.Windows.Controls;
using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views.Screens;

public partial class UsersView : UserControl
{
    private List<(int id, string code)> _roles = new();
    private List<object> _usersAll = new();

    public UsersView()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshData();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshData();

    private void Search_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // المعيار يُنفذ من نافذة البحث الموحدة؛ لا تُصفّى القائمة محلياً أثناء الكتابة.
    }

    private void Search_Click(object sender, RoutedEventArgs e) => OpenUserSearch();

    private void OpenUserSearch()
    {
        try
        {
            var win = new DocSearchWindow("المستخدمون",
                new List<SearchFieldDef> { new() { Key = "term", LabelAr = "كود المستخدم أو اسم الدخول أو الاسم الكامل", DefaultValue = SearchBox.Text } }, filters =>
                {
                    using var scope = AppContainer.NewScope();
                    var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                    var all = db.Users.AsNoTracking().OrderBy(u => u.Id).ToList();
                    var term = ScreenSearch.Normalize(filters.GetValueOrDefault("term") ?? "");
                    var rows = all.Where(u => string.IsNullOrEmpty(term)
                        || ScreenSearch.Normalize(u.UserCode).Contains(term)
                        || ScreenSearch.Normalize(u.UserName).Contains(term)
                        || ScreenSearch.Normalize(u.FullName).Contains(term)).ToList();
                    return new SearchResult
                    {
                        Columns = new List<string> { "الكود", "اسم الدخول", "الاسم الكامل", "آخر دخول", "الحالة", "تغيير كلمة المرور" },
                        Rows = rows.Select(u => (u.Id, new object[] { u.UserCode, u.UserName, u.FullName,
                            u.LastLoginDate?.ToString("dd/MM/yyyy HH:mm") ?? "—", u.IsActive ? "نشط" : "موقوف",
                            u.MustChangePassword ? "نعم" : "لا" })).ToList()
                    };
                }) { Owner = Window.GetWindow(this) };
            if (win.ShowDialog() == true && win.SelectedId is int id)
            {
                RefreshData();
                foreach (var item in UsersGrid.Items)
                    if (item?.GetType().GetProperty("Id")?.GetValue(item) is int rowId && rowId == id)
                    { UsersGrid.SelectedItem = item; UsersGrid.ScrollIntoView(item); break; }
            }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Users.Search"); }
    }

    private void RefreshData()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            _roles = db.Roles.OrderBy(r => r.Id).ToList().Select(r => (r.Id, r.RoleNameAr)).ToList();
            RoleBox.ItemsSource = _roles.Select(r => r.code).ToList();

            _usersAll = db.Users.OrderBy(u => u.Id).ToList().Select(u => (object)new
            {
                u.Id,
                u.UserCode,
                Code = u.UserCode,
                u.UserName,
                u.FullName,
                LastLogin = u.LastLoginDate?.ToString("dd/MM/yyyy HH:mm"),
                Status = u.IsActive ? "نشط" : "موقوف",
                MustChange = u.MustChangePassword ? "نعم" : "لا"
            }).ToList();
            UsersGrid.ItemsSource = _usersAll;
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Users.Load"); }
    }

    private int SelectedUserId()
    {
        if (UsersGrid.SelectedItem?.GetType().GetProperty("Id")?.GetValue(UsersGrid.SelectedItem) is int id) return id;
        AppContainer.Get<DialogService>().Error("اختر مستخدماً من الجدول أولاً.");
        return 0;
    }

    private void ToggleUser_Click(object sender, RoutedEventArgs e)
    {
        var id = SelectedUserId(); if (id == 0) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var r = scope.ServiceProvider.GetRequiredService<MasterDataService>().ToggleUserActive(id);
            if (r.Ok) { AppContainer.Get<DialogService>().Info(r.Message); RefreshData(); }
            else AppContainer.Get<DialogService>().Error(r.Message);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Users.Toggle"); }
    }

    private void Unlock_Click(object sender, RoutedEventArgs e)
    {
        var id = SelectedUserId(); if (id == 0) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var r = scope.ServiceProvider.GetRequiredService<MasterDataService>().UnlockUser(id);
            if (r.Ok) { AppContainer.Get<DialogService>().Info(r.Message); RefreshData(); }
            else AppContainer.Get<DialogService>().Error(r.Message);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Users.Unlock"); }
    }

    /// <summary>§إصلاح: تغيير المستخدم لكلمة مروره بنفسه — لم تكن توجد آلية لذلك إطلاقاً.</summary>
    private void ChangeMyPass_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            int uid = AppContainer.Get<DatesErp.Infrastructure.Session.SessionContext>().UserId;
            if (uid <= 0) { AppContainer.Get<DialogService>().Error("لا توجد جلسة مستخدم."); return; }
            var win = new Views.ChangePasswordWindow(uid) { Owner = Window.GetWindow(this) };
            if (win.ShowDialog() == true) AppContainer.Get<DialogService>().Info("تم تغيير كلمة المرور.");
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Users.ChangeMyPassword"); }
    }

    private void ResetPass_Click(object sender, RoutedEventArgs e)
    {
        var id = SelectedUserId(); if (id == 0) return;
        // §الكلمة المفقودة — كلمة الموظف الأصلية لا يمكن معرفتها أبداً (تجزئة غير عكوسة).
        // البديل الاحترافي: تصفير إلى كلمة محددة أو مولَّدة تلقائياً (اترك الحقل فارغاً)،
        // تُعرض مرة واحدة هنا، ويُجبر الموظف على تغييرها بنفسه عند أول دخول.
        var dlg = new InputDialog("تصفير كلمة السر",
            "كلمة السر الجديدة (8 رموز فأكثر، حروف وأرقام) — أو اترك الحقل فارغاً لتوليد كلمة مؤقتة تلقائياً:\nسيُجبر المستخدم على تغييرها عند أول دخول.") { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var r = scope.ServiceProvider.GetRequiredService<MasterDataService>().ResetUserPassword(id, dlg.Value);
            AppContainer.Get<DialogService>().Info(r.Message);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Users.ResetPass"); }
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(UserNameBox.Text)) { AppContainer.Get<DialogService>().Error("أدخل اسم الدخول."); return; }
            if (string.IsNullOrEmpty(PasswordBox.Password)) { AppContainer.Get<DialogService>().Error("أدخل كلمة المرور."); return; }

            using var scope = AppContainer.NewScope();
            var svc = (IAdminService)scope.ServiceProvider.GetService(typeof(IAdminService));
            var r = svc.SaveUser(null, UserCodeBox.Text?.Trim(), UserNameBox.Text.Trim(), FullNameBox.Text.Trim(),
                PasswordBox.Password,
                RoleBox.SelectedIndex >= 0 ? new List<int> { _roles[RoleBox.SelectedIndex].id } : new List<int>(),
                true);
            if (!r.Ok) AppContainer.Get<DialogService>().Error(r.Message);
            else
            {
                AppContainer.Get<DialogService>().Info(r.Message + "\n(سيُطلب من المستخدم تغيير كلمة المرور عند أول دخول)");
                UserCodeBox.Text = ""; UserNameBox.Text = ""; FullNameBox.Text = ""; PasswordBox.Clear();
                RefreshData();
            }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Users.Create"); }
    }
}
