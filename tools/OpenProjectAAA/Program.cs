using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OpenProjectAAA;

internal static class Program
{
    private const string DefaultInstallDir = @"D:\TM1212\TMRSA_1.50.20";
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            string targetExe = ResolveExecutable(baseDir);

            if (targetExe == null)
            {
                MessageBox.Show(
                    "لم يتم العثور على ملف تشغيل النظام (MfgSystem.exe) داخل:\n" +
                    baseDir + "\nأو المسار:\n" + DefaultInstallDir +
                    "\n\nيرجى تشغيل بوت التحديث (UpdateBot_AAA.exe) أولاً.",
                    "أداة فتح المشروع — AAA",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button1,
                    MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
                return;
            }

            string workDir = Path.GetDirectoryName(targetExe)!;
            EnsureConnectionConfigMigrated(workDir);

            // إذا كان النظام يعمل بالفعل من نفس المجلد، نفعّل نافذته مباشرة بدلاً من تكرار الفتح
            try
            {
                var running = Process.GetProcessesByName("MfgSystem")
                    .FirstOrDefault(p =>
                    {
                        try
                        {
                            return p.MainWindowHandle != IntPtr.Zero &&
                                   string.Equals(Path.GetDirectoryName(p.MainModule?.FileName ?? ""), workDir, StringComparison.OrdinalIgnoreCase);
                        }
                        catch { return false; }
                    });

                if (running != null && running.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(running.MainWindowHandle, SW_RESTORE);
                    SetForegroundWindow(running.MainWindowHandle);
                    return;
                }
            }
            catch
            {
                // تجاهل أخطاء استعلام العمليات وتابع التشغيل المباشر
            }

            var psi = new ProcessStartInfo
            {
                FileName = targetExe,
                WorkingDirectory = workDir,
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "تعذّر فتح النظام:\n" + ex.Message,
                "أداة فتح المشروع — AAA",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error,
                MessageBoxDefaultButton.Button1,
                MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
        }
    }

    private static string ResolveExecutable(string baseDir)
    {
        string[] candidateDirs =
        {
            baseDir,
            Path.Combine(baseDir, "MfgSystem_Publish"),
            Path.Combine(baseDir, "Publish"),
            DefaultInstallDir,
            Path.Combine(DefaultInstallDir, "MfgSystem_Publish"),
            Path.Combine(DefaultInstallDir, "Publish")
        };

        foreach (var dir in candidateDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            string mfg = Path.Combine(dir, "MfgSystem.exe");
            if (File.Exists(mfg)) return mfg;
        }
        return null;
    }

    /// <summary>
    /// يحافظ على إعداد اتصال قاعدة البيانات من النسخة 1.50.20 إن لم يكن ملف الإعداد الجديد موجوداً بعد.
    /// </summary>
    private static void EnsureConnectionConfigMigrated(string installDir)
    {
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string newConfigDir = Path.Combine(localAppData, "MfgSystem");
            string newConfigPath = Path.Combine(newConfigDir, "config.json");
            if (File.Exists(newConfigPath)) return;

            string[] legacyPaths =
            {
                Path.Combine(installDir, "config.json"),
                Path.Combine(localAppData, "DateERP", "config.json"),
                Path.Combine(localAppData, "TMRSA", "config.json")
            };

            foreach (var legacy in legacyPaths)
            {
                if (!File.Exists(legacy)) continue;
                Directory.CreateDirectory(newConfigDir);
                File.Copy(legacy, newConfigPath, overwrite: false);
                break;
            }
        }
        catch
        {
            // لا يعطل الإقلاع أبداً
        }
    }
}
