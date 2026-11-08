using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace DatesErp.Infrastructure.Connection;

/// <summary>
/// وضع تشغيل منفصل لحزمة OFFTT 1.50.74: إعدادات وملفات المستخدم داخل مجلد خاص،
/// ولا يقرأ إعدادات النسخ السابقة أو يكتشف خادماً بديلاً أو يرجع إلى SQLite.
/// لا ينشئ قاعدة SQL Server؛ يتطلب قاعدة جديدة فارغة أُنشئت مسبقاً.
/// </summary>
public static class StandaloneInstall
{
    public const string StartupSwitch = "--offtt-standalone";
    public const string LocalDataFolder = "OFFTT_LATEST_20261022";
    private const string AppIdentity = "OFFTT_LATEST_20261022";
    private const string SourceVersion = "1.50.74";
    private const string DatabaseMarkerFile = "standalone-database.json";
    private static int _enabled;
    private static Marker _pendingMarker;

    public static bool IsEnabled => Volatile.Read(ref _enabled) == 1;

    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LocalDataFolder);

    public static void Enable()
    {
        Interlocked.Exchange(ref _enabled, 1);
        Directory.CreateDirectory(DataDirectory);
    }

    /// <summary>يفحص الاتصال بالإعداد الذي أدخله المستخدم فقط؛ لا يكتشف خوادم بديلة.</summary>
    public static bool ProbeConfiguredDatabase(AppConfig config)
    {
        if (!IsEnabled || config == null || string.IsNullOrWhiteSpace(config.Server)
            || string.IsNullOrWhiteSpace(config.Database) || config.AuthMode == "Local")
            return false;

        try
        {
            using var connection = new SqlConnection(config.BuildSqlServerConnectionString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT DB_NAME()";
            var actualName = Convert.ToString(command.ExecuteScalar());
            return string.Equals(actualName, config.Database, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// حارس قبل أي EnsureCreated/ترحيل/بذر: يثبت هوية SQL Server والقاعدة،
    /// ويرفض قاعدة بها جداول عند أول تشغيل. لقاعدة فارغة، يؤجل كتابة علامة الربط
    /// المحلية حتى يوافق المستخدم صراحةً على إنشاء المخطط داخل تلك القاعدة.
    /// </summary>
    public static void VerifyTargetDatabase(DbConnection connection, AppConfig config)
    {
        if (!IsEnabled) return;
        if (config == null || string.IsNullOrWhiteSpace(config.Server)
            || string.IsNullOrWhiteSpace(config.Database) || config.AuthMode == "Local")
            throw new InvalidOperationException("وضع OFFTT المنفصل يتطلب إعداد SQL Server صريحاً؛ SQLite والوضع المحلي ممنوعان.");
        if (connection is not SqlConnection)
            throw new InvalidOperationException("وضع OFFTT المنفصل يقبل SQL Server فقط؛ لم يحدث أي إنشاء أو ترحيل أو بذر.");
        if (connection.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("يجب فتح اتصال قاعدة OFFTT الجديدة قبل التحقق من هويتها.");

        var settings = new SqlConnectionStringBuilder(connection.ConnectionString);
        if (!string.Equals(settings.DataSource, config.Server.Trim(), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(settings.InitialCatalog, config.Database.Trim(), StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(settings.InitialCatalog))
            throw new InvalidOperationException("رفض الاتصال لأن وجهته لا تطابق خادم/قاعدة إعداد OFFTT المنفصل.");

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DB_NAME(), " +
            "(SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0), " +
            "HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION')";
        using var reader = command.ExecuteReader(System.Data.CommandBehavior.SingleRow);
        if (!reader.Read())
            throw new InvalidOperationException("لم يُرجع SQL Server هوية قاعدة OFFTT.");

        var actualDatabase = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
        var userTableCount = reader.IsDBNull(1) ? -1 : reader.GetInt32(1);
        var canViewSchema = !reader.IsDBNull(2) && Convert.ToInt32(reader.GetValue(2)) == 1;
        if (!string.Equals(actualDatabase, config.Database.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"رفض الاتصال: القاعدة الفعلية {actualDatabase}، والمطلوب {config.Database.Trim()}. لم تُجرَ تهيئة.");
        if (!canViewSchema)
            throw new InvalidOperationException(
                "حساب SQL لا يملك VIEW DEFINITION على القاعدة؛ تعذر التحقق الموثوق من الجداول، لذلك لم تبدأ التهيئة.");
        reader.Close();

        var markerPath = Path.Combine(AppConfig.ConfigDirectory, DatabaseMarkerFile);
        if (File.Exists(markerPath))
        {
            Marker marker;
            try
            {
                marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(markerPath));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("علامة ربط قاعدة OFFTT تالفة؛ أُوقف التشغيل دون تعديل قاعدة البيانات.", ex);
            }

            if (marker == null
                || !string.Equals(marker.AppIdentity, AppIdentity, StringComparison.Ordinal)
                || !string.Equals(marker.Server, settings.DataSource, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(marker.Database, actualDatabase, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("إعداد SQL لا يطابق قاعدة OFFTT المرتبطة بهذه النسخة؛ لم تُجرَ تهيئة.");
            _pendingMarker = null;
            return;
        }

        if (userTableCount != 0)
            throw new InvalidOperationException(
                $"قاعدة {actualDatabase} ليست فارغة ولا توجد علامة ربط لهذه النسخة. " +
                "اربط قاعدة جديدة فارغة؛ لم يُنشأ أو يُرحّل أو يُبذر أي جدول.");

        // لا يُحفظ الربط بعد إذا اختار المستخدم إلغاء تهيئة القاعدة الفارغة.
        _pendingMarker = new Marker
        {
            AppIdentity = AppIdentity,
            Server = settings.DataSource,
            Database = actualDatabase,
            AppVersion = SourceVersion
        };
    }

    /// <summary>يُحفظ الربط المحلي بعد موافقة المستخدم وقبل أول إنشاء للمخطط.</summary>
    public static void CommitTargetDatabase()
    {
        if (!IsEnabled) return;
        var marker = Interlocked.Exchange(ref _pendingMarker, null);
        if (marker == null) return;

        Directory.CreateDirectory(AppConfig.ConfigDirectory);
        var markerPath = Path.Combine(AppConfig.ConfigDirectory, DatabaseMarkerFile);
        if (File.Exists(markerPath))
        {
            Marker existing;
            try { existing = JsonSerializer.Deserialize<Marker>(File.ReadAllText(markerPath)); }
            catch (Exception ex)
            {
                throw new InvalidOperationException("تعذر قراءة علامة ربط OFFTT؛ لم تُجرَ تهيئة.", ex);
            }
            if (existing == null
                || !string.Equals(existing.AppIdentity, marker.AppIdentity, StringComparison.Ordinal)
                || !string.Equals(existing.Server, marker.Server, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(existing.Database, marker.Database, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("تغيرت علامة ربط OFFTT أثناء التهيئة؛ أُوقف التشغيل دون تعديل القاعدة.");
            return;
        }

        File.WriteAllText(markerPath, JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class Marker
    {
        public string AppIdentity { get; set; }
        public string Server { get; set; }
        public string Database { get; set; }
        public string AppVersion { get; set; }
    }
}
