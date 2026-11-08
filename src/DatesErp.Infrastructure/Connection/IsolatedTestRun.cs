using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace DatesErp.Infrastructure.Connection;

/// <summary>
/// اتصال تشغيل R2 المعزول. لا يقرأ إعدادات المستخدم؛ الوجهة ثابتة عمداً ولا يوجد
/// مسار بديل إلى إعداد الإنتاج أو SQLite. يفعّلها مشغّل سطح المكتب فقط عبر المفتاح الصريح.
/// </summary>
public static class IsolatedTestRun
{
    public const string StartupSwitch = "--offtt-test-mode";
    public const string Server = @".\SQLEXPRESS2022";
    public const string Database = "DateERP_OFFTT_TEST_20261017";
    public const string LocalDataFolder = "MfgSystem_R2_TEST_20261017";
    public const string SqlInstance = "SQLEXPRESS2022";

    private static int _enabled;
    private static int _databaseVerified;

    public static bool IsEnabled => Volatile.Read(ref _enabled) == 1;
    public static bool IsDatabaseVerified => IsEnabled && Volatile.Read(ref _databaseVerified) == 1;

    public static void Enable() => Interlocked.Exchange(ref _enabled, 1);

    /// <summary>حارس صريح لأعمال إنشاء المخطط والترحيل والبذر في وضع الاختبار.</summary>
    public static void RequireDatabaseVerified()
    {
        if (IsEnabled && !IsDatabaseVerified)
            throw new InvalidOperationException(
                $"حُظرت تهيئة/ترحيل/بذر قاعدة البيانات قبل التحقق من الاتصال بـ {Server} / {Database}.");
    }

    public static string BuildConnectionString()
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = Database,
            IntegratedSecurity = true,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = true,
            PersistSecurityInfo = false,
            MultipleActiveResultSets = true,
            ConnectTimeout = 8
        };
        return builder.ConnectionString;
    }

    /// <summary>يفشل فوراً إذا حاول أي سياق استعمال مزوداً أو وجهة أخرى.</summary>
    public static void AssertConnectionSettings(DbConnection connection)
    {
        if (!IsEnabled) return;
        if (connection is not SqlConnection)
            throw new InvalidOperationException(
                $"وضع الاختبار المعزول يقبل SQL Server فقط: {Server} / {Database}؛ SQLite والمزودات الأخرى ممنوعة.");

        var builder = new SqlConnectionStringBuilder(connection.ConnectionString);
        if (!string.Equals(builder.DataSource, Server, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(builder.InitialCatalog, Database, StringComparison.OrdinalIgnoreCase)
            || !builder.IntegratedSecurity
            || !string.IsNullOrEmpty(builder.UserID)
            || !string.IsNullOrEmpty(builder.AttachDBFilename)
            || !string.IsNullOrEmpty(builder.FailoverPartner)
            || builder.UserInstance)
        {
            throw new InvalidOperationException(
                $"رفض اتصال خارج قاعدة الاختبار المحددة حصراً: {Server} / {Database}.");
        }
    }

    /// <summary>
    /// يجب استدعاؤه بعد Open وقبل أي تهيئة/ترحيل/بذر؛ يتحقق من هوية القاعدة والنسخة
    /// التي أكدها SQL Server نفسه، لا من نص الاتصال وحده.
    /// </summary>
    public static void VerifyOpenedConnection(DbConnection connection)
    {
        if (!IsEnabled)
            throw new InvalidOperationException("لا يُستخدم فحص اتصال الاختبار خارج وضع الاختبار المعزول.");
        Volatile.Write(ref _databaseVerified, 0);
        AssertConnectionSettings(connection);
        if (connection.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("يجب فتح اتصال SQL Server قبل التحقق من هوية قاعدة الاختبار.");

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT DB_NAME(), CONVERT(nvarchar(128), SERVERPROPERTY('InstanceName'))";
        using var reader = command.ExecuteReader(System.Data.CommandBehavior.SingleRow);
        if (!reader.Read())
            throw new InvalidOperationException("لم يُرجع SQL Server هوية قاعدة الاختبار.");

        var actualDatabase = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
        var actualInstance = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
        if (!string.Equals(actualDatabase, Database, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(actualInstance, SqlInstance, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"رفض الاتصال: الهوية الفعلية هي {actualInstance}/{actualDatabase}، " +
                $"والمطلوب حصراً {SqlInstance}/{Database}.");
        }
        Volatile.Write(ref _databaseVerified, 1);
    }
}
