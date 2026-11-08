using DatesErp.Core.Common;

namespace DatesErp.Core.Interfaces.Services;

/// <summary>نموذج الثيم المركزي: لا تعتمد الواجهة على XAML أو ألوان متناثرة داخل الشاشات.</summary>
public sealed class ThemeProfileDto
{
    public string Name { get; set; } = "الافتراضي";
    public string Mode { get; set; } = "Light"; // Light | Dark | Auto

    // الألوان
    public string PrimaryColor { get; set; } = "#0F172A";
    public string SecondaryColor { get; set; } = "#2563EB";
    public string BackgroundColor { get; set; } = "#F8FAFC";
    public string WindowBackgroundColor { get; set; } = "#F8FAFC";
    public string TableBackgroundColor { get; set; } = "#FFFFFF";
    public string TableHeaderColor { get; set; } = "#1E293B";
    public string TextColor { get; set; } = "#0F172A";
    public string SecondaryTextColor { get; set; } = "#475569";
    public string ButtonColor { get; set; } = "#0284C7";
    public string ButtonHoverColor { get; set; } = "#0369A1";
    public string ButtonPressedColor { get; set; } = "#075985";
    public string BorderColor { get; set; } = "#CBD5E1";
    public string SuccessColor { get; set; } = "#16A34A";
    public string WarningColor { get; set; } = "#D97706";
    public string ErrorColor { get; set; } = "#DC2626";
    public string InfoColor { get; set; } = "#2563EB";
    public string SelectedRowColor { get; set; } = "#E0F2FE";
    public string HoverRowColor { get; set; } = "#F1F5F9";
    public string AlternateRowColor { get; set; } = "#F8FAFC";
    public string CardBackgroundColor { get; set; } = "#FFFFFF";
    public string InputBackgroundColor { get; set; } = "#FFFFFF";
    public string TitleBarColor { get; set; } = "#0F172A";

    // الخطوط والأحجام
    public string BodyFontFamily { get; set; } = "Segoe UI";
    public string HeadingFontFamily { get; set; } = "Segoe UI";
    public double BaseFontSize { get; set; } = 13;
    public double PageTitleFontSize { get; set; } = 18;
    public double SectionTitleFontSize { get; set; } = 15;
    public double TableFontSize { get; set; } = 13;
    public double ButtonFontSize { get; set; } = 13;
    public bool HeadingBold { get; set; } = true;
    public bool BodyBold { get; set; } = false;

    // الحدود والحقول
    public bool ShowFieldUnderlines { get; set; } = true;
    public bool ShowTextBoxBorders { get; set; } = true;
    public bool ShowButtonBorders { get; set; } = true;
    public double BorderThickness { get; set; } = 1;
    public double CornerRadius { get; set; } = 6;
    public string InputStyle { get; set; } = "Simple"; // Flat | Rounded | Simple
    public double Padding { get; set; } = 6;

    // الأزرار
    public string ButtonStyle { get; set; } = "Rounded"; // Square | Rounded | Flat | Border
    public double ButtonWidth { get; set; } = 0;
    public double ButtonHeight { get; set; } = 34;
    public double IconSize { get; set; } = 14;
    public string IconPosition { get; set; } = "Before"; // Before | After | Above | None
    public string ButtonTextColor { get; set; } = "#FFFFFF";

    // الجداول والنوافذ
    public double RowHeight { get; set; } = 36;
    public bool ShowTableBorders { get; set; } = true;
    public bool ShowColumnDividers { get; set; } = true;
    public string TableTextAlignment { get; set; } = "Right";
    public bool ShowPagination { get; set; } = true;
    public double TitleBarHeight { get; set; } = 38;
    public double ContentWidth { get; set; } = 0;
    public string SidebarStyle { get; set; } = "Classic";
    public double SidebarWidth { get; set; } = 250;
    public bool ShowSidebar { get; set; } = true;
    public bool CardShadowEnabled { get; set; } = true;
    public double ShadowDepth { get; set; } = 1;
    public double ShadowOpacity { get; set; } = .12;

    public ThemeProfileDto Clone() => new()
    {
        Name = Name, Mode = Mode,
        PrimaryColor = PrimaryColor, SecondaryColor = SecondaryColor, BackgroundColor = BackgroundColor,
        WindowBackgroundColor = WindowBackgroundColor, TableBackgroundColor = TableBackgroundColor,
        TableHeaderColor = TableHeaderColor, TextColor = TextColor, SecondaryTextColor = SecondaryTextColor,
        ButtonColor = ButtonColor, ButtonHoverColor = ButtonHoverColor, ButtonPressedColor = ButtonPressedColor,
        BorderColor = BorderColor, SuccessColor = SuccessColor, WarningColor = WarningColor,
        ErrorColor = ErrorColor, InfoColor = InfoColor, SelectedRowColor = SelectedRowColor,
        HoverRowColor = HoverRowColor, AlternateRowColor = AlternateRowColor, CardBackgroundColor = CardBackgroundColor,
        InputBackgroundColor = InputBackgroundColor, TitleBarColor = TitleBarColor,
        BodyFontFamily = BodyFontFamily, HeadingFontFamily = HeadingFontFamily, BaseFontSize = BaseFontSize,
        PageTitleFontSize = PageTitleFontSize, SectionTitleFontSize = SectionTitleFontSize, TableFontSize = TableFontSize,
        ButtonFontSize = ButtonFontSize, HeadingBold = HeadingBold, BodyBold = BodyBold,
        ShowFieldUnderlines = ShowFieldUnderlines, ShowTextBoxBorders = ShowTextBoxBorders, ShowButtonBorders = ShowButtonBorders,
        BorderThickness = BorderThickness, CornerRadius = CornerRadius, InputStyle = InputStyle, Padding = Padding,
        ButtonStyle = ButtonStyle, ButtonWidth = ButtonWidth, ButtonHeight = ButtonHeight, IconSize = IconSize,
        IconPosition = IconPosition, ButtonTextColor = ButtonTextColor, RowHeight = RowHeight,
        ShowTableBorders = ShowTableBorders, ShowColumnDividers = ShowColumnDividers, TableTextAlignment = TableTextAlignment,
        ShowPagination = ShowPagination, TitleBarHeight = TitleBarHeight, ContentWidth = ContentWidth,
        SidebarStyle = SidebarStyle, SidebarWidth = SidebarWidth, ShowSidebar = ShowSidebar,
        CardShadowEnabled = CardShadowEnabled, ShadowDepth = ShadowDepth, ShadowOpacity = ShadowOpacity
    };
}

public sealed class ThemeCatalogDto
{
    public string ActiveName { get; set; } = "الافتراضي";
    public List<ThemeProfileDto> Profiles { get; set; } = new();
}

/// <summary>حفظ واستعادة عدة ثيمات عبر SystemSettings دون تغيير مخطط قاعدة البيانات.</summary>
public interface IThemeSettingsService
{
    ThemeCatalogDto Load();
    OpResult Save(ThemeProfileDto profile, bool activate = true);
    OpResult Activate(string name);
    OpResult Delete(string name);
}

/// <summary>مصنع الثيمات المضمّنة لضمان وجود مظهر صالح حتى قبل أول حفظ.</summary>
public static class ThemeProfileDefaults
{
    public static ThemeProfileDto Light() => new();

    public static ThemeProfileDto Dark() => new()
    {
        Name = "داكن", Mode = "Dark",
        PrimaryColor = "#0F766E", SecondaryColor = "#F59E0B", BackgroundColor = "#111827",
        WindowBackgroundColor = "#111827", TableBackgroundColor = "#1F2937", TableHeaderColor = "#0F766E",
        TextColor = "#F9FAFB", SecondaryTextColor = "#CBD5E1", ButtonColor = "#0F766E",
        ButtonHoverColor = "#14B8A6", ButtonPressedColor = "#115E59", BorderColor = "#475569",
        SuccessColor = "#22C55E", WarningColor = "#F59E0B", ErrorColor = "#F87171", InfoColor = "#38BDF8",
        SelectedRowColor = "#854D0E", HoverRowColor = "#334155", AlternateRowColor = "#1E293B",
        CardBackgroundColor = "#1F2937", InputBackgroundColor = "#111827", TitleBarColor = "#0F172A",
        ButtonTextColor = "#FFFFFF"
    };

    public static ThemeCatalogDto CreateCatalog()
    {
        var light = Light();
        var official = light.Clone(); official.Name = "رسمي";
        var dark = Dark();
        return new ThemeCatalogDto { ActiveName = light.Name, Profiles = new List<ThemeProfileDto> { light, official, dark } };
    }

    /// <summary>
    /// ترقية لون الثيمين المضمّنين فقط إذا كانا مطابقين تماماً للوحة القديمة غير المعدّلة.
    /// أي تخصيص لوني للمستخدم يحمي الملف من الاستبدال؛ الثيمات المسماة الأخرى لا تُمس.
    /// </summary>
    public static ThemeCatalogDto UpgradePristineBuiltIns(ThemeCatalogDto catalog)
    {
        if (catalog?.Profiles == null) return catalog;
        var defaults = CreateCatalog().Profiles;
        foreach (var profile in catalog.Profiles.Where(p => p != null))
        {
            var name = profile.Name?.Trim();
            if (!string.Equals(name, "الافتراضي", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, "رسمي", StringComparison.OrdinalIgnoreCase)) continue;
            if (!HasLegacyPalette(profile)) continue;
            var target = defaults.First(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            profile.PrimaryColor = target.PrimaryColor;
            profile.SecondaryColor = target.SecondaryColor;
            profile.BackgroundColor = target.BackgroundColor;
            profile.WindowBackgroundColor = target.WindowBackgroundColor;
            profile.TableBackgroundColor = target.TableBackgroundColor;
            profile.TableHeaderColor = target.TableHeaderColor;
            profile.TextColor = target.TextColor;
            profile.SecondaryTextColor = target.SecondaryTextColor;
            profile.ButtonColor = target.ButtonColor;
            profile.ButtonHoverColor = target.ButtonHoverColor;
            profile.ButtonPressedColor = target.ButtonPressedColor;
            profile.BorderColor = target.BorderColor;
            profile.SuccessColor = target.SuccessColor;
            profile.WarningColor = target.WarningColor;
            profile.ErrorColor = target.ErrorColor;
            profile.InfoColor = target.InfoColor;
            profile.SelectedRowColor = target.SelectedRowColor;
            profile.HoverRowColor = target.HoverRowColor;
            profile.AlternateRowColor = target.AlternateRowColor;
            profile.CardBackgroundColor = target.CardBackgroundColor;
            profile.InputBackgroundColor = target.InputBackgroundColor;
            profile.TitleBarColor = target.TitleBarColor;
        }
        return catalog;
    }

    private static bool HasLegacyPalette(ThemeProfileDto p)
        => p.PrimaryColor == "#16324F" && p.SecondaryColor == "#C69C3A"
        && p.BackgroundColor == "#F2F5F9" && p.WindowBackgroundColor == "#F2F5F9"
        && p.TableBackgroundColor == "#FFFFFF" && p.TableHeaderColor == "#16324F"
        && p.TextColor == "#1B2733" && p.SecondaryTextColor == "#617286"
        && p.ButtonColor == "#16324F" && p.ButtonHoverColor == "#245477" && p.ButtonPressedColor == "#10263B"
        && p.BorderColor == "#718096" && p.SuccessColor == "#16835D" && p.WarningColor == "#A96812"
        && p.ErrorColor == "#B93D4B" && p.InfoColor == "#2277A5" && p.SelectedRowColor == "#DCEBFA"
        && p.HoverRowColor == "#EDF3F8" && p.AlternateRowColor == "#F7F9FC"
        && p.CardBackgroundColor == "#FFFFFF" && p.InputBackgroundColor == "#FFFFFF" && p.TitleBarColor == "#10263B";
}
