using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UpdateBotAAA;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        bool autoRun = args == null || !args.Any(a => string.Equals(a, "--manual", StringComparison.OrdinalIgnoreCase));
        Application.Run(new UpdaterBotForm(autoRun));
    }
}

public sealed class UpdaterBotForm : Form
{
    private const string DefaultTargetDir = @"D:\TM1212\TMRSA_1.50.20";
    private const string ShortcutName = "AAA";
    private const string VersionStamp = "1.50.122";

    private readonly TextBox _targetBox;
    private readonly Button _browseBtn;
    private readonly Button _startBtn;
    private readonly Button _openAppBtn;
    private readonly ProgressBar _progress;
    private readonly Label _statusLabel;
    private readonly RichTextBox _logBox;
    private readonly bool _autoStartOnLoad;
    private bool _isRunning;
    private bool _completed;

    public UpdaterBotForm(bool autoStartOnLoad)
    {
        _autoStartOnLoad = autoStartOnLoad;

        Text = $"بوت التحديثات الخفيف المباشر — الإصدار {VersionStamp} (اختصار {ShortcutName})";
        Size = new Size(750, 500);
        MinimumSize = new Size(680, 440);
        StartPosition = FormStartPosition.CenterScreen;
        RightToLeft = RightToLeft.Yes;
        RightToLeftLayout = true;
        BackColor = Color.FromArgb(248, 250, 252);
        Font = new Font("Tahoma", 9.5f, FontStyle.Regular);

        var headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 76,
            BackColor = Color.FromArgb(22, 40, 60),
            Padding = new Padding(18, 12, 18, 10)
        };

        var titleLbl = new Label
        {
            Text = $"⚡ بوت التحديث السريع (ملفات التحديث فقط) — النسخة {VersionStamp}",
            ForeColor = Color.White,
            Font = new Font("Tahoma", 12f, FontStyle.Bold),
            Dock = DockStyle.Top,
            Height = 28
        };

        var subLbl = new Label
        {
            Text = "يحدّث ملفات النظام المعدّلة فقط داخل D:\\TM1212\\TMRSA_1.50.20 وينشئ اختصار سطح المكتب (AAA)",
            ForeColor = Color.FromArgb(212, 175, 55),
            Font = new Font("Tahoma", 9f, FontStyle.Regular),
            Dock = DockStyle.Top,
            Height = 22
        };

        headerPanel.Controls.Add(subLbl);
        headerPanel.Controls.Add(titleLbl);

        var targetPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 58,
            Padding = new Padding(18, 12, 18, 8)
        };

        var targetLbl = new Label
        {
            Text = "مجلد النظام الحالي:",
            AutoSize = true,
            Location = new Point(18, 17),
            Font = new Font("Tahoma", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(31, 41, 55)
        };

        _targetBox = new TextBox
        {
            Text = DefaultTargetDir,
            Location = new Point(170, 14),
            Width = 435,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Font = new Font("Consolas", 10f, FontStyle.Bold),
            RightToLeft = RightToLeft.No
        };

        _browseBtn = new Button
        {
            Text = "تغيير...",
            Location = new Point(615, 12),
            Size = new Size(105, 29),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            BackColor = Color.White,
            FlatStyle = FlatStyle.System
        };
        _browseBtn.Click += BrowseBtn_Click;

        targetPanel.Controls.Add(targetLbl);
        targetPanel.Controls.Add(_targetBox);
        targetPanel.Controls.Add(_browseBtn);

        var progressPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 64,
            Padding = new Padding(18, 4, 18, 8)
        };

        _statusLabel = new Label
        {
            Text = "جاهز لتركيب ملفات التحديث وإنشاء اختصار AAA...",
            Dock = DockStyle.Top,
            Height = 24,
            Font = new Font("Tahoma", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(20, 83, 45)
        };

        _progress = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = 24,
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Style = ProgressBarStyle.Continuous
        };

        progressPanel.Controls.Add(_progress);
        progressPanel.Controls.Add(_statusLabel);

        var bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            Padding = new Padding(18, 10, 18, 10),
            BackColor = Color.FromArgb(241, 245, 249)
        };

        _startBtn = new Button
        {
            Text = "🔄 تركيب التحديث وإنشاء اختصار AAA",
            Size = new Size(275, 36),
            Location = new Point(18, 11),
            BackColor = Color.FromArgb(20, 83, 45),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Tahoma", 9.5f, FontStyle.Bold)
        };
        _startBtn.FlatAppearance.BorderSize = 0;
        _startBtn.Click += async (_, _) => await RunUpdateAsync();

        _openAppBtn = new Button
        {
            Text = "🚀 فتح النظام المحدث الآن",
            Size = new Size(220, 36),
            Location = new Point(305, 11),
            BackColor = Color.FromArgb(22, 40, 60),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Tahoma", 9.5f, FontStyle.Bold),
            Enabled = false
        };
        _openAppBtn.FlatAppearance.BorderSize = 0;
        _openAppBtn.Click += OpenAppBtn_Click;

        var closeBtn = new Button
        {
            Text = "إغلاق",
            Size = new Size(100, 36),
            Location = new Point(620, 11),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.System
        };
        closeBtn.Click += (_, _) => Close();

        bottomPanel.Controls.Add(_startBtn);
        bottomPanel.Controls.Add(_openAppBtn);
        bottomPanel.Controls.Add(closeBtn);

        var logHost = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18, 4, 18, 8)
        };

        _logBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(15, 23, 42),
            ForeColor = Color.FromArgb(226, 232, 240),
            Font = new Font("Tahoma", 9.2f),
            BorderStyle = BorderStyle.FixedSingle
        };
        logHost.Controls.Add(_logBox);

        Controls.Add(logHost);
        Controls.Add(bottomPanel);
        Controls.Add(progressPanel);
        Controls.Add(targetPanel);
        Controls.Add(headerPanel);

        Shown += UpdaterBotForm_Shown;
    }

    private async void UpdaterBotForm_Shown(object sender, EventArgs e)
    {
        Log($"مرحباً بك في بوت التحديث السريع إلى الإصدار {VersionStamp}.");
        Log($"المجلد المستهدف: {_targetBox.Text}");

        if (!_autoStartOnLoad) return;

        if (Directory.Exists(_targetBox.Text.Trim()))
        {
            Log("تم العثور على مجلد النظام الحالي — سيبدأ التحديث التلقائي الآن...");
            await Task.Delay(700);
            if (!IsDisposed && !_isRunning && !_completed)
                await RunUpdateAsync();
        }
        else
        {
            SetStatus("المجلد المحدد غير موجود حالياً — تحقق من المسار ثم اضغط زر التحديث.", 0, Color.FromArgb(180, 83, 9));
            Log($"⚠ لم يتم العثور على المجلد: {_targetBox.Text}. يمكنك استعراض المجلد الصحيح ثم الضغط على زر التحديث.");
        }
    }

    private void BrowseBtn_Click(object sender, EventArgs e)
    {
        if (_isRunning) return;
        using var dlg = new FolderBrowserDialog
        {
            Description = "اختر مجلد النظام الحالي المحمّل مسبقاً (D:\\TM1212\\TMRSA_1.50.20)",
            SelectedPath = Directory.Exists(_targetBox.Text) ? _targetBox.Text : ""
        };
        if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
            _targetBox.Text = dlg.SelectedPath;
    }

    private async Task RunUpdateAsync()
    {
        if (_isRunning) return;
        string targetDir = _targetBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
        {
            MessageBox.Show(
                "مجلد النظام المحدد غير موجود:\n" + targetDir + "\n\nتأكد من المسار الصحيح ثم أعد المحاولة.",
                "تنبيه",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1,
                MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
            return;
        }

        string existingExe = ResolveLauncherExe(targetDir);
        if (existingExe == null)
        {
            MessageBox.Show(
                "لم يتم العثور على أداة تشغيل النظام (MfgSystem.exe) داخل:\n" + targetDir +
                "\n\nهذه الحزمة مخصصة لتحديث النظام المحمّل مسبقاً. تأكد من اختيار المجلد الذي يحتوي على MfgSystem.exe.",
                "تنبيه",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1,
                MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
            return;
        }

        string botDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
        string deltaDir = Path.Combine(botDir, "UpdateFiles");
        string optionalDir = Path.Combine(botDir, "OptionalDeps");

        if (!Directory.Exists(deltaDir) || !File.Exists(Path.Combine(deltaDir, "MfgSystem.dll")))
        {
            MessageBox.Show(
                "لم يتم العثور على مجلد ملفات التحديث (UpdateFiles) بجانب البوت.\n" +
                "يرجى فك ضغط ملف الـ ZIP بالكامل أولاً ثم تشغيل UpdateBot_AAA.exe.",
                "فك الضغط مطلوب",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1,
                MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
            return;
        }

        _isRunning = true;
        _startBtn.Enabled = false;
        _browseBtn.Enabled = false;
        _targetBox.ReadOnly = true;

        try
        {
            string actualAppDir = Path.GetDirectoryName(existingExe)!;

            SetStatus("[1/5] التحقق من النظام الحالي وملفات التحديث...", 15, Color.FromArgb(30, 64, 175));
            Log($"✔ [1/5] تم التحقق من وجود أداة فتح النظام: {existingExe}");

            SetStatus("[2/5] إغلاق أي نافذة مفتوحة من النظام مؤقتاً...", 32, Color.FromArgb(30, 64, 175));
            int closed = await Task.Run(() => StopRunningInstances(actualAppDir));
            Log(closed > 0
                ? $"✔ [2/5] تم إغلاق ({closed}) عملية مفتوحة لتحرير ملفات الـ DLL."
                : "✔ [2/5] النظام مغلق وجاهز للتحديث الفوري.");

            SetStatus("[3/5] حفظ نسخة احتياطية سريعة للمكتبات القديمة...", 52, Color.FromArgb(30, 64, 175));
            string backupPath = await Task.Run(() => BackupModifiedDlls(actualAppDir, deltaDir));
            Log($"✔ [3/5] تم حفظ نسخة رجوع للملفات المستبدلة في: {backupPath}");

            SetStatus("[4/5] نسخ ملفات التحديث الجديدة فقط بدون المساس بأدوات التشغيل أو البيانات...", 78, Color.FromArgb(30, 64, 175));
            int updatedCount = await Task.Run(() => ApplyDeltaFiles(deltaDir, optionalDir, actualAppDir));
            Log($"✔ [4/5] تم تحديث ({updatedCount}) ملف بنجاح خلال لحظات مع الحفاظ الكامل على قواعد البيانات وإعدادات الاتصال.");

            SetStatus("[5/5] إنشاء اختصار أداة فتح النظام على سطح المكتب باسم AAA...", 92, Color.FromArgb(30, 64, 175));
            var shortcuts = await Task.Run(() => CreateDesktopShortcutAAA(existingExe));
            foreach (var sc in shortcuts)
                Log($"✔ [5/5] تم إنشاء الاختصار على سطح المكتب: {sc}");

            SetStatus($"✅ اكتمل التحديث إلى الإصدار {VersionStamp} وإنشاء اختصار {ShortcutName} على سطح المكتب!", 100, Color.FromArgb(20, 83, 45));
            Log("════════════════════════════════════════════════════════════");
            Log($"🎉 تم تحديث النظام بنجاح تام وبدون أي أوامر تنصيب!");
            Log($"• المجلد المحدّث      : {actualAppDir}");
            Log($"• أداة فتح المشروع   : {existingExe}");
            Log($"• اختصار سطح المكتب  : {ShortcutName}.lnk");
            Log($"• التعديلات المفعّلة  : تخطيط إنتاج يومي متتابع بحجز خام المسودة وإظهار المتبقي، مع أدوات النظام الحالية.");
            Log("════════════════════════════════════════════════════════════");

            _completed = true;
            _openAppBtn.Enabled = true;
            _startBtn.Text = "✅ اكتمل التحديث";
        }
        catch (Exception ex)
        {
            SetStatus("❌ توقفت عملية التحديث بسبب خطأ — راجع السجل أدناه.", _progress.Value, Color.FromArgb(185, 28, 28));
            Log("❌ خطأ أثناء التحديث: " + ex.Message);
            _startBtn.Enabled = true;
            _browseBtn.Enabled = true;
            _targetBox.ReadOnly = false;
            MessageBox.Show(
                "حدث خطأ أثناء التحديث:\n" + ex.Message,
                "بوت التحديث — خطأ",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error,
                MessageBoxDefaultButton.Button1,
                MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
        }
        finally
        {
            _isRunning = false;
        }
    }

    private static string ResolveLauncherExe(string targetDir)
    {
        string[] candidates =
        {
            Path.Combine(targetDir, "MfgSystem.exe"),
            Path.Combine(targetDir, "MfgSystem_Publish", "MfgSystem.exe"),
            Path.Combine(targetDir, "Publish", "MfgSystem.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static int StopRunningInstances(string appDir)
    {
        int stopped = 0;
        string normalized = Path.GetFullPath(appDir).TrimEnd('\\', '/');
        string[] names = { "MfgSystem", "DatesErp.Desktop", "DateERP", "TMRSA" };

        foreach (var name in names)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var p in procs)
            {
                try
                {
                    if (p.Id == Process.GetCurrentProcess().Id) continue;
                    string exePath = "";
                    try { exePath = p.MainModule?.FileName ?? ""; } catch { }

                    bool match = string.IsNullOrEmpty(exePath) ||
                                 exePath.StartsWith(normalized, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(name, "MfgSystem", StringComparison.OrdinalIgnoreCase);
                    if (!match) continue;

                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        p.CloseMainWindow();
                        if (p.WaitForExit(2000)) { stopped++; continue; }
                    }
                    p.Kill();
                    p.WaitForExit(3000);
                    stopped++;
                }
                catch { }
            }
        }
        Thread.Sleep(300);
        return stopped;
    }

    private static string BackupModifiedDlls(string appDir, string deltaDir)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string mfgDataDir = Path.Combine(localAppData, "MfgSystem");
        Directory.CreateDirectory(mfgDataDir);

        // الحفاظ على ملف الاتصال أو ترحيل نسخة قديمة إن لم تكن موجودة في MfgSystem بعد
        string newCfg = Path.Combine(mfgDataDir, "config.json");
        if (!File.Exists(newCfg))
        {
            foreach (var legacy in new[]
            {
                Path.Combine(appDir, "config.json"),
                Path.Combine(localAppData, "DateERP", "config.json"),
                Path.Combine(localAppData, "TMRSA", "config.json")
            })
            {
                if (!File.Exists(legacy)) continue;
                File.Copy(legacy, newCfg, overwrite: false);
                break;
            }
        }

        string backupRoot = Path.Combine(mfgDataDir, "updates", "backups", "delta_before_" + VersionStamp + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(backupRoot);

        foreach (var file in Directory.GetFiles(deltaDir))
        {
            string existing = Path.Combine(appDir, Path.GetFileName(file));
            if (File.Exists(existing))
                File.Copy(existing, Path.Combine(backupRoot, Path.GetFileName(file)), overwrite: true);
        }

        return backupRoot;
    }

    private static int ApplyDeltaFiles(string deltaDir, string optionalDir, string appDir)
    {
        int count = CopyRecursiveOverwrite(deltaDir, appDir);

        // المكتبات المساعدة الإضافية تُنسخ فقط إن لم تكن موجودة مسبقاً في مجلد المستخدم
        if (Directory.Exists(optionalDir))
        {
            foreach (var optFile in Directory.GetFiles(optionalDir))
            {
                string dest = Path.Combine(appDir, Path.GetFileName(optFile));
                if (!File.Exists(dest))
                {
                    File.Copy(optFile, dest, overwrite: false);
                    count++;
                }
            }
        }

        return count;
    }

    private static int CopyRecursiveOverwrite(string srcDir, string dstDir)
    {
        int copied = 0;
        Directory.CreateDirectory(dstDir);
        foreach (var f in Directory.GetFiles(srcDir))
        {
            string dest = Path.Combine(dstDir, Path.GetFileName(f));
            File.Copy(f, dest, overwrite: true);
            copied++;
        }
        foreach (var sub in Directory.GetDirectories(srcDir))
        {
            copied += CopyRecursiveOverwrite(sub, Path.Combine(dstDir, Path.GetFileName(sub)));
        }
        return copied;
    }

    private static List<string> CreateDesktopShortcutAAA(string existingLauncherExe)
    {
        string workDir = Path.GetDirectoryName(existingLauncherExe)!;
        var desktopFolders = new List<string>();

        string specialDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(specialDesktop) && Directory.Exists(specialDesktop))
            desktopFolders.Add(specialDesktop);

        string userProfileDesktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");
        if (!string.IsNullOrWhiteSpace(userProfileDesktop) && Directory.Exists(userProfileDesktop) &&
            !desktopFolders.Any(d => string.Equals(d, userProfileDesktop, StringComparison.OrdinalIgnoreCase)))
        {
            desktopFolders.Add(userProfileDesktop);
        }

        var createdPaths = new List<string>();
        Type shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("تعذر الوصول إلى خدمة اختصارات ويندوز (WScript.Shell).");
        object shell = Activator.CreateInstance(shellType);

        foreach (var desktop in desktopFolders)
        {
            string lnkPath = Path.Combine(desktop, ShortcutName + ".lnk");
            object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            Type scType = shortcut.GetType();
            scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { existingLauncherExe });
            scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workDir });
            scType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { existingLauncherExe + ",0" });
            scType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { $"AAA — فتح نظام التصنيع ({VersionStamp})" });
            scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            createdPaths.Add(lnkPath);
        }

        return createdPaths;
    }

    private void OpenAppBtn_Click(object sender, EventArgs e)
    {
        try
        {
            string targetExe = ResolveLauncherExe(_targetBox.Text.Trim());
            if (targetExe == null) return;
            Process.Start(new ProcessStartInfo
            {
                FileName = targetExe,
                WorkingDirectory = Path.GetDirectoryName(targetExe)!,
                UseShellExecute = true
            });
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show("تعذر تشغيل النظام:\n" + ex.Message, "تنبيه", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetStatus(string text, int progress, Color color)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => SetStatus(text, progress, color)));
            return;
        }
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
        _progress.Value = Math.Max(0, Math.Min(100, progress));
    }

    private void Log(string line)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => Log(line)));
            return;
        }
        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }
}
