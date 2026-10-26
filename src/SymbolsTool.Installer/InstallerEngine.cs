using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SymbolsTool.Installer;

/// <summary>安装进度（0~1）与当前步骤说明</summary>
public readonly record struct InstallProgress(double Percent, string Message);

/// <summary>
/// 安装核心逻辑：解包内置载荷到用户选择的目录，
/// 并写入开机自启动、卸载信息与卸载程序。
/// </summary>
public static class InstallerEngine
{
    private const string AppExeName = "SymbolsTool.exe";
    private const string PayloadResource = "SymbolsTool.Payload.zip";
    private const string UninstallerResource = "SymbolsTool.Uninstall.exe";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SymbolsTool";
    private const string AppDisplayName = "符号工具 SymbolsTool";
    private const string AppVersion = "1.0.0";
    private const string RunValueName = "SymbolsTool";

    /// <summary>校验安装目录，返回 null 表示可以使用</summary>
    public static string? ValidateTarget(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "请选择安装位置。";

        string full;
        try { full = Path.GetFullPath(path); }
        catch { return "安装路径无效，请重新选择。"; }

        string root = Path.GetPathRoot(full) ?? "";
        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar),
                          root.TrimEnd(Path.DirectorySeparatorChar),
                          StringComparison.OrdinalIgnoreCase))
            return "不能直接安装到磁盘根目录，请选择子文件夹。";

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (full.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return "不能安装到 Windows 目录。";

        try
        {
            Directory.CreateDirectory(full);
            // 试写一个临时文件，确认当前用户对目标目录有写权限
            using (File.Create(Path.Combine(full, ".symbols-write-test"), 1, FileOptions.DeleteOnClose)) { }
        }
        catch (Exception ex)
        {
            return $"该目录不可写（{ex.Message}）。请换一个位置，或使用默认的当前用户目录。";
        }

        return null;
    }

    public static async Task InstallAsync(
        string targetDir,
        bool autoStart,
        IProgress<InstallProgress> progress)
    {
        await Task.Run(() =>
        {
            string target = Path.GetFullPath(targetDir);
            Directory.CreateDirectory(target);

            progress.Report(new InstallProgress(0.02, "正在关闭已运行的符号工具…"));
            StopRunningApp();

            string tempZip = Path.Combine(
                Path.GetTempPath(), "SymbolsToolSetup-" + Guid.NewGuid().ToString("N") + ".zip");

            try
            {
                progress.Report(new InstallProgress(0.05, "正在准备安装文件…"));
                using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource)
                                      ?? throw new InvalidOperationException("安装包不完整：缺少程序文件。"))
                using (var file = File.Create(tempZip))
                {
                    resource.CopyTo(file);
                }

                progress.Report(new InstallProgress(0.10, "正在写入程序文件…"));
                ExtractPayload(tempZip, target, progress);

                if (!File.Exists(Path.Combine(target, AppExeName)))
                    throw new InvalidOperationException("安装包不完整：未找到主程序。");

                progress.Report(new InstallProgress(0.93, "正在写入卸载程序…"));
                WriteUninstaller(target);

                progress.Report(new InstallProgress(0.96, "正在写入启动与卸载信息…"));
                RegisterUninstallEntry(target);
                SetAutoStart(target, autoStart);

                progress.Report(new InstallProgress(1.0, "安装完成"));
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            }
        });
    }

    private static void StopRunningApp()
    {
        foreach (var process in Process.GetProcessesByName("SymbolsTool"))
        {
            try
            {
                process.Kill();
                process.WaitForExit(4000);
            }
            catch
            {
                // 进程可能已退出或无权限，继续安装
            }
        }
    }

    private static void ExtractPayload(string zipPath, string target, IProgress<InstallProgress> progress)
    {
        string targetRoot = target.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        using var zip = ZipFile.OpenRead(zipPath);
        int total = zip.Entries.Count;

        for (int i = 0; i < total; i++)
        {
            var entry = zip.Entries[i];
            string destination = Path.GetFullPath(Path.Combine(target, entry.FullName));

            // 防止压缩包内路径跳出安装目录
            if (!destination.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("安装包内容非法。");

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }

            if (i % 8 == 0 || i == total - 1)
            {
                string label = entry.Name.Length > 0 ? entry.Name : entry.FullName;
                progress.Report(new InstallProgress(0.10 + 0.80 * (i + 1) / total, "正在写入：" + label));
            }
        }
    }

    private static void WriteUninstaller(string target)
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(UninstallerResource)
                             ?? throw new InvalidOperationException("安装包不完整：缺少卸载程序。");

        string destination = Path.Combine(target, "Uninstall.exe");
        using var file = File.Create(destination);
        resource.CopyTo(file);
    }

    private static void RegisterUninstallEntry(string target)
    {
        long sizeKb = 0;
        try
        {
            sizeKb = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
                              .Sum(f => new FileInfo(f).Length) / 1024;
        }
        catch
        {
            // 统计失败时忽略
        }

        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", AppDisplayName, RegistryValueKind.String);
        key.SetValue("DisplayVersion", AppVersion, RegistryValueKind.String);
        key.SetValue("Publisher", "SymbolsTool", RegistryValueKind.String);
        key.SetValue("InstallLocation", target, RegistryValueKind.String);
        key.SetValue("DisplayIcon", Path.Combine(target, AppExeName), RegistryValueKind.String);
        key.SetValue("UninstallString", "\"" + Path.Combine(target, "Uninstall.exe") + "\"", RegistryValueKind.String);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)Math.Min(sizeKb, int.MaxValue), RegistryValueKind.DWord);
    }

    private static void SetAutoStart(string target, bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            run.SetValue(RunValueName, "\"" + Path.Combine(target, AppExeName) + "\"", RegistryValueKind.String);
        else
            run.DeleteValue(RunValueName, throwOnMissingValue: false);
    }
}
