﻿using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

/// <summary>
/// 符号工具卸载程序（.NET Framework 4.8，系统自带，无需额外运行时）。
/// 删除开机自启动项、卸载注册表信息，并通过临时助手进程删除整个安装目录
/// （助手会等待本进程退出后再删除，因此目录内的 Uninstall.exe 也能一并删掉）。
/// </summary>
internal static class Program
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SymbolsTool";
    private const string RunValueName = "SymbolsTool";

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--cleanup")
        {
            Cleanup(args[1], args[2]);
            return;
        }

        string dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        DialogResult result = MessageBox.Show(
            "确定要卸载“符号工具”吗？\r\n\r\n将删除安装目录：\r\n" + dir +
            "\r\n\r\n同时清除开机自启动和卸载注册表信息。",
            "卸载符号工具", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (result != DialogResult.Yes) return;

        StopApp();
        RemoveRunEntry();
        RemoveUninstallEntry();

        // 防止误删磁盘根目录
        if (dir.Length <= 3) return;

        try
        {
            string helper = Path.Combine(
                Path.GetTempPath(), "SymbolsTool-Uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
            File.Copy(Application.ExecutablePath, helper, true);

            ProcessStartInfo psi = new ProcessStartInfo(
                helper,
                "--cleanup \"" + dir + "\" " + Process.GetCurrentProcess().Id.ToString());
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = Path.GetTempPath();
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show("已清除卸载信息，但删除安装目录失败，请手动删除：\r\n" + dir +
                            "\r\n\r\n" + ex.Message,
                "卸载符号工具", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>临时助手：等待原卸载程序退出后删除安装目录，最后删除自身</summary>
    private static void Cleanup(string dir, string pidText)
    {
        int pid;
        if (int.TryParse(pidText, out pid))
        {
            try { Process.GetProcessById(pid).WaitForExit(60000); }
            catch { /* 原进程已退出 */ }
        }

        for (int attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                if (!Directory.Exists(dir)) break;
                ClearReadOnly(dir);
                Directory.Delete(dir, true);
                break;
            }
            catch
            {
                System.Threading.Thread.Sleep(500);
            }
        }

        try
        {
            string self = Application.ExecutablePath;
            ProcessStartInfo psi = new ProcessStartInfo(
                "cmd.exe",
                "/c ping -n 4 127.0.0.1 >nul & del /f /q \"" + self + "\" & " +
                "ping -n 2 127.0.0.1 >nul & if exist \"" + self + "\" del /f /q \"" + self + "\"");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.WorkingDirectory = Path.GetTempPath();
            Process.Start(psi);
        }
        catch
        {
            // 临时文件删除失败不影响卸载结果
        }
    }

    private static void ClearReadOnly(string dir)
    {
        try
        {
            string[] files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
            for (int i = 0; i < files.Length; i++)
            {
                try { File.SetAttributes(files[i], FileAttributes.Normal); }
                catch { /* 单个文件失败时忽略 */ }
            }
        }
        catch
        {
            // 忽略
        }
    }

    private static void StopApp()
    {
        try
        {
            Process[] processes = Process.GetProcessesByName("SymbolsTool");
            for (int i = 0; i < processes.Length; i++)
            {
                try
                {
                    processes[i].Kill();
                    processes[i].WaitForExit(4000);
                }
                catch
                {
                    // 忽略单个进程的失败
                }
            }
        }
        catch
        {
            // 忽略
        }
    }

    private static void RemoveRunEntry()
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (key != null) key.DeleteValue(RunValueName, false);
            }
        }
        catch
        {
            // 忽略
        }
    }

    private static void RemoveUninstallEntry()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, false);
        }
        catch
        {
            // 忽略
        }
    }
}
