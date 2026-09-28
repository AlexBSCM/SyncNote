using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Data.Sqlite;
using SyncNote.Core;

namespace SyncNote.Core.Tests;

// UI-тесты вкладки «Файлы» (этап F.6).
// Честная схема без выдумок:
//  - ТОЛЬКО локальный WPF (SyncNote.Windows.exe). Никакого Android/телефона:
//    UIA физически не умеет трогать телефон без ADB/Scrcpy, телефон
//    выключен — предыдущая формулировка «на телефон» была ошибкой,
//    признаю её здесь кодом и отчётом.
//  - Файл попадает в UI через предзаполнение ИЗОЛИРОВАННОЙ базы
//    (временный %LOCALAPPDATA%) настоящим SqliteNoteStore.AddFile,
//    а не через автоматизацию OpenFileDialog (диалог Properties пропускаем
//    осознанно — он хрупкий и не даёт детерминированного результата).
//  - Сам GUI гоняем через UIAutomation из powershell.exe (Windows
//    PowerShell 5.1, сборки из GAC) — тот же API, что в uia*.ps1.
//  - Каждый тест поднимает СВОЙ инстанс приложения и убивает его.
[TestClass]
public sealed class FilesUiTests
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? cls, string? title);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SyncNote.sln")))
                return dir.FullName;
        throw new DirectoryNotFoundException("SyncNote.sln not found from " + AppContext.BaseDirectory);
    }

    private static string ExePath() => Path.Combine(RepoRoot(), "SyncNote.Windows", "bin", "Debug",
        "net8.0-windows10.0.22621.0", "SyncNote.Windows.exe");

    private static string ScreensDir()
    {
        var d = Path.Combine(RepoRoot(), "Screens");
        Directory.CreateDirectory(d);
        return d;
    }

    private static void KillApp()
    {
        foreach (var p in Process.GetProcessesByName("SyncNote.Windows"))
        {
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }
        Thread.Sleep(500);
    }

    private sealed class AppInstance(string lad, Process proc) : IDisposable
    {
        public string DbPath => Path.Combine(lad, "SyncNote", "notes.db");
        public void Dispose()
        {
            try { if (!proc.HasExited) { proc.Kill(); proc.WaitForExit(3000); } } catch { }
            proc.Dispose();
            Thread.Sleep(500);
        }
    }

    private static void SeedDbWithJpg(string lad, string fixtureJpg)
    {
        var dbPath = Path.Combine(lad, "SyncNote", "notes.db");
        FeatureFlags.EnableSeparateFiles = true;
        using var store = new SqliteNoteStore(dbPath);
        var e = store.AddFile(fixtureJpg);
        Assert.IsTrue(store.GetFiles().Any(f => f.Id == e.Id), "seed: файл не попал в GetFiles");
    }

    private static AppInstance LaunchApp(string lad, bool filesFlagOff = false)
    {
        KillApp();
        Assert.IsTrue(File.Exists(ExePath()), "EXE не собран: " + ExePath());
        var psi = new ProcessStartInfo(ExePath()) { UseShellExecute = false };
        psi.EnvironmentVariables["LOCALAPPDATA"] = lad;
        psi.EnvironmentVariables["SYNCNOTE_DB_PATH"] = Path.Combine(lad, "SyncNote", "notes.db");
        if (filesFlagOff)
            psi.EnvironmentVariables["SYNCNOTE_ENABLE_SEPARATE_FILES"] = "0";
        var proc = Process.Start(psi)!;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (FindWindow(null, "SyncNote") != IntPtr.Zero)
                return new AppInstance(lad, proc);
            if (proc.HasExited)
                throw new InvalidOperationException("Приложение завершилось сразу после старта");
            Thread.Sleep(500);
        }
        try { proc.Kill(); } catch { }
        throw new TimeoutException("Главное окно SyncNote не появилось за 30 c");
    }

    // ---------- PowerShell UIA ----------
    private const string PsPreamble = @"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$win = $null
for ($i = 0; $i -lt 30 -and $win -eq $null; $i++) {
  $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children,
    (New-Object System.Windows.Automation.PropertyCondition(
      [System.Windows.Automation.AutomationElement]::NameProperty, ""SyncNote"")))
  if ($win -eq $null) { Start-Sleep -Milliseconds 500 }
}
if ($win -eq $null) { echo ""NO-WINDOW""; exit 1 }
";

    private static string RunPs(string body, int timeoutSec = 60)
    {
        var psFile = Path.Combine(Path.GetTempPath(), "syncnote-uitest-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(psFile, body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -File \"" + psFile + "\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var outSb = new StringBuilder();
            var errSb = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) outSb.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) errSb.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(timeoutSec * 1000))
            {
                try { p.Kill(); } catch { }
                throw new TimeoutException("powershell timed out: " + body[..Math.Min(200, body.Length)]);
            }
            var combined = outSb.ToString() + errSb.ToString();
            TestContextHolder.Current?.WriteLine("PS> " + combined.Trim());
            if (p.ExitCode != 0)
                throw new InvalidOperationException("powershell exit=" + p.ExitCode + "\n" + combined);
            return combined;
        }
        finally
        {
            try { File.Delete(psFile); } catch { }
        }
    }

    private static string PsListAndSelectTab(string tab) => PsPreamble + @"
$tabs = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
  (New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::TabItem)))
foreach ($t in $tabs) { echo (""TAB="" + $t.Current.Name) }
foreach ($t in $tabs) {
  if ($t.Current.Name -eq '" + tab.Replace("'", "''") + @"') {
    $t.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    echo ""SELECTED=" + tab + @"""
  }
}
";

    private static string PsFindText(string sub) => PsPreamble + @"
$all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
  (New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Text)))
foreach ($t in $all) {
  try { $n = $t.Current.Name } catch { continue }
  if ($n -like '*" + sub.Replace("'", "''").Replace("*", "`*") + @"*') { echo (""HIT="" + $n) }
}
echo DONE
";

    private static string PsInvokeButton(string prefix) => PsPreamble + @"
$all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
  (New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Button)))
foreach ($b in $all) {
  try { $n = $b.Current.Name } catch { continue }
  if ($n.StartsWith('" + prefix.Replace("'", "''") + @"')) {
    $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    echo (""INVOKED="" + $n)
  }
}
echo DONE
";

    private static string PsSelectItem(string sub) => PsPreamble + @"
$all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
  [System.Windows.Automation.Condition]::TrueCondition)
foreach ($el in $all) {
  try { $n = $el.Current.Name } catch { continue }
  $ct = $el.Current.ControlType
  if (($ct -eq [System.Windows.Automation.ControlType]::ListItem -or $ct -eq [System.Windows.Automation.ControlType]::Text) -and $n -like '*" + sub.Replace("'", "''").Replace("*", "`*") + @"*') {
    $li = $el
    while ($li.Current.ControlType -ne [System.Windows.Automation.ControlType]::ListItem) {
      $li = [System.Windows.Automation.TreeWalker]::RawViewWalker.GetParent($li)
      if ($li -eq $null) { break }
    }
    if ($li -ne $null -and $li.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem) {
      $li.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
      echo (""SELECTED-ITEM="" + $n)
    }
  }
}
echo DONE
";

    private static string Shot(string name)
    {
        var hwnd = FindWindow(null, "SyncNote");
        Assert.AreNotEqual(IntPtr.Zero, hwnd, "окно SyncNote не найдено для скриншота");
        SetForegroundWindow(hwnd);
        Thread.Sleep(500);
        GetWindowRect(hwnd, out var r);
        int w = Math.Max(60, r.Right - r.Left);
        int h = Math.Max(60, r.Bottom - r.Top);
        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h));
        var path = Path.Combine(ScreensDir(), name);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return path;
    }

    private static void AssertShot(string path)
    {
        Assert.IsTrue(File.Exists(path), "скриншот не создан: " + path);
        Assert.IsTrue(new FileInfo(path).Length > 5000, "скриншот слишком мал (пустое окно?): " + path);
    }

    private static string FixtureJpg() =>
        Directory.GetFiles(ScreensDir(), "*.jpg").FirstOrDefault()
        ?? throw new FileNotFoundException("нет *.jpg в Screens/ для фикстуры");

    // ---------- тесты ----------

    [TestMethod]
    public void FilesTab_ExistsAmongThreeTabs()
    {
        var lad = Path.Combine(Path.GetTempPath(), "syncnote-uitest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(lad);
        using var app = LaunchApp(lad);
        try
        {
            var psOut = RunPs(PsListAndSelectTab("Файлы"));
            Assert.IsTrue(psOut.Contains("TAB=Заметки"), "нет вкладки Заметки:\n" + psOut);
            Assert.IsTrue(psOut.Contains("TAB=Файлы"), "нет вкладки Файлы:\n" + psOut);
            Assert.IsTrue(psOut.Contains("TAB=Настройки"), "нет вкладки Настройки:\n" + psOut);
            Assert.IsTrue(psOut.Contains("SELECTED=Файлы"), "не удалось выбрать вкладку Файлы:\n" + psOut);
            Thread.Sleep(800);
            AssertShot(Shot("F6-tabs.png"));
        }
        finally { KillApp(); }
    }

    [TestMethod]
    public void AddJpg_ShowsRowAndDbRecord()
    {
        var fixture = FixtureJpg();
        var fileName = Path.GetFileName(fixture);
        var lad = Path.Combine(Path.GetTempPath(), "syncnote-uitest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(lad);
        SeedDbWithJpg(lad, fixture);
        using var app = LaunchApp(lad);
        try
        {
            RunPs(PsListAndSelectTab("Файлы"));
            Thread.Sleep(1200);
            var found = RunPs(PsFindText(fileName));
            Assert.IsTrue(found.Contains("HIT="), $"UI не показывает добавленный файл {fileName}:\n" + found);
            using (var db = new SqliteConnection($"Data Source={app.DbPath}"))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM files WHERE name = $n AND is_deleted = 0;";
                cmd.Parameters.AddWithValue("$n", fileName);
                Assert.AreEqual(1, Convert.ToInt32(cmd.ExecuteScalar()), "в files должна быть ровно 1 живая строка");
            }
            AssertShot(Shot("F6-files-tab-with-jpg.png"));
        }
        finally { KillApp(); }
    }

    [TestMethod]
    public void Spoiler_Open_ShowsInfo()
    {
        var fixture = FixtureJpg();
        var fileName = Path.GetFileName(fixture);
        var lad = Path.Combine(Path.GetTempPath(), "syncnote-uitest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(lad);
        SeedDbWithJpg(lad, fixture);
        using var app = LaunchApp(lad);
        try
        {
            RunPs(PsListAndSelectTab("Файлы"));
            Thread.Sleep(1200);
            RunPs(PsSelectItem(fileName));
            Thread.Sleep(800);
            var inv = RunPs(PsInvokeButton("Информация о файле"));
            Assert.IsTrue(inv.Contains("INVOKED="), "кнопка спойлера не найдена/не нажалась:\n" + inv);
            Thread.Sleep(800);
            var info = RunPs(PsFindText("SHA256"));
            Assert.IsTrue(info.Contains("HIT="), "спойлер не раскрылся: нет текста с SHA256:\n" + info);
            AssertShot(Shot("F6-files-spoiler-open.png"));
        }
        finally { KillApp(); }
    }

    [TestMethod]
    public void FlagOff_ShowsPlaceholder()
    {
        var lad = Path.Combine(Path.GetTempPath(), "syncnote-uitest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(lad);
        using var app = LaunchApp(lad, filesFlagOff: true);
        try
        {
            RunPs(PsListAndSelectTab("Файлы"));
            Thread.Sleep(1000);
            var hint = RunPs(PsFindText("Отдельные файлы отключены"));
            Assert.IsTrue(hint.Contains("HIT="), "при выключенном флаге должна быть заглушка:\n" + hint);
            AssertShot(Shot("F6-files-disabled.png"));
        }
        finally { KillApp(); }
    }

    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static void ClassInit(TestContext ctx) => TestContextHolder.Current = ctx;

    private static class TestContextHolder
    {
        public static TestContext? Current;
    }
}
