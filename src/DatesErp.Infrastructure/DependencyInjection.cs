using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Connection;
using DatesErp.Infrastructure.Persistence;
using DatesErp.Infrastructure.Session;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Infrastructure;

/// <summary>تركيب طبقة البنية التحتية في حاوية الخدمات.</summary>
public static class DependencyInjection
{
    /// <summary>تسجيل سياق قاعدة البيانات حسب الإعداد: SQL Server مركزي أو SQLite (اختبارات).</summary>
    public static IServiceCollection AddDatesErpInfrastructure(this IServiceCollection services, Action<DbContextOptionsBuilder> configure = null)
    {
        services.AddSingleton<SessionContext>();
        services.AddSingleton<ICurrentSession>(sp => sp.GetRequiredService<SessionContext>());
        services.AddSingleton<AuditSaveChangesInterceptor>();
        services.AddSingleton<ConnectionTester>();
        // §مهم: ترقيم المستندات يجب أن يكون Scoped حتى تُحفظ زيادة التسلسل مع معاملة المستند نفسها
        // (كان Singleton من قبل فلا تُحفظ الزيادة أبداً ← أرقام مكررة ← UNIQUE constraint)
        services.AddScoped<INumberingService, NumberingService>();

        services.AddDbContext<DatesErpDbContext>((sp, options) =>
        {
            options.AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>());
            if (IsolatedTestRun.IsEnabled)
            {
                if (configure != null)
                    throw new InvalidOperationException(
                        "لا يُسمح بتجاوز وجهة قاعدة البيانات الثابتة في وضع الاختبار المعزول.");
                options.UseSqlServer(IsolatedTestRun.BuildConnectionString());
                return;
            }

            if (configure != null)
            {
                configure(options);
                return;
            }

            var cfg = AppConfig.Load();
            if (cfg != null) options.UseSqlServer(cfg.BuildSqlServerConnectionString());
            else options.UseSqlite("Data Source=dateerp_dev.db");
        });

        return services;
    }
}

/// <summary>
/// ترقيم المستندات المركزي — تسلسل آمن داخل معاملة.
/// §الحماية من التكرار (طبقتان):
///  • يُصلح المخططات المفقودة تلقائياً (ينشئ المخطط إن لم يوجد).
///  • حلقة ضمان: يتقدّم حتى رقم غير مستخدم — ويقرأ كل مخطط من جدول مصدره، بما فيه
///    TRT من RawTreatments، كي يتعافى من أي عدّاد متأخر.
/// §B84/C1: قيد التفرد في قاعدة البيانات هو الحكم النهائي؛ إعادة المحاولة في
///    ServiceBase.RunInTransaction شبكة أمان عند تعارض القيد.
/// §B110: على SQL Server يُقفل صف المخطط بقفل تحديث داخل معاملة المستدعي،
///    فيتسلسل الطلبان المتزامنان ولا يتصادمان أصلاً.
/// </summary>
public class NumberingService : INumberingService
{
    private readonly DatesErpDbContext _db;

    public NumberingService(DatesErpDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// §ترقيم نقي 1.50.53 — كل نوع مستند له تسلسل نقي 1،2،3… بلا بادئة ولا سنة.
    /// المستندات القديمة (REC-2026-0001…) تبقى كما هي — لا تُمس.
    /// التسلسل الجديد يبدأ من 1 عند أول استخدام نقي، ويستمر مستقلاً عن العداد القديم.
    /// </summary>
    private static string NormalizeScheme(string schemeCode) => schemeCode switch
    {
        "PLN" => "PLAN",
        _ => schemeCode
    };

    public string Next(string schemeCode)
    {
        schemeCode = NormalizeScheme(schemeCode);
        var scheme = GetOrCreateScheme(schemeCode);
        int maxPure = GetMaxPureNumber(schemeCode);
        if (maxPure == 0 && scheme.Prefix != "PURE")
        {
            // ترحيل لمرة واحدة فقط من العدادات ذات البادئة القديمة (REC-...) إلى التسلسل النقي،
            // دون تصفير العداد في الاستدعاءات التالية داخل نفس الجلسة أو للقوالب.
            scheme.LastSequence = 0;
            scheme.Prefix = "PURE";
        }
        else
        {
            if (scheme.Prefix != "PURE") scheme.Prefix = "PURE";
            if (maxPure > scheme.LastSequence) scheme.LastSequence = maxPure;
        }

        string number;
        do
        {
            scheme.LastSequence += 1;
            number = scheme.LastSequence.ToString();
        } while (IsUsed(schemeCode, number));

        return number;
    }

    public string Peek(string schemeCode)
    {
        schemeCode = NormalizeScheme(schemeCode);
        var scheme = GetOrCreateScheme(schemeCode);
        int maxPure = GetMaxPureNumber(schemeCode);
        int last = (maxPure == 0 && scheme.Prefix != "PURE")
            ? 0
            : Math.Max(maxPure, scheme.LastSequence);
        int next = last + 1;
        while (IsUsed(schemeCode, next.ToString())) next++;
        return next.ToString();
    }

    public string Reserve(string schemeCode)
    {
        bool ownsTransaction = _db.Database.CurrentTransaction == null;
        using var transaction = ownsTransaction
            ? _db.Database.BeginTransaction(System.Data.IsolationLevel.Serializable)
            : null;
        try
        {
            var num = Next(schemeCode);
            _db.SaveChanges();
            transaction?.Commit();
            return num;
        }
        catch
        {
            try { transaction?.Rollback(); } catch { }
            if (ownsTransaction) _db.ChangeTracker.Clear();
            throw;
        }
    }

    private NumberingScheme GetOrCreateScheme(string schemeCode)
    {
        NumberingScheme? scheme = _db.NumberingSchemes.Local.FirstOrDefault(s => s.SchemeCode == schemeCode)
            ?? (_db.Database.IsSqlServer()
                ? _db.NumberingSchemes
                    .FromSqlRaw("SELECT * FROM [NumberingSchemes] WITH (UPDLOCK, HOLDLOCK) WHERE [SchemeCode] = {0}", schemeCode)
                    .FirstOrDefault()
                : _db.NumberingSchemes.FirstOrDefault(s => s.SchemeCode == schemeCode));
        if (scheme == null)
        {
            scheme = new NumberingScheme
            {
                SchemeCode = schemeCode,
                SchemeName = schemeCode,
                Prefix = "PURE",
                LastSequence = 0
            };
            _db.NumberingSchemes.Add(scheme);
        }
        return scheme;
    }

    /// <summary>أكبر رقم نقي موجود في جدول المخطط باستعلام قاعدة بيانات مباشر O(1) بلا سحب آلاف الصفوف للذاكرة.</summary>
    private int GetMaxPureNumber(string schemeCode)
    {
        (string? table, string? column, IEnumerable<string?> localValues) = schemeCode switch
        {
            "SHIP" => ("Shipments", "DocumentNumber", _db.Shipments.Local.Select(x => x.DocumentNumber)),
            "PLAN" => ("ProductionPlans", "DocumentNumber", _db.ProductionPlans.Local.Select(x => x.DocumentNumber)),
            "ORD" => ("ProductionOrders", "DocumentNumber", _db.ProductionOrders.Local.Select(x => x.DocumentNumber)),
            "EXE" => ("ProductionExecutions", "DocumentNumber", _db.ProductionExecutions.Local.Select(x => x.DocumentNumber)),
            "QC" => ("QualityChecks", "DocumentNumber", _db.QualityChecks.Local.Select(x => x.DocumentNumber)),
            "PDL" => ("ProductionDeliveries", "DocumentNumber", _db.ProductionDeliveries.Local.Select(x => x.DocumentNumber)),
            "FGR" => ("FinishedGoodsReceipts", "DocumentNumber", _db.FinishedGoodsReceipts.Local.Select(x => x.DocumentNumber)),
            "RCV" => ("FinishedGoodsReceipts", "DocumentNumber", _db.FinishedGoodsReceipts.Local.Select(x => x.DocumentNumber)),
            "CD" => ("CustomerDeliveries", "DocumentNumber", _db.CustomerDeliveries.Local.Select(x => x.DocumentNumber)),
            "TXN" => ("InventoryTransactions", "TxnNumber", _db.InventoryTransactions.Local.Select(x => x.TxnNumber)),
            "CTX" => ("InventoryTransactions", "TxnNumber", _db.InventoryTransactions.Local.Select(x => x.TxnNumber)),
            "CCD" => ("CartonCountDocs", "DocumentNumber", _db.CartonCountDocs.Local.Select(x => x.DocumentNumber)),
            "CSD" => ("CartonSaleDocs", "DocumentNumber", _db.CartonSaleDocs.Local.Select(x => x.DocumentNumber)),
            "PCL" => ("PlanClosings", "DocumentNumber", _db.PlanClosings.Local.Select(x => x.DocumentNumber)),
            "LOT" => ("Lots", "LotCode", _db.Lots.Local.Select(x => x.LotCode)),
            "TASK" => ("WorkflowTasks", "TaskNumber", _db.WorkflowTasks.Local.Select(x => x.TaskNumber)),
            "TRT" => ("RawTreatments", "TreatmentNo", _db.RawTreatments.Local.Select(x => x.TreatmentNo)),
            "TRF" => ("InventoryTransactions", "ReferenceDocNumber",
                _db.InventoryTransactions.Local.Where(x => x.ReferenceDocType == Core.Domain.Enums.ReferenceDocType.StockTransfer).Select(x => x.ReferenceDocNumber)),
            _ => (null, null, Array.Empty<string?>())
        };
        if (table == null || column == null) return 0;

        int max = 0;
        foreach (var number in localValues)
            if (int.TryParse(number, out var v) && v > max) max = v;

        var conn = _db.Database.GetDbConnection();
        bool wasClosed = conn.State != System.Data.ConnectionState.Open;
        if (wasClosed) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            if (_db.Database.CurrentTransaction is var efTx && efTx != null)
                cmd.Transaction = (System.Data.Common.DbTransaction)Microsoft.EntityFrameworkCore.Storage.DbContextTransactionExtensions.GetDbTransaction(efTx);
            string extraFilter = schemeCode == "TRF"
                ? $" AND [ReferenceDocType] = {(int)Core.Domain.Enums.ReferenceDocType.StockTransfer}"
                : "";
            cmd.CommandText = _db.Database.IsSqlServer()
                ? $"SELECT COALESCE(MAX(TRY_CAST([{column}] AS INT)), 0) FROM [{table}] WHERE [{column}] IS NOT NULL{extraFilter} AND [{column}] NOT LIKE '%[^0-9]%' AND LEN([{column}]) BETWEEN 1 AND 9"
                : $"SELECT COALESCE(MAX(CAST([{column}] AS INTEGER)), 0) FROM [{table}] WHERE [{column}] IS NOT NULL{extraFilter} AND [{column}] != '' AND NOT [{column}] GLOB '*[^0-9]*' AND LENGTH([{column}]) BETWEEN 1 AND 9";
            var scalar = cmd.ExecuteScalar();
            int dbMax = scalar == null || scalar == DBNull.Value ? 0 : Convert.ToInt32(scalar);
            return Math.Max(max, dbMax);
        }
        finally
        {
            if (wasClosed) conn.Close();
        }
    }

    /// <summary>هل هذا الرقم مستخدم فعلاً في جدول المخطط؟</summary>
    private bool IsUsed(string schemeCode, string number) => schemeCode switch
    {
        "SHIP" => _db.Shipments.Any(x => x.DocumentNumber == number) || _db.Shipments.Local.Any(x => x.DocumentNumber == number),
        "PLAN" => _db.ProductionPlans.Any(x => x.DocumentNumber == number) || _db.ProductionPlans.Local.Any(x => x.DocumentNumber == number),
        "ORD" => _db.ProductionOrders.Any(x => x.DocumentNumber == number) || _db.ProductionOrders.Local.Any(x => x.DocumentNumber == number),
        "EXE" => _db.ProductionExecutions.Any(x => x.DocumentNumber == number) || _db.ProductionExecutions.Local.Any(x => x.DocumentNumber == number),
        "QC" => _db.QualityChecks.Any(x => x.DocumentNumber == number) || _db.QualityChecks.Local.Any(x => x.DocumentNumber == number),
        "PDL" => _db.ProductionDeliveries.Any(x => x.DocumentNumber == number) || _db.ProductionDeliveries.Local.Any(x => x.DocumentNumber == number),
        "FGR" => _db.FinishedGoodsReceipts.Any(x => x.DocumentNumber == number) || _db.FinishedGoodsReceipts.Local.Any(x => x.DocumentNumber == number),
        "RCV" => _db.FinishedGoodsReceipts.Any(x => x.DocumentNumber == number) || _db.FinishedGoodsReceipts.Local.Any(x => x.DocumentNumber == number),
        "CD" => _db.CustomerDeliveries.Any(x => x.DocumentNumber == number) || _db.CustomerDeliveries.Local.Any(x => x.DocumentNumber == number),
        "TXN" => _db.InventoryTransactions.Any(x => x.TxnNumber == number) || _db.InventoryTransactions.Local.Any(x => x.TxnNumber == number),
        "CTX" => _db.InventoryTransactions.Any(x => x.TxnNumber == number) || _db.InventoryTransactions.Local.Any(x => x.TxnNumber == number),
        "CCD" => _db.CartonCountDocs.Any(x => x.DocumentNumber == number) || _db.CartonCountDocs.Local.Any(x => x.DocumentNumber == number),
        "CSD" => _db.CartonSaleDocs.Any(x => x.DocumentNumber == number) || _db.CartonSaleDocs.Local.Any(x => x.DocumentNumber == number),
        "PCL" => _db.PlanClosings.Any(x => x.DocumentNumber == number) || _db.PlanClosings.Local.Any(x => x.DocumentNumber == number),
        "LOT" => _db.Lots.Any(x => x.LotCode == number) || _db.Lots.Local.Any(x => x.LotCode == number),
        "TASK" => _db.WorkflowTasks.Any(x => x.TaskNumber == number) || _db.WorkflowTasks.Local.Any(x => x.TaskNumber == number),
        "TRT" => _db.RawTreatments.Any(x => x.TreatmentNo == number) || _db.RawTreatments.Local.Any(x => x.TreatmentNo == number),
        "TRF" => _db.InventoryTransactions.Any(x => x.ReferenceDocType == Core.Domain.Enums.ReferenceDocType.StockTransfer && x.ReferenceDocNumber == number)
                 || _db.InventoryTransactions.Local.Any(x => x.ReferenceDocType == Core.Domain.Enums.ReferenceDocType.StockTransfer && x.ReferenceDocNumber == number),
        _ => false
    };
}
