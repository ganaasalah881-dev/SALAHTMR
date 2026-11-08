using System.Data;
using DatesErp.Infrastructure.Connection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DatesErp.Infrastructure.Persistence;

/// <summary>
/// §الترحيل الآمن الشامل: يضمن أن قاعدة بيانات المستخدم (مهما كان عمرها وإصدارها)
/// تحتوي على كل الجداول وكل الأعمدة الموجودة في نموذج النظام الحالي — بلا حذف بيانات أبداً.
/// يُنفَّذ عند الإقلاع على SQLite وعلى SQL Server على حد سواء.
///
/// لماذا هذا ضروري: المستخدم يحدّث النظام بنسخ ملفات DLL فقط (لا مثبّت ولا حذف قاعدة)،
/// فأي جدول أو عمود أُضيف في إصدار لاحق يجب إنشاؤه/إلحاقه تلقائياً وإلا فشلت الشاشات
/// بأخطاء «عمود/جدول غير موجود» — وهذا بالضبط ما كان يُخفي العملاء ويعطل زر اختيار الأصناف.
/// </summary>
public static class SchemaMigrator
{
    /// <summary>ينفذ الترحيل ويعيد قائمة وصفية بما تم إنشاؤه/إضافته (أو الأخطاء إن وقعت).</summary>
    public static List<string> Migrate(DatesErpDbContext db)
    {
        IsolatedTestRun.RequireDatabaseVerified();
        var report = new List<string>();
        try
        {
            var conn = db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open) conn.Open();
            bool sqlite = !db.Database.IsSqlServer();

            foreach (var entity in db.Model.GetEntityTypes())
            {
                var table = entity.GetTableName();
                if (string.IsNullOrEmpty(table)) continue;
                var tableId = StoreObjectIdentifier.Table(table, entity.GetSchema());

                try
                {
                    if (!TableExists(conn, table, sqlite))
                    {
                        Exec(conn, BuildCreateTable(entity, table, tableId, sqlite));
                        report.Add($"تم إنشاء الجدول المفقود: {table}");
                        // §B110 — الجدول الجديد يُنشأ بفهارسه أيضاً (كان يُخلق بلا فهارس
                        // ولا قيود تكرار، فتتباين القواعد المُرحّلة عن الجديدة بنيوياً).
                        EnsureIndexes(conn, entity, table, tableId, sqlite, report);
                        continue; // جدول جديد — كل أعمدته موجودة بالتعريف
                    }

                    // جدول قائم: تأكد من وجود كل الأعمدة
                    var existing = GetColumns(conn, table, sqlite);
                    foreach (var prop in entity.GetProperties())
                    {
                        var col = prop.GetColumnName(tableId);
                        if (string.IsNullOrEmpty(col)) continue;
                        // §45 — عمود التزامن يُضاف بخطوة مخصصة (نوع rowversion له قواعد إضافة خاصة)
                        if (string.Equals(col, "RowVersion", StringComparison.OrdinalIgnoreCase)) continue;
                        if (existing.Contains(col, StringComparer.OrdinalIgnoreCase)) continue;

                        var type = prop.GetColumnType(tableId);
                        // SQLite يقبل «ADD COLUMN» بينما SQL Server يتطلب «ADD» بدون COLUMN
                        var addSql = sqlite
                            ? $"ALTER TABLE [{table}] ADD COLUMN [{col}] {type} NULL"
                            : $"ALTER TABLE [{table}] ADD [{col}] {type} NULL";
                        Exec(conn, addSql);
                        FillNulls(conn, table, col, prop);
                        report.Add($"تمت إضافة العمود المفقود: {table}.{col}");
                    }

                    // §B110 — استكمال الفهارس المفقودة على الجداول القائمة (من تعريف النموذج)
                    EnsureRowVersion(conn, table, sqlite, report);

                    if (!sqlite)
                    {
                        var schema = entity.GetSchema() ?? "dbo";
                        RepairDecimalColumnsForTable(conn, schema, table, entity, tableId, report);
                        AlignSqlServerColumnsForTable(conn, schema, table, entity, tableId, report);
                    }

                    // §إصلاح الأعطال الشاملة: تعبئة أي قيمة فارغة (NULL) في الأعمدة غير
                    // القابلة للفراغ — هذا يحل خطأ «The data is NULL at ordinal» نهائياً،
                    // سواء كان الفراغ من عمود أُضيف حديثاً أو من بيانات قديمة.
                    // §B110 — كل تعبئة تُعلن في التقرير بدل الصمت.
                    SweepNulls(conn, table, entity, tableId, report);
                    EnsureIndexes(conn, entity, table, tableId, sqlite, report);
                }
                catch (Exception exTable)
                {
                    report.Add($"خطأ في ترحيل {table}: {exTable.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            report.Add("خطأ عام في الترحيل: " + ex.Message);
        }

        // §تتبع الصنف: الربط الآلي لتعريفات التحويل الرسمية في القواعد القديمة
        LinkProductSources(db, report);
        // §نظام الوحدات: توحيد المجموعات (001 خام | 002 تام | 003 مخرجات ثانوية)
        NormalizeGroups(db, report);

        // §B87/M6: توحيد العميل الفارغ — الصفر القديم يعني «بلا عميل» فيُرحَّل إلى NULL
        NullifyZeroCustomers(db, report);

        // §ترحيل عدد القوالب ووزن القالب للبيانات السابقة من تعريف العبوة/الصنف بدل الصفر الصامت
        BackfillMoldsCount(db, report);

        // §المعالجة والتعقيم — الدفعات القائمة تُعتبر جاهزة (منع توقف الإنتاج عند الترقية)
        EnsureReceivingTreatmentIndex(db, report);
        BackfillTreatmentReadiness(db, report);

        // §فك قفل الحسابات تلقائياً — النظام بدون أي قفل آلي
        UnlockAllUsers(db);

        return report;
    }

    private static void BackfillMoldsCount(DatesErpDbContext db, List<string> report)
    {
        try
        {
            int fixedPacks = 0;
            foreach (var pk in db.PackagingTypes.Where(p => p.MoldsCount <= 0 && p.UnitWeightKg > 0 && p.MoldWeightKg > 0).ToList())
            {
                int derived = (int)Math.Round(pk.UnitWeightKg / pk.MoldWeightKg);
                if (derived > 0 && Math.Abs((derived * pk.MoldWeightKg) - pk.UnitWeightKg) <= 0.05)
                {
                    pk.MoldsCount = derived;
                    fixedPacks++;
                }
            }
            if (fixedPacks > 0) db.SaveChanges();

            var packMolds = db.PackagingTypes.AsNoTracking().Where(p => p.MoldsCount > 0).ToDictionary(p => p.Id, p => p.MoldsCount);
            var prodMolds = db.Products.AsNoTracking().Where(p => p.MoldsCount > 0).ToDictionary(p => p.Id, p => p.MoldsCount);
            var packMoldWeights = db.PackagingTypes.AsNoTracking().Where(p => p.MoldWeightKg > 0).ToDictionary(p => p.Id, p => (decimal)p.MoldWeightKg);
            var prodMoldWeights = db.Products.AsNoTracking().Where(p => p.MoldWeightKg > 0).ToDictionary(p => p.Id, p => (decimal)p.MoldWeightKg);
            int fixedItems = 0;
            foreach (var item in db.ProductionPlanItems.Where(i => i.MoldsCount <= 0 || i.MoldWeightKg <= 0).ToList())
            {
                bool touched = false;
                if (item.MoldsCount <= 0)
                {
                    int m = (item.PackagingTypeId != null && packMolds.TryGetValue(item.PackagingTypeId.Value, out var pm) && pm > 0)
                        ? pm
                        : (prodMolds.TryGetValue(item.ProductId, out var prm) ? prm : 0);
                    if (m > 0) { item.MoldsCount = m; touched = true; }
                }
                if (item.MoldWeightKg <= 0)
                {
                    decimal mw = (item.PackagingTypeId != null && packMoldWeights.TryGetValue(item.PackagingTypeId.Value, out var pmw) && pmw > 0)
                        ? pmw
                        : (prodMoldWeights.TryGetValue(item.ProductId, out var prmw) ? prmw : 0m);
                    if (mw > 0) { item.MoldWeightKg = mw; touched = true; }
                }
                if (touched) fixedItems++;
            }
            if (fixedItems > 0) db.SaveChanges();
            if (fixedPacks > 0 || fixedItems > 0)
                report.Add($"ترحيل القوالب: تم استكمال القوالب/الأوزان في {fixedPacks} عبوة و{fixedItems} بند خطة.");
        }
        catch (Exception ex)
        {
            report.Add("خطأ في ترحيل عدد القوالب: " + ex.Message);
        }
    }

    /// <summary>
    /// §المعالجة والتعقيم — ترحيل صريح ومسجَّل للمخزون القائم (قرار المستخدم س2).
    ///
    /// **لماذا هذا إلزامي؟** الدفعات الموجودة في القاعدة لم تمر بدورة معالجة قط، لأن
    /// الدورة لم تكن موجودة. لو تُركت بـ TreatmentReadyQtyKg = 0 ثم فُعّل اشتراط
    /// المعالجة (المرحلة 3)، لتوقف الإنتاج كله لحظة الترقية على مخزون قائم ومدفوع ثمنه.
    /// هذا فخ ترقية حقيقي، نظير ما عولج في طبقة الصلاحيات.
    ///
    /// **آمن للتكرار (idempotent):** يعالج الدفعات التي لم تُرحَّل ولم تدخل معالجة قط
    /// فقط. لا يلمس صفاً بدأت عليه دورة حقيقية، فلا يمحو عمل النظام بعد التشغيل.
    /// </summary>
    private static void EnsureReceivingTreatmentIndex(DatesErpDbContext db, List<string> report)
    {
        // الفهرس مطلوب أيضاً للقواعد القائمة؛ الترحيل العام يضيف الأعمدة لا الفهارس.
        using var check = db.Database.GetDbConnection().CreateCommand();
        check.CommandText = db.IsSqlServer
            ? "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_RawTreatments_ReceivingItemId' AND object_id = OBJECT_ID('RawTreatments')"
            : "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_RawTreatments_ReceivingItemId'";
        if (Convert.ToInt32(check.ExecuteScalar()) > 0) return;
        var sql = db.IsSqlServer
            ? "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_RawTreatments_ReceivingItemId' AND object_id = OBJECT_ID('RawTreatments')) CREATE UNIQUE INDEX [IX_RawTreatments_ReceivingItemId] ON [RawTreatments] ([ReceivingItemId]) WHERE [ReceivingItemId] IS NOT NULL"
            : "CREATE UNIQUE INDEX IF NOT EXISTS [IX_RawTreatments_ReceivingItemId] ON [RawTreatments] ([ReceivingItemId]) WHERE [ReceivingItemId] IS NOT NULL";
        db.Database.ExecuteSqlRaw(sql); // فشل قيد الأمان لا يُبتلع ولا يُعلن الترحيل ناجحاً.
        report.Add("تم إنشاء فهرس المعالجة الفريد لكل بند؛ البيانات السابقة بقيت بلا تحويل ضمني إلى نعم/لا.");
    }

    private static void BackfillTreatmentReadiness(DatesErpDbContext db, List<string> report)
    {
        try
        {
            // الشرط الثلاثي هو ضمان عدم اللمس: جاهز = 0، وتحت المعالجة = 0،
            // ولا توجد أي عملية معالجة مسجّلة على الدفعة.
            var treated = db.RawTreatments.Select(t => t.LotId).Distinct().ToHashSet();
            var pending = db.Lots
                .Where(l => l.TreatmentReadyQtyKg <= 0 && l.UnderTreatmentQtyKg <= 0 && l.InStockQtyKg > 0)
                .Where(l => !db.ShipmentItems.Any(i => i.Id == l.ShipmentItemId && i.TreatmentRequired != null))
                .ToList()
                .Where(l => !treated.Contains(l.Id))
                .ToList();

            if (pending.Count == 0) return;
            foreach (var lot in pending) lot.TreatmentReadyQtyKg = lot.InStockQtyKg;
            db.SaveChanges();
            report.Add($"ترحيل المعالجة: اعتُبرت {pending.Count} دفعة قائمة جاهزة للإنتاج "
                       + "(مخزون سابق لتفعيل دورة المعالجة — لم يُمسّ أي رصيد).");
        }
        catch (Exception ex)
        {
            // الترحيل لا يُسقط الإقلاع: النظام يعمل بسلوكه السابق ويُبلَّغ بالسبب
            report.Add("تعذّر ترحيل جاهزية المعالجة: " + ex.Message);
        }
    }

    /// <summary>
    /// §نظام الوحدات — المعيار الرسمي للمجموعات:
    /// 001 مواد خام | 002 منتجات تامة | 003 مخرجات ثانوية (وحداتها كجم).
    /// القواعد القديمة: المجموعة 003 كانت «مواد مساعدة» و004 «أصناف ثانوية» —
    /// تُرحَّل الأصناف الثانوية إلى 003، والمواد المساعدة كيان مستقل خارج مجموعات الأصناف.
    /// </summary>
    private static void NormalizeGroups(DatesErpDbContext db, List<string> report)
    {
        try
        {
            bool changed = false;

            // مجموعة 003 تصبح «المخرجات الثانوية»
            var g3 = db.ItemGroups.FirstOrDefault(g => g.GroupCode == "003");
            if (g3 != null && g3.GroupType != "ByProduct")
            {
                g3.GroupNameAr = "المخرجات الثانوية";
                g3.GroupType = "ByProduct";
                g3.DefaultUnit = "كجم";
                changed = true;
                report.Add("تم توحيد المجموعة 003 = المخرجات الثانوية (كجم)");
            }

            // مجموعة 004 (أصناف ثانوية قديمة) تُعلَّم كياناتها وتنقل أصنافها إلى 003
            var g4 = db.ItemGroups.FirstOrDefault(g => g.GroupCode == "004");
            if (g4 != null && g4.GroupType == "ByProduct")
            {
                g4.GroupNameAr = "أصناف ثانوية (قديمة — رُحلت إلى 003)";
                g4.IsActive = false;
                changed = true;
            }
            foreach (var p in db.Products.Where(p => p.GroupCode == "004"))
            {
                p.GroupCode = "003";
                if (p.ItemType != "ByProduct") p.ItemType = "ByProduct";
                if (p.UnitOfMeasure != "كجم") p.UnitOfMeasure = "كجم"; // §المخرجات الثانوية كجم دائماً
                changed = true;
            }

            // مطابقة المجموعة مع نوع الصنف للسلالات الثلاث
            foreach (var p in db.Products.Where(p => p.ItemType == "Raw" && (p.GroupCode == null || p.GroupCode == "")))
            { p.GroupCode = "001"; changed = true; }
            foreach (var p in db.Products.Where(p => p.ItemType == "Finished" && (p.GroupCode == null || p.GroupCode == "")))
            { p.GroupCode = "002"; changed = true; }
            foreach (var p in db.Products.Where(p => p.ItemType == "ByProduct" && (p.GroupCode == null || p.GroupCode == "" || p.GroupCode == "004")))
            { p.GroupCode = "003"; changed = true; }

            if (changed) db.SaveChanges();
        }
        catch (Exception ex) { report.Add("خطأ في توحيد المجموعات: " + ex.Message); }
    }

    /// <summary>
    /// §تتبع الصنف: كل منتج تام بلا تعريف مصدر يُربط آلياً بهوية الصنف الخام الذي تظهر
    /// كلمته (السلالة) في اسمه — «سكري فاخر 1كجم» ← «تمر خام - سكري».
    /// هذا يثبت علاقة الهوية فقط؛ لا يُنشئ معامل كمية. التخطيط يظل محتاجاً إلى
    /// ProductRawSource أو YieldFactor رسمي، ولا يجوز استنتاج معامل من اسم الصنف.
    /// </summary>
    private static void LinkProductSources(DatesErpDbContext db, List<string> report)
    {
        try
        {
            var raws = db.Products.AsNoTracking().Where(p => p.ItemType == "Raw" && p.IsActive).ToList();
            if (raws.Count == 0) return;
            bool changed = false;
            foreach (var fin in db.Products.Where(p => p.ItemType == "Finished" && p.SourceProductId == null).ToList())
            {
                var match = raws.Select(r => (r, token: RawToken(r.ProductNameAr)))
                    .Where(x => x.token.Length >= 2 && (fin.ProductNameAr ?? "").Contains(x.token))
                    .OrderByDescending(x => x.token.Length)
                    .Select(x => x.r)
                    .FirstOrDefault();
                if (match != null)
                {
                    fin.SourceProductId = match.Id;
                    changed = true;
                    report.Add($"تم ربط هوية مصدر الخام فقط: {fin.ProductNameAr} ← {match.ProductNameAr}؛ لا يُنشأ معامل كمية تلقائياً.");
                }
            }
            if (changed) db.SaveChanges();
        }
        catch (Exception ex) { report.Add("خطأ في ربط تحويلات الأصناف: " + ex.Message); }
    }

    /// <summary>كلمة السلالة في اسم الخام: «تمر خام - سكري» ← «سكري».</summary>
    private static string RawToken(string rawName)
    {
        var t = (rawName ?? "").Replace("تمر خام", "").Replace("مواد خام", "").Replace("خام", "");
        return t.Trim(' ', '-', '–', '—', ':', '.');
    }

    /// <summary>هل الجدول موجود؟</summary>
    public static bool TableExists(IDbConnection conn, string table, bool sqlite)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sqlite
            ? $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'"
            : $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='{table}'";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private static HashSet<string> GetColumns(IDbConnection conn, string table, bool sqlite)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sqlite
            ? $"SELECT name FROM pragma_table_info('{table}')"
            : $"SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='{table}'";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) set.Add(rd.GetString(0));
        return set;
    }

    /// <summary>بناء أمر CREATE TABLE لجدول مفقود من وصف نموذج EF مباشرة.</summary>
    private static string BuildCreateTable(IEntityType entity, string table, StoreObjectIdentifier tableId, bool sqlite)
    {
        var pk = entity.FindPrimaryKey();
        var colDefs = new List<string>();
        foreach (var prop in entity.GetProperties())
        {
            var col = prop.GetColumnName(tableId);
            var type = prop.GetColumnType(tableId);
            bool isPk = pk?.Properties.Any(p => p.Name == prop.Name) == true;
            bool autoInt = isPk && pk!.Properties.Count == 1
                           && prop.ClrType == typeof(int)
                           && prop.ValueGenerated == ValueGenerated.OnAdd;

            string def = $"[{col}] {type}";
            if (sqlite && autoInt)
                def += " PRIMARY KEY AUTOINCREMENT";          // SQLite: مفتاح تلقائي
            else if (!sqlite && autoInt)
                def += " IDENTITY(1,1) NOT NULL";              // SQL Server: عدّاد تلقائي
            else if (!prop.IsNullable)
                def += " NOT NULL";
            colDefs.Add(def);
        }
        // مفتاح مركّب (مثل UserRoles / RolePermissions)
        if (pk != null && pk.Properties.Count > 1)
            colDefs.Add($"CONSTRAINT [PK_{table}] PRIMARY KEY (" +
                        string.Join(", ", pk.Properties.Select(p => $"[{p.GetColumnName(tableId)}]")) + ")");
        else if (pk != null && pk.Properties.Count == 1)
        {
            var kp = pk.Properties[0];
            bool autoInt = kp.ClrType == typeof(int) && kp.ValueGenerated == ValueGenerated.OnAdd;
            if (sqlite && autoInt) { /* عولج داخل تعريف العمود */ }
            else if (!sqlite && autoInt) colDefs.Add($"CONSTRAINT [PK_{table}] PRIMARY KEY ([{kp.GetColumnName(tableId)}])");
            else colDefs.Add($"CONSTRAINT [PK_{table}] PRIMARY KEY ([{kp.GetColumnName(tableId)}])");
        }
        return $"CREATE TABLE [{table}] ({string.Join(", ", colDefs)})";
    }

    /// <summary>قيمة افتراضية لعمود غير قابل للفراغ — أو null إن لم يكن له افتراضي معروف.</summary>
    private static string DefaultFill(IProperty prop)
    {
        // §الحالة النشطة افتراضياً: أعمدة IsActive الفارغة تُملأ «نشط» (1) — لا «موقوف»،
        // حتى لا تختفي السجلات (عملاء/أصناف/موردون...) من القوائم المنسدلة
        if (prop.Name == "IsActive") return "1";

        var t = Nullable.GetUnderlyingType(prop.ClrType) ?? prop.ClrType;
        if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
            || t == typeof(double) || t == typeof(float) || t == typeof(decimal)) return "0";
        if (t == typeof(bool)) return "0";
        if (t == typeof(string)) return "''";
        if (t == typeof(DateTime)) return "'2000-01-01'";
        if (t.IsEnum) return "0";
        return null;
    }

    /// <summary>§45 — يضمن عمود RowVersion غير القابل للفراغ على المزودين.</summary>
    private static void EnsureRowVersion(IDbConnection conn, string table, bool sqlite, List<string> report)
    {
        var cols = GetColumns(conn, table, sqlite);
        if (sqlite)
        {
            // ALTER TABLE في SQLite لا يقبل إضافة BLOB NOT NULL بلا قيمة افتراضية
            // عند وجود بيانات. قيمة ثابتة صالحة للسجلات القديمة؛ كل INSERT/UPDATE عبر
            // EF يحصل على رمز جديد من DatesErpDbContext، ولا تُخفّف قيمة العمود أو قيده.
            const string legacyToken = "X'00000000000000000000000000000000'";
            if (!cols.Contains("RowVersion", StringComparer.OrdinalIgnoreCase))
            {
                Exec(conn, $"ALTER TABLE [{table}] ADD COLUMN [RowVersion] BLOB NOT NULL DEFAULT {legacyToken}");
                report.Add($"تمت إضافة عمود تزامن SQLite غير قابل للفراغ: {table}.RowVersion");
            }
            else
            {
                // لا ننفذ UPDATE فارغاً على كل جدول عند كل إقلاع؛ نصلح فقط الصفوف القديمة
                // التي تفتقد الرمز فعلاً، وبذلك لا نأخذ قفل كتابة غير لازم.
                using var probe = conn.CreateCommand();
                probe.CommandText = $"SELECT 1 FROM [{table}] WHERE [RowVersion] IS NULL OR length([RowVersion]) = 0 LIMIT 1";
                if (probe.ExecuteScalar() != null)
                {
                    int repaired = Exec(conn,
                        $"UPDATE [{table}] SET [RowVersion] = {legacyToken} WHERE [RowVersion] IS NULL OR length([RowVersion]) = 0");
                    if (repaired > 0)
                        report.Add($"تمت تهيئة {repaired} رمز تزامن فارغ في {table}.RowVersion");
                }
            }
            return;
        }

        if (cols.Contains("RowVersion", StringComparer.OrdinalIgnoreCase)) return;
        // SQL Server rowversion يولّد بايتات غير فارغة للسجلات القائمة والجديدة.
        // لا يوجد مسار احتياطي ROWVERSION NULL: فشل DDL يُبلّغ عنه كفشل ترحيل
        // بدلاً من إضعاف شرط التزامن أو ترك قاعدة مختلفة بصمت.
        Exec(conn, $"ALTER TABLE [{table}] ADD [RowVersion] ROWVERSION");
        report.Add($"تمت إضافة عمود التزامن المطلوب من SQL Server: {table}.RowVersion");
    }

    private static void FillNulls(IDbConnection conn, string table, string col, IProperty prop)
    {
        if (prop.IsNullable) return;
        var fill = DefaultFill(prop);
        if (fill == null) return;
        try { Exec(conn, $"UPDATE [{table}] SET [{col}] = {fill} WHERE [{col}] IS NULL"); }
        catch { /* الأفضل ألا يعطل الترحيل */ }
    }

    /// <summary>
    /// §إصلاح الأعطال الشاملة: يمسح كل جدول ويملأ أي قيمة فارغة (NULL) في أي عمود غير
    /// قابل للفراغ — بصرف النظر إن كان العمود مضافاً حديثاً أو قديماً وفيه بيانات فارغة.
    /// هذا يحل نهائياً خطأ: «The data is NULL at ordinal ... can't be called on NULL values».
    /// </summary>
    private static void SweepNulls(IDbConnection conn, string table, IEntityType entity, StoreObjectIdentifier tableId, List<string> report)
    {
        foreach (var prop in entity.GetProperties())
        {
            if (prop.IsNullable) continue; // القابل للفراغ يتحمله EF بلا مشكلة
            var col = prop.GetColumnName(tableId);
            if (string.IsNullOrEmpty(col)) continue;
            var fill = DefaultFill(prop);
            if (fill == null) continue;
            try
            {
                // §B110 — التعبئة لم تعد صامتة: عدد الصفوف المعبأة يُعلن في تقرير الترحيل
                var filled = Exec(conn, $"UPDATE [{table}] SET [{col}] = {fill} WHERE [{col}] IS NULL");
                if (filled > 0)
                    report.Add($"§تعبئة تلقائية: مُلئت {filled} قيمة فارغة بالافتراضي في {table}.{col}");
            }
            catch { /* لا نوقف الإقلاع على عمود واحد */ }
        }
    }

    /// <summary>
    /// §B110 — يضمن وجود كل فهارس النموذج على الجدول (فريدة/عادية/مصفّاة).
    /// قبل هذا الإصلاح كانت الجداول المُنشأة بالترحيل تنقصها الفهارس كلها:
    /// بطء في المسارات الساخنة وغياب حماية التكرار التي تعتمد عليها أرقام المستندات.
    /// الفحص بالاسم ثم بتوقيع الأعمدة (على SQLite) يمنع إنشاء فهرس مكرر باسم مختلف.
    /// </summary>
    private static void EnsureIndexes(IDbConnection conn, IEntityType entity, string table,
        StoreObjectIdentifier tableId, bool sqlite, List<string> report)
    {
        foreach (var idx in entity.GetIndexes())
        {
            var name = idx.GetDatabaseName();
            var cols = idx.Properties
                .Select(p => p.GetColumnName(tableId))
                .Where(c => !string.IsNullOrEmpty(c))
                .Select(c => c!)
                .ToList();
            if (string.IsNullOrEmpty(name) || cols.Count != idx.Properties.Count) continue;
            try
            {
                if (IndexExists(conn, table, name, cols, sqlite)) continue;
                var unique = idx.IsUnique ? "UNIQUE " : "";
                var colList = string.Join(", ", cols.Select(c => $"[{c}]"));
                var filter = idx.GetFilter();
                var where = string.IsNullOrEmpty(filter) ? "" : $" WHERE {filter}";
                Exec(conn, $"CREATE {unique}INDEX [{name}] ON [{table}] ({colList}){where}");
                report.Add($"تم إنشاء الفهرس المفقود: {table}.{name}");
            }
            catch (Exception exIdx)
            {
                report.Add($"خطأ في إنشاء فهرس {table}.{name}: {exIdx.Message}");
            }
        }
    }

    /// <summary>هل الفهرس موجود؟ بالاسم على المزودين، وبتوقيع الأعمدة على SQLite لمنع التكرار.</summary>
    private static bool IndexExists(IDbConnection conn, string table, string name, List<string> cols, bool sqlite)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = sqlite
                ? $"SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND tbl_name='{table}' AND name='{name}'"
                : $"SELECT COUNT(*) FROM sys.indexes WHERE name='{name}' AND object_id = OBJECT_ID('{table}')";
            if (Convert.ToInt32(cmd.ExecuteScalar()) > 0) return true;
        }

        if (!sqlite) return false; // على SQL Server يكفي الاسم؛ تكرار بأسماء مختلفة أثره أداء لا صحة

        var indexNames = new List<string>();
        using (var list = conn.CreateCommand())
        {
            list.CommandText = $"SELECT name FROM pragma_index_list('{table}')";
            using var rd = list.ExecuteReader();
            while (rd.Read()) indexNames.Add(rd.GetString(0));
        }
        foreach (var idxName in indexNames)
        {
            var indexCols = new List<string>();
            using var info = conn.CreateCommand();
            info.CommandText = $"SELECT name FROM pragma_index_info('{idxName.Replace("'", "''")}') ORDER BY seqno";
            using var rd2 = info.ExecuteReader();
            while (rd2.Read()) indexCols.Add(rd2.GetString(0));
            if (indexCols.SequenceEqual(cols, StringComparer.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private sealed record SqlColumnMeta(
        string Name,
        string TypeName,
        short MaxLength,
        byte Precision,
        byte Scale,
        bool IsNullable,
        bool IsIdentity,
        bool IsComputed,
        string CollationName,
        string DefaultConstraintName,
        bool IsPrimaryKey,
        bool HasForeignKey);

    private static string QuoteBracket(string identifier)
        => "[" + (identifier ?? "").Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static string EscapeSqlLiteral(string text)
        => (text ?? "").Replace("'", "''", StringComparison.Ordinal);

    private static string SanitizeSqlId(string text)
    {
        var chars = (text ?? "").Where(ch => char.IsLetterOrDigit(ch) || ch == '_').ToArray();
        var clean = new string(chars);
        return clean.Length > 45 ? clean[..45] : (clean.Length == 0 ? "Col" : clean);
    }

    public static string BuildSqlServerTypeDefinition(
        string typeName, short maxLength, byte precision, byte scale, string collationName = null)
    {
        var t = (typeName ?? "").Trim().ToLowerInvariant();
        string coll = !string.IsNullOrWhiteSpace(collationName) ? $" COLLATE {collationName}" : "";
        return t switch
        {
            "nvarchar" or "nchar" => $"{t}({(maxLength == -1 ? "max" : Math.Max(1, maxLength / 2).ToString())}){coll}",
            "varchar" or "char" => $"{t}({(maxLength == -1 ? "max" : Math.Max(1, (int)maxLength).ToString())}){coll}",
            "varbinary" or "binary" => $"{t}({(maxLength == -1 ? "max" : Math.Max(1, (int)maxLength).ToString())})",
            "decimal" or "numeric" => $"{t}({(precision > 0 ? precision : 18)},{(scale <= (precision > 0 ? precision : 18) ? scale : 0)})",
            "datetime2" or "datetimeoffset" or "time" => $"{t}({scale})",
            "ntext" => "nvarchar(max)",
            "text" => "varchar(max)",
            "image" => "varbinary(max)",
            _ => t
        };
    }

    public static string SafeSqlDefaultLiteral(string typeName) => (typeName ?? "").Trim().ToLowerInvariant() switch
    {
        "int" or "bigint" or "smallint" or "tinyint" or "bit"
            or "decimal" or "numeric" or "float" or "real" or "money" or "smallmoney" => "0",
        "nvarchar" or "nchar" or "ntext" => "N''",
        "varchar" or "char" or "text" or "xml" => "''",
        "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" => "'2000-01-01'",
        "time" => "'00:00:00'",
        "uniqueidentifier" => "NEWID()",
        "varbinary" or "binary" or "image" => "0x",
        _ => null
    };

    private static void DropDefaultConstraintIfExists(IDbConnection conn, string schema, string table, string colName)
    {
        string dcName = null;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $@"
SELECT TOP 1 dc.name
FROM sys.default_constraints dc
JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
JOIN sys.tables tb ON tb.object_id = c.object_id
JOIN sys.schemas sc ON sc.schema_id = tb.schema_id
WHERE sc.name = N'{EscapeSqlLiteral(schema)}'
  AND tb.name = N'{EscapeSqlLiteral(table)}'
  AND c.name = N'{EscapeSqlLiteral(colName)}'";
            dcName = cmd.ExecuteScalar() as string;
        }
        if (!string.IsNullOrEmpty(dcName))
            Exec(conn, $"ALTER TABLE {QuoteBracket(schema)}.{QuoteBracket(table)} DROP CONSTRAINT {QuoteBracket(dcName)}");
    }

    private static void DropDependentNonPkIndexes(IDbConnection conn, string schema, string table, string colName)
    {
        var items = new List<(string Name, bool IsUniqueConstraint)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $@"
SELECT DISTINCT ix.name, ix.is_unique_constraint
FROM sys.indexes ix
JOIN sys.index_columns ic ON ic.object_id = ix.object_id AND ic.index_id = ix.index_id
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
JOIN sys.tables tb ON tb.object_id = ix.object_id
JOIN sys.schemas sc ON sc.schema_id = tb.schema_id
WHERE sc.name = N'{EscapeSqlLiteral(schema)}'
  AND tb.name = N'{EscapeSqlLiteral(table)}'
  AND c.name = N'{EscapeSqlLiteral(colName)}'
  AND ix.is_primary_key = 0
  AND ix.name IS NOT NULL
  AND NOT EXISTS (
      SELECT 1 FROM sys.foreign_keys fk
      WHERE fk.referenced_object_id = ix.object_id AND fk.key_index_id = ix.index_id
  )";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                items.Add((rd.GetString(0), rd.GetBoolean(1)));
        }

        foreach (var (ixName, isUqConstraint) in items)
        {
            if (isUqConstraint)
                Exec(conn, $"ALTER TABLE {QuoteBracket(schema)}.{QuoteBracket(table)} DROP CONSTRAINT {QuoteBracket(ixName)}");
            else
                Exec(conn, $"DROP INDEX {QuoteBracket(ixName)} ON {QuoteBracket(schema)}.{QuoteBracket(table)}");
        }
    }

    /// <summary>
    /// §B110 + v1.50.76 — تصحيح أعمدة decimal على SQL Server التي خُزنت نصاً أو float في إصدارات سابقة
    /// مع فك القيود/الفهارس المانعة قبل التحويل وضمان عدم بقاء NULL في الأعمدة غير القابلة للفراغ.
    /// </summary>
    private static void RepairDecimalColumnsForTable(
        IDbConnection conn, string schema, string table, IEntityType entity,
        StoreObjectIdentifier tableId, List<string> report)
    {
        var qSchema = QuoteBracket(schema);
        var qTable = QuoteBracket(table);

        foreach (var prop in entity.GetProperties())
        {
            var underlying = Nullable.GetUnderlyingType(prop.ClrType) ?? prop.ClrType;
            if (underlying != typeof(decimal)) continue;
            var col = prop.GetColumnName(tableId);
            if (string.IsNullOrEmpty(col)) continue;

            try
            {
                string actualType;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $@"
SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = N'{EscapeSqlLiteral(schema)}'
  AND TABLE_NAME = N'{EscapeSqlLiteral(table)}'
  AND COLUMN_NAME = N'{EscapeSqlLiteral(col)}'";
                    actualType = cmd.ExecuteScalar() as string;
                }
                if (actualType == null
                    || actualType.StartsWith("decimal", StringComparison.OrdinalIgnoreCase)
                    || actualType.StartsWith("numeric", StringComparison.OrdinalIgnoreCase))
                    continue;

                int prec = prop.GetPrecision() ?? 18;
                int scale = prop.GetScale() ?? 4;
                var qCol = QuoteBracket(col);

                DropDefaultConstraintIfExists(conn, schema, table, col);
                DropDependentNonPkIndexes(conn, schema, table, col);

                bool isNumericSource = actualType.Equals("float", StringComparison.OrdinalIgnoreCase)
                    || actualType.Equals("real", StringComparison.OrdinalIgnoreCase)
                    || actualType.Equals("int", StringComparison.OrdinalIgnoreCase)
                    || actualType.Equals("bigint", StringComparison.OrdinalIgnoreCase)
                    || actualType.Equals("smallint", StringComparison.OrdinalIgnoreCase)
                    || actualType.Equals("tinyint", StringComparison.OrdinalIgnoreCase)
                    || actualType.Equals("money", StringComparison.OrdinalIgnoreCase)
                    || actualType.Equals("smallmoney", StringComparison.OrdinalIgnoreCase);

                if (isNumericSource)
                {
                    if (!prop.IsNullable)
                        Exec(conn, $"UPDATE {qSchema}.{qTable} SET {qCol} = 0 WHERE {qCol} IS NULL");
                    Exec(conn, $"ALTER TABLE {qSchema}.{qTable} ALTER COLUMN {qCol} decimal({prec},{scale}) {(prop.IsNullable ? "NULL" : "NOT NULL")}");
                }
                else
                {
                    var tmp = col + "_b110_dec";
                    var qTmp = QuoteBracket(tmp);
                    Exec(conn, $"ALTER TABLE {qSchema}.{qTable} ADD {qTmp} decimal({prec},{scale}) NULL");
                    Exec(conn, $"UPDATE {qSchema}.{qTable} SET {qTmp} = TRY_CAST(NULLIF(LTRIM(RTRIM(CAST({qCol} AS nvarchar(max)))), '') AS decimal({prec},{scale})) WHERE {qCol} IS NOT NULL");
                    Exec(conn, $"ALTER TABLE {qSchema}.{qTable} DROP COLUMN {qCol}");
                    Exec(conn, $"EXEC sp_rename N'{EscapeSqlLiteral($"{schema}.{table}.{tmp}")}', N'{EscapeSqlLiteral(col)}', N'COLUMN'");
                    if (!prop.IsNullable)
                    {
                        Exec(conn, $"UPDATE {qSchema}.{qTable} SET {qCol} = 0 WHERE {qCol} IS NULL");
                        Exec(conn, $"ALTER TABLE {qSchema}.{qTable} ALTER COLUMN {qCol} decimal({prec},{scale}) NOT NULL");
                    }
                }
                report.Add($"توافق نوع عشري: حُوّل العمود {table}.{col} من {actualType} إلى decimal({prec},{scale}).");
            }
            catch (Exception exCol)
            {
                report.Add($"خطأ في تصحيح {table}.{col} إلى decimal: {exCol.Message}");
            }
        }
    }

    /// <summary>
    /// §v1.50.76 — مواءمة شاملة لأعمدة SQL Server القائمة مع نموذج EF الحالي بلا فقد بيانات:
    /// 1) أي عمود تاريخي غير موجود في النموذج الحالي ومُعرَّف كـ NOT NULL في القاعدة القديمة
    ///    يُوسَّع إلى NULL (أو يُعطى افتراضياً آمناً عند وجود قيد يمنع التوسيع) حتى لا يفشل INSERT.
    /// 2) أي عمود موجود في النموذج الحالي كقابل للفراغ (prop.IsNullable) وما زال NOT NULL
    ///    في القاعدة القديمة يُوسَّع إلى NULL حتى لا تفشل القيم الفارغة الشرعية.
    /// </summary>
    private static void AlignSqlServerColumnsForTable(
        IDbConnection conn, string schema, string table, IEntityType entity,
        StoreObjectIdentifier tableId, List<string> report)
    {
        var mappedProps = new Dictionary<string, IProperty>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in entity.GetProperties())
        {
            var colName = p.GetColumnName(tableId);
            if (!string.IsNullOrEmpty(colName))
                mappedProps[colName] = p;
        }

        var dbCols = new List<SqlColumnMeta>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $@"
SELECT
    c.name AS ColName,
    t.name AS TypeName,
    c.max_length AS MaxLength,
    c.precision AS Precision,
    c.scale AS Scale,
    c.is_nullable AS IsNullable,
    c.is_identity AS IsIdentity,
    c.is_computed AS IsComputed,
    c.collation_name AS CollationName,
    dc.name AS DefaultConstraintName,
    CAST(CASE WHEN EXISTS (
        SELECT 1 FROM sys.index_columns ic
        JOIN sys.indexes ix ON ix.object_id = ic.object_id AND ix.index_id = ic.index_id
        WHERE ic.object_id = c.object_id AND ic.column_id = c.column_id AND ix.is_primary_key = 1
    ) THEN 1 ELSE 0 END AS bit) AS IsPrimaryKey,
    CAST(CASE WHEN EXISTS (
        SELECT 1 FROM sys.foreign_key_columns fkc
        WHERE fkc.parent_object_id = c.object_id AND fkc.parent_column_id = c.column_id
    ) THEN 1 ELSE 0 END AS bit) AS HasForeignKey
FROM sys.columns c
JOIN sys.tables tb ON tb.object_id = c.object_id
JOIN sys.schemas sc ON sc.schema_id = tb.schema_id
JOIN sys.types t ON t.user_type_id = c.system_type_id
LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
WHERE sc.name = N'{EscapeSqlLiteral(schema)}'
  AND tb.name = N'{EscapeSqlLiteral(table)}'";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                dbCols.Add(new SqlColumnMeta(
                    Name: rd.GetString(0),
                    TypeName: rd.GetString(1),
                    MaxLength: Convert.ToInt16(rd.GetValue(2)),
                    Precision: Convert.ToByte(rd.GetValue(3)),
                    Scale: Convert.ToByte(rd.GetValue(4)),
                    IsNullable: rd.GetBoolean(5),
                    IsIdentity: rd.GetBoolean(6),
                    IsComputed: rd.GetBoolean(7),
                    CollationName: rd.IsDBNull(8) ? null : rd.GetString(8),
                    DefaultConstraintName: rd.IsDBNull(9) ? null : rd.GetString(9),
                    IsPrimaryKey: rd.GetBoolean(10),
                    HasForeignKey: rd.GetBoolean(11)));
            }
        }

        var qSchema = QuoteBracket(schema);
        var qTable = QuoteBracket(table);

        foreach (var col in dbCols)
        {
            if (col.IsIdentity || col.IsComputed || col.IsPrimaryKey) continue;
            if (string.Equals(col.Name, "RowVersion", StringComparison.OrdinalIgnoreCase)) continue;
            if (col.TypeName.Equals("timestamp", StringComparison.OrdinalIgnoreCase)
                || col.TypeName.Equals("rowversion", StringComparison.OrdinalIgnoreCase)) continue;

            var sqlType = BuildSqlServerTypeDefinition(col.TypeName, col.MaxLength, col.Precision, col.Scale, col.CollationName);
            var qCol = QuoteBracket(col.Name);

            if (!mappedProps.TryGetValue(col.Name, out var prop))
            {
                // عمود تاريخي غير مستخدم في النموذج الحالي
                if (!col.IsNullable)
                {
                    try
                    {
                        DropDependentNonPkIndexes(conn, schema, table, col.Name);
                        Exec(conn, $"ALTER TABLE {qSchema}.{qTable} ALTER COLUMN {qCol} {sqlType} NULL");
                        if (col.HasForeignKey && !string.IsNullOrEmpty(col.DefaultConstraintName))
                            DropDefaultConstraintIfExists(conn, schema, table, col.Name);
                        report.Add($"توافق مخطط قديم: جُعل العمود التاريخي {table}.{col.Name} قابلاً للفراغ.");
                    }
                    catch
                    {
                        if (string.IsNullOrEmpty(col.DefaultConstraintName) && !col.HasForeignKey)
                        {
                            var defLit = SafeSqlDefaultLiteral(col.TypeName);
                            if (defLit != null)
                            {
                                try
                                {
                                    var dfName = $"DF_Legacy_{SanitizeSqlId(table)}_{SanitizeSqlId(col.Name)}";
                                    Exec(conn, $"ALTER TABLE {qSchema}.{qTable} ADD CONSTRAINT {QuoteBracket(dfName)} DEFAULT ({defLit}) FOR {qCol}");
                                    report.Add($"توافق مخطط قديم: أُضيف افتراضي آمن للعمود التاريخي {table}.{col.Name}.");
                                }
                                catch { }
                            }
                        }
                    }
                }
                else if (col.HasForeignKey && !string.IsNullOrEmpty(col.DefaultConstraintName))
                {
                    try { DropDefaultConstraintIfExists(conn, schema, table, col.Name); } catch { }
                }
                continue;
            }

            // عمود موجود في النموذج الحالي كـ double (SQL float) لكنه أنشئ قديماً بنوع رقمي آخر (int/decimal/real)
            var clr = Nullable.GetUnderlyingType(prop.ClrType) ?? prop.ClrType;
            if (clr == typeof(double)
                && !col.TypeName.Equals("float", StringComparison.OrdinalIgnoreCase)
                && (col.TypeName.Equals("int", StringComparison.OrdinalIgnoreCase)
                    || col.TypeName.Equals("bigint", StringComparison.OrdinalIgnoreCase)
                    || col.TypeName.Equals("smallint", StringComparison.OrdinalIgnoreCase)
                    || col.TypeName.Equals("tinyint", StringComparison.OrdinalIgnoreCase)
                    || col.TypeName.Equals("decimal", StringComparison.OrdinalIgnoreCase)
                    || col.TypeName.Equals("numeric", StringComparison.OrdinalIgnoreCase)
                    || col.TypeName.Equals("real", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    DropDefaultConstraintIfExists(conn, schema, table, col.Name);
                    DropDependentNonPkIndexes(conn, schema, table, col.Name);
                    if (!prop.IsNullable)
                        Exec(conn, $"UPDATE {qSchema}.{qTable} SET {qCol} = 0 WHERE {qCol} IS NULL");
                    Exec(conn, $"ALTER TABLE {qSchema}.{qTable} ALTER COLUMN {qCol} float {(prop.IsNullable ? "NULL" : "NOT NULL")}");
                    report.Add($"توافق نوع رقمي: حُوّل العمود {table}.{col.Name} من {col.TypeName} إلى float.");
                    continue;
                }
                catch (Exception exDbl)
                {
                    report.Add($"خطأ في تحويل {table}.{col.Name} إلى float: {exDbl.Message}");
                }
            }

            // عمود موجود في النموذج الحالي كـ int لكنه أنشئ قديماً بنوع رقمي مختلف (float/decimal/smallint)
            if (clr == typeof(int) && !prop.ClrType.IsEnum
                && !col.TypeName.Equals("int", StringComparison.OrdinalIgnoreCase)
                && (col.TypeName.Equals("smallint", StringComparison.OrdinalIgnoreCase)
                    || col.TypeName.Equals("tinyint", StringComparison.OrdinalIgnoreCase)
                    || col.TypeName.Equals("float", StringComparison.OrdinalIgnoreCase)
                    || col.TypeName.Equals("real", StringComparison.OrdinalIgnoreCase)
                    || col.TypeName.Equals("decimal", StringComparison.OrdinalIgnoreCase)
                    || col.TypeName.Equals("numeric", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    DropDefaultConstraintIfExists(conn, schema, table, col.Name);
                    DropDependentNonPkIndexes(conn, schema, table, col.Name);
                    if (col.TypeName.Equals("float", StringComparison.OrdinalIgnoreCase)
                        || col.TypeName.Equals("real", StringComparison.OrdinalIgnoreCase)
                        || col.TypeName.Equals("decimal", StringComparison.OrdinalIgnoreCase)
                        || col.TypeName.Equals("numeric", StringComparison.OrdinalIgnoreCase))
                        Exec(conn, $"UPDATE {qSchema}.{qTable} SET {qCol} = ROUND(ISNULL({qCol}, 0), 0)");
                    else if (!prop.IsNullable)
                        Exec(conn, $"UPDATE {qSchema}.{qTable} SET {qCol} = 0 WHERE {qCol} IS NULL");
                    Exec(conn, $"ALTER TABLE {qSchema}.{qTable} ALTER COLUMN {qCol} int {(prop.IsNullable ? "NULL" : "NOT NULL")}");
                    report.Add($"توافق نوع رقمي: حُوّل العمود {table}.{col.Name} من {col.TypeName} إلى int.");
                    continue;
                }
                catch { }
            }

            // توسيع أعمدة الملاحظات ولقطات JSON المحدودة الطول قديماً إلى nvarchar(max) لمنع خطأ Truncation
            if (clr == typeof(string)
                && col.MaxLength > 0 && col.MaxLength < 4000
                && (col.Name.EndsWith("Notes", StringComparison.OrdinalIgnoreCase)
                    || col.Name.EndsWith("Json", StringComparison.OrdinalIgnoreCase)
                    || col.Name.Equals("OldValue", StringComparison.OrdinalIgnoreCase)
                    || col.Name.Equals("NewValue", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var colClause = !string.IsNullOrWhiteSpace(col.CollationName) ? $" COLLATE {col.CollationName}" : "";
                    Exec(conn, $"ALTER TABLE {qSchema}.{qTable} ALTER COLUMN {qCol} nvarchar(max){colClause} NULL");
                    report.Add($"توافق مخطط قديم: وُسّع العمود النصي {table}.{col.Name} إلى nvarchar(max).");
                    continue;
                }
                catch { }
            }

            // عمود موجود في النموذج الحالي كقابل للفراغ لكنه NOT NULL في القاعدة القديمة
            if (prop.IsNullable && !col.IsNullable)
            {
                try
                {
                    DropDependentNonPkIndexes(conn, schema, table, col.Name);
                    Exec(conn, $"ALTER TABLE {qSchema}.{qTable} ALTER COLUMN {qCol} {sqlType} NULL");
                    report.Add($"توافق مخطط قديم: وُسّع العمود {table}.{col.Name} ليقبل الفراغ (NULL) وفق النموذج الحالي.");
                }
                catch (Exception exNull)
                {
                    report.Add($"خطأ في توسيع قابلية الفراغ للعمود {table}.{col.Name}: {exNull.Message}");
                }
            }
        }
    }

    /// <summary>
    /// §B87/M6: «بدون عميل» كانت تُخزَّن صفراً في بعض المسارات القديمة — تُرحَّل إلى NULL
    /// (الصفر ليس عميلاً حقيقياً ويكسر التقارير والتجميع). آمنة تكرارياً: الصفر فقط يتحول.
    /// </summary>
    private static void NullifyZeroCustomers(DatesErpDbContext db, List<string> report)
    {
        try
        {
            int n = 0;
            foreach (var i in db.ProductionPlanItems.Where(i => i.CustomerId == 0)) { i.CustomerId = null; n++; }
            foreach (var i in db.ProductionOrderItems.Where(i => i.CustomerId == 0)) { i.CustomerId = null; n++; }
            foreach (var o in db.ProductionOrders.Where(o => o.CustomerId == 0)) { o.CustomerId = null; n++; }
            foreach (var l in db.Lots.Where(l => l.CustomerId == 0)) { l.CustomerId = null; n++; }
            if (n > 0) { db.SaveChanges(); report.Add($"تم ترحيل {n} سجلاً من عميل-صفر إلى «بدون عميل» (NULL)"); }
        }
        catch (Exception ex) { report.Add("خطأ في ترحيل العملاء الأصفار: " + ex.Message); }
    }

    private static int Exec(IDbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    private static void UnlockAllUsers(DatesErpDbContext db)
    {
        try
        {
            var locked = db.Users.Where(u => u.IsLocked || u.FailedLoginCount > 0 || u.LockoutDate != null).ToList();
            if (locked.Count > 0)
            {
                foreach (var u in locked)
                {
                    u.IsLocked = false;
                    u.FailedLoginCount = 0;
                    u.LockoutDate = null;
                }
                db.SaveChanges();
            }
        }
        catch { }
    }
}
