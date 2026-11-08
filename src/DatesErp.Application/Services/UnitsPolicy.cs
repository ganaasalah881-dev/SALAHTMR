using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Exceptions;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §نظام الوحدات والمجموعات — المعيار الرسمي المفروض مركزياً (قاعدة بيانات + Backend + API + واجهات):
/// • 001 المواد الخام: الاستلام فقط — وحدة الاستلام مرنة (كرتون/سلة/كجم) والكمية القياسية للمخزون = كجم.
/// • 002 المنتجات التامة: الإنتاج والتسليم — الوحدة الأساسية كرتونة، والوزن المكافئ بالكيلو
///   يُحسب من تعريف العبوة/المنتج (عدد القوالب × وزن القالب أو وزن الكرتون) — لا وزن ثابت افتراضي.
/// • 003 المخرجات الثانوية: وحدتها من تعريف الصنف (كجم/كرتون/حبة...) — §لا وحدة مفروضة في الكود.
/// المجموعة مرتبطة بنوع العملية وليست رقماً شكلياً: أي عملية بصنف من مجموعة غير مسموحة تُرفض هنا.
/// </summary>
public static class UnitsPolicy
{
    public const string GroupRaw = "001";
    public const string GroupFinished = "002";
    public const string GroupByProduct = "003";
    /// <summary>§كرتون/وعاء مرتجع — أُضيفت لاحقاً وكانت مجهولة لدى هذا المعيار.</summary>
    public const string GroupPack = "004";

    /// <summary>نوع الصنف المسموح لكل مجموعة — المرجع المركزي للتصنيف.</summary>
    public static string ItemTypeOfGroup(string groupCode) => groupCode switch
    {
        GroupRaw => "Raw",
        GroupFinished => "Finished",
        GroupByProduct => "ByProduct",
        // §إصلاح: كانت 004 تُرجع null رغم أن SaveProductFull يعتمدها نوعاً رابعاً
        GroupPack => "Pack",
        _ => null
    };

    // ═══════════════════════════════════════════════════════════════════════
    // §قاعدة الوحدات الإلزامية على مستوى النظام — مصدر واحد للحقيقة
    //
    //   المواد الداخلة / الخام (001)      = KG
    //   الإنتاج التام (002)                = CARTON (بوزن كرتون من تعريف المنتج/العبوة)
    //   المخرجات الثانوية (003)            = KG
    //
    // لا يجوز لصنف أن يحمل وحدة تخالف نوعه. والمخرج الثانوي الذي يُراد عدّه
    // بالكرتون يُعرَّف منتجاً تاماً (002) فيخضع لقاعدة الإنتاج التام.
    //
    // §تاريخ: في B33 رُفع الإلزام استجابةً لطلب «لا تفرض الوحدات داخل الكود»،
    // فأصبح أي صنف يقبل أي وحدة. القاعدة المعتمدة لاحقاً نسخت ذلك وأعادته —
    // وهذا الموضع هو المكان الوحيد الذي يُقرر فيه الأمر، لا الشاشات.
    // ═══════════════════════════════════════════════════════════════════════

    public const string UnitKg = "كجم";
    public const string UnitCarton = "كرتون";

    /// <summary>الوحدة الإلزامية لنوع الصنف — null تعني «بلا إلزام» (كرتون/وعاء مرتجع).</summary>
    public static string MandatedUnitFor(string itemType) => itemType switch
    {
        "Raw" => UnitKg,
        "Finished" => UnitCarton,
        "ByProduct" => UnitKg,
        _ => null
    };

    /// <summary>اسم النوع بالعربية لرسائل الرفض.</summary>
    public static string ItemTypeNameAr(string itemType) => itemType switch
    {
        "Raw" => "مادة خام (001)",
        "Finished" => "منتج تام (002)",
        "ByProduct" => "مخرج ثانوي (003)",
        "Pack" => "كرتون/وعاء مرتجع (004)",
        _ => itemType ?? "—"
    };

    /// <summary>
    /// §الوحدة الافتراضية للمجموعة — تيسير عند الفراغ فقط، لا إلزام.
    /// القاعدة المعتمدة: لا تُفرض الوحدات داخل الكود؛ الوحدة تأتي من تعريف الصنف
    /// في شاشة الأصناف المركزية، ومجموعة الصنف وتصنيفه هما ما يحدد نوعه لا اسم الوحدة.
    /// </summary>
    public static string DefaultUnitFor(string itemType) => MandatedUnitFor(itemType);

    /// <summary>
    /// §لا وزن كرتون ثابت: يُرفض أي بند بكمية كراتين أو كجم ما لم يكن وزن العبوة
    /// معرّفاً للصنف/العبوة؛ لا يُفترض وزن ثابت ولا تُخزّن كمية على أنها محسوبة بالتخمين.
    /// </summary>
    public static void RequireCartonWeight(DatesErpDbContext db, int productId, int? packagingTypeId,
        int cartons, string contextAr, double plannedQtyKg = 0)
    {
        if (cartons <= 0 && plannedQtyKg <= 0) return;
        double w = CartonWeight(db, productId, packagingTypeId);
        if (w > 0) return;
        string name = db.Products.AsNoTracking().Where(p => p.Id == productId).Select(p => p.ProductNameAr).FirstOrDefault() ?? $"#{productId}";
        throw new DomainException(
            $"⛔ {contextAr}: سُجّلت {cartons:N0} كرتون و{plannedQtyKg:N1} كجم للصنف «{name}» بلا وزن كرتون معرَّف.\n" +
            "عرّف وزن الكرتون (أو عدد القوالب × وزن القالب) في بطاقة الصنف أو العبوة.\n" +
            "النظام لا يفترض وزناً ثابتاً للكرتون — فأي حساب سيكون تخميناً.",
            "CARTON_WEIGHT_UNDEFINED");
    }

    /// <summary>§منع الخلط بين المجموعات: يفرض أن الصنف من النوع/المجموعة المطلوبة للعملية.</summary>
    public static void RequireItemType(DatesErpDbContext db, int productId, string requiredItemType, string operationAr)
    {
        var product = db.Products.AsNoTracking().FirstOrDefault(p => p.Id == productId)
                      ?? throw new DomainException("الصنف غير موجود في بطاقة الأصناف.");
        if (string.Equals(product.ItemType, requiredItemType, StringComparison.OrdinalIgnoreCase)) return;

        string typeAr = product.ItemType switch { "Raw" => "مادة خام (001)", "Finished" => "منتج تام (002)", "ByProduct" => "مخرج ثانوي (003)", "Pack" => "كرتون/وعاء مرتجع (004)", _ => product.ItemType };
        string requiredAr = requiredItemType switch { "Raw" => "المواد الخام (001)", "Finished" => "المنتجات التامة (002)", "ByProduct" => "المخرجات الثانوية (003)", "Pack" => "الكراتين والأوعية المرتجعة (004)", _ => requiredItemType };
        throw new DomainException(
            $"⛔ {operationAr}: الصنف «{product.ProductNameAr}» من {typeAr} — وغير مسموح هنا.\n" +
            $"هذه العملية تقبل فقط أصناف {requiredAr}.\n" +
            $"المجموعات معيار ملزم: 001 للاستلام الخام | 002 للإنتاج التام والتسليم | 003 للمخرجات الثانوية (كجم).",
            "WRONG_GROUP");
    }

    /// <summary>
    /// §وزن الكرتون المعتمد لصنف/عبوة: العبوة المحددة أولاً، ثم وزن كرتون المنتج،
    /// ثم عدد القوالب × وزن القالب (مثال: 5 قوالب × 2 كجم = 10 كجم/كرتون).
    /// </summary>
    /// <summary>§وزن الكرتون المعتمد لصنف/عبوة: العبوة المحددة أولاً (وزن مباشر أو قوالب × وزن قالب)، ثم بطاقة الصنف.</summary>
    public static double CartonWeight(DatesErpDbContext db, int productId, int? packagingTypeId)
    {
        if (packagingTypeId != null)
        {
            var pack = db.PackagingTypes.AsNoTracking()
                .Where(p => p.Id == packagingTypeId)
                .Select(p => new { p.UnitWeightKg, p.MoldsCount, p.MoldWeightKg })
                .FirstOrDefault();
            if (pack != null)
            {
                if (pack.UnitWeightKg > 0) return pack.UnitWeightKg;
                if (pack.MoldsCount > 0 && pack.MoldWeightKg > 0) return pack.MoldsCount * pack.MoldWeightKg;
            }
        }
        var prod = db.Products.AsNoTracking().FirstOrDefault(p => p.Id == productId);
        return CartonWeight(prod, null);
    }

    /// <summary>نفس سياسة الوزن عند استخدام بيانات العبوة المحمّلة مسبقاً لتجنّب استعلام لكل صنف.</summary>
    public static double CartonWeight(Product prod, double? packagingUnitWeightKg)
    {
        if (packagingUnitWeightKg > 0) return packagingUnitWeightKg.Value;
        if (prod == null) return 0;
        if (prod.CartonWeightKg > 0) return prod.CartonWeightKg;
        if (prod.MoldsCount > 0 && prod.MoldWeightKg > 0) return prod.MoldsCount * prod.MoldWeightKg;
        return 0;
    }

    /// <summary>
    /// §حفظ تعريف التعبئة وقت العملية — بالأولوية نفسها المتبعة في وزن الكرتون
    /// (العبوة المحددة أولاً ثم بطاقة الصنف). يُستدعى عند إنشاء بند أمر أو إقفال
    /// حتى لا يؤدي تعديل تعريف المنتج لاحقاً إلى تغيير نتائج قديمة.
    /// </summary>
    public static (int MoldsCount, double MoldWeightKg) PackagingDefinition(
        DatesErpDbContext db, int productId, int? packagingTypeId)
    {
        if (packagingTypeId != null)
        {
            var pack = db.PackagingTypes.AsNoTracking().FirstOrDefault(p => p.Id == packagingTypeId);
            if (pack != null && pack.MoldsCount > 0 && pack.MoldWeightKg > 0)
                return (pack.MoldsCount, pack.MoldWeightKg);
        }
        var prod = db.Products.AsNoTracking().FirstOrDefault(p => p.Id == productId);
        return (prod?.MoldsCount ?? 0, prod?.MoldWeightKg ?? 0);
    }

    /// <summary>
    /// §القاعدة 5 — الإنتاج الأساسي كرتونة والوزن المكافئ يُحسب من وزن الكرتون:
    /// إن أُعطيت الكراتين بلا وزن ← الكيلو = كراتين × وزن الكرتون.
    /// §1.50.68 FIX: إن أُعطيا معاً وتطابقا ضمن تسامح → اقبل، وإلا صحح تلقائياً
    /// لأن الكرتون هو الوحدة الأساسية (يمنع رسالة "لا تطابق" التي ظهرت في الصورة)
    /// </summary>
    public static double EnsureCartonKgConsistency(DatesErpDbContext db, int productId, int? packagingTypeId,
        double qtyKg, int cartons, string contextAr)
    {
        if (cartons <= 0) return qtyKg;
        double weight = CartonWeight(db, productId, packagingTypeId);
        if (weight <= 0) return qtyKg; // لا تعريف وزن بعد — لا حساب

        double computed = Math.Round(cartons * weight, 1);
        if (qtyKg <= 0) return computed;

        double tolerance = Math.Max(1.0, qtyKg * 0.02);
        if (Math.Abs(qtyKg - computed) > tolerance)
        {
            // §1.50.68: بدل رمي استثناء يمنع الحفظ، صحح تلقائياً
            // مثال الصورة: 3000 كجم و 4000 كرتون × 2.5 = 10000 → نرجع 10000
            // السجل سيحفظ القيمة الصحيحة المطابقة للكراتين
            return computed;
        }
        return qtyKg;
    }

    /// <summary>
    /// يتحقق بصرامة من تطابق الوزن بالكيلو مع عدد الكراتين المسجل في إقفال الإنتاج ومحاضر الجودة المرتبطة بأمر،
    /// ويشتق الوزن تلقائياً فقط عندما يكون الكيلو غير مُدخل (صفراً).
    /// </summary>
    public static double RequireCartonKgMatch(DatesErpDbContext db, int productId, int? packagingTypeId,
        double qtyKg, int cartons, string contextAr)
    {
        if (cartons <= 0) return qtyKg;
        double weight = CartonWeight(db, productId, packagingTypeId);
        if (weight <= 0) return qtyKg;

        double computed = Math.Round(cartons * weight, 1);
        if (qtyKg <= 0) return computed;

        double tolerance = Math.Max(1.0, Math.Max(qtyKg, computed) * 0.02);
        if (Math.Abs(qtyKg - computed) > tolerance)
        {
            string name = db.Products.AsNoTracking().Where(p => p.Id == productId).Select(p => p.ProductNameAr).FirstOrDefault() ?? $"#{productId}";
            throw new DomainException(
                $"⛔ {contextAr}: الكمية بالكيلو ({qtyKg:N1} كجم) لا تطابق عدد الكراتين ({cartons:N0} × {weight:N2} = {computed:N1} كجم) للصنف «{name}».",
                "CARTON_KG_MISMATCH");
        }
        return qtyKg;
    }

    /// <summary>
    /// §قاعدة الكرتون عند التسليم: الكراتين هي الوحدة الأساسية والكجم وزن مكافئ يُشتق منها.
    /// يُستخدم حيث الكراتين هي المُدخَل الموثوق (تسليم العميل) — فيُشتق الكجم بدل تصالب مدخلين.
    /// </summary>
    public static double DeriveKgFromCartons(DatesErpDbContext db, int productId, int? packagingTypeId,
        double qtyKg, int cartons)
    {
        if (cartons <= 0) return qtyKg;
        double weight = CartonWeight(db, productId, packagingTypeId);
        return weight > 0 ? Math.Round(cartons * weight, 1) : qtyKg;
    }

    // ═══════════════ §أمر وحدة القياس الرسمية — ثوابت العرض المركزية ═══════════════
    public const string RawReferenceUnit = UnitKg;
    public const string FinishedOfficialUnit = UnitCarton;
    public const string ByProductOfficialUnit = UnitKg;
    public const string RawQtyHeader = "الكمية (كجم)";
    public const string FinishedQtyHeader = "الكمية (كرتون)";
    public const string FinishedWeightHeader = "الوزن (كجم)";
    public const string BalanceCtnHeader = "الرصيد (كرتون)";
    public const string BalanceKgHeader = "الرصيد (كجم)";
    public const string PackagesHeader = "العبوات";

    /// <summary>تحويل أي وحدة استلام إلى الكجم المرجعي: العدد × وزن الوحدة.</summary>
    public static double KgOf(double packageCount, double unitWeightKg) => packageCount * unitWeightKg;
    /// <summary>المكافئ بالكرتون من الكجم وفق وزن عبوة المنتج الفعلي (لا قيمة ثابتة).</summary>
    public static double CartonsOf(double kg, double cartonWeightKg) => cartonWeightKg > 0 ? kg / cartonWeightKg : 0;
    /// <summary>الوزن الكلي بالكرتون: عدد الكراتين × وزن العبوة الفعلي.</summary>
    public static double KgOfCartons(double cartons, double cartonWeightKg) => cartons * cartonWeightKg;

    // ═══════════════ §مساعدات المخازن والوحدات ═══════════════
    public static string NormalizeReceiptUnit(string unit, string packName = null, int packageCount = 0, double unitWeightKg = 0)
    {
        string u = (unit ?? "").Trim();
        string p = (packName ?? "").Trim();

        if (u.Contains("كرتون") || p.Contains("كرتون")) return "كرتون";
        if (u.Contains("سلة") || u.Contains("سله") || p.Contains("سلة") || p.Contains("سله")) return "سلة";
        if (u.Contains("عبوة") || u.Contains("عبوه") || p.Contains("عبوة") || p.Contains("عبوه")) return "عبوة";
        if (u.Contains("كجم") || u.Contains("كيلو") || u.Contains("كيلوغرام")) return "كجم";

        if (packageCount > 0 && unitWeightKg > 0)
        {
            if (!string.IsNullOrWhiteSpace(p)) return p;
            return "عبوة";
        }

        return string.IsNullOrWhiteSpace(u) ? "كجم" : u;
    }

    public static string NormalizeWarehouseType(string type)
    {
        string t = (type ?? "").Trim();
        if (t.StartsWith("Finished", StringComparison.OrdinalIgnoreCase) || t.Contains("تام")) return "Finished";
        if (t.StartsWith("Raw", StringComparison.OrdinalIgnoreCase) || t.Contains("خام")) return "Raw";
        if (t.StartsWith("Auxiliary", StringComparison.OrdinalIgnoreCase) || t.Contains("مساعدة") || t.Contains("مساعده")) return "Auxiliary";
        if (t.StartsWith("Pack", StringComparison.OrdinalIgnoreCase) || t.Contains("تغليف") || t.Contains("كرتون")) return "Pack";
        if (t.StartsWith("ByProduct", StringComparison.OrdinalIgnoreCase) || t.Contains("ثانوية") || t.Contains("ثانويه")) return "ByProduct";
        if (t.StartsWith("Quarantine", StringComparison.OrdinalIgnoreCase) || t.Contains("حجر") || t.Contains("فحص")) return "Quarantine";
        return string.IsNullOrWhiteSpace(t) ? "Raw" : t;
    }

    public static string WarehouseTypeNameAr(string type, string code = null)
    {
        string c = (code ?? "").Trim().ToUpperInvariant();
        if (c == "WRM") return "مخزن مواد خام (WRM)";
        if (c == "WFG") return "مستودع إنتاج تام (WFG)";
        if (c == "WBY") return "مخزن مخرجات ثانوية (WBY)";
        if (c == "WQUAR") return "مخزن حجر وفحص (WQUAR)";

        string n = NormalizeWarehouseType(type);
        return n switch
        {
            "Raw" => "مخزن مواد خام",
            "Finished" => "مستودع إنتاج تام",
            "ByProduct" => "مخزن مخرجات ثانوية",
            "Quarantine" => "مخزن حجر وفحص",
            "Auxiliary" => "مخزن مواد مساعدة",
            "Pack" => "مخزن تغليف وكرتون",
            _ => type ?? ""
        };
    }

    public static bool IsFinishedWarehouseType(string type)
    {
        return NormalizeWarehouseType(type) == "Finished";
    }

    public static List<int> GetFinishedWarehouseIds(DatesErpDbContext db)
    {
        var whs = db.Warehouses.AsNoTracking().Where(w => w.IsActive).ToList();
        var ids = whs.Where(w => IsFinishedWarehouseType(w.WarehouseType) || w.WarehouseCode == "WFG").Select(w => w.Id).ToList();
        return ids.Count > 0 ? ids : whs.Select(w => w.Id).ToList();
    }

    public static List<int> GetRawSourceWarehouseIds(DatesErpDbContext db)
    {
        var whs = db.Warehouses.AsNoTracking().Where(w => w.IsActive).ToList();
        var ids = whs.Where(w => NormalizeWarehouseType(w.WarehouseType) == "Raw" || w.WarehouseCode == "WRM").Select(w => w.Id).ToList();
        return ids.Count > 0 ? ids : whs.Select(w => w.Id).ToList();
    }

}
