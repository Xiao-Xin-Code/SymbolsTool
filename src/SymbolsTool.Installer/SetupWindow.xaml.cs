using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace SymbolsTool.Installer;

public partial class SetupWindow : Window
{
    private static readonly string DefaultInstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "SymbolsTool");

    private bool _installing;
    private string _installedDir = "";

    public SetupWindow()
    {
        InitializeComponent();
        InstallPathBox.Text = DefaultInstallDir;
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // 安装过程中不允许关闭窗口
        if (_installing) e.Cancel = true;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_installing) return;
        Close();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_installing) return;

        string current = InstallPathBox.Text.Trim();
        string initial = Directory.Exists(current)
            ? current
            : Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar)) ?? "";
        if (!Directory.Exists(initial))
            initial = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var dialog = new OpenFolderDialog
        {
            Title = "选择安装位置",
            InitialDirectory = initial
        };

        if (dialog.ShowDialog(this) == true)
            InstallPathBox.Text = dialog.FolderName;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_installing) return;

        string target = InstallPathBox.Text.Trim().Trim('"');
        string? error = InstallerEngine.ValidateTarget(target);
        if (error != null)
        {
            ShowStatus(error, isError: true);
            return;
        }

        _installing = true;
        InstallButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        OptionsPage.Visibility = Visibility.Collapsed;
        ProgressPage.Visibility = Visibility.Visible;
        InstallProgress.Value = 0;
        ProgressPercent.Text = "0%";
        ProgressDetail.Text = "正在准备…";

        var progress = new Progress<InstallProgress>(p =>
        {
            InstallProgress.Value = p.Percent * 100;
            ProgressPercent.Text = $"{p.Percent * 100:0}%";
            ProgressDetail.Text = p.Message;
        });

        try
        {
            await InstallerEngine.InstallAsync(target, AutoStartCheck.IsChecked == true, progress);

            _installedDir = target;
            _installing = false;
            ProgressPage.Visibility = Visibility.Collapsed;
            DonePage.Visibility = Visibility.Visible;
            DonePath.Text = target;
            DoneAutoStart.Text = AutoStartCheck.IsChecked == true
                ? "已设置开机自动启动，可在“设置 → 应用 → 启动”中关闭。"
                : "未设置开机自动启动。";
            CloseButton.IsEnabled = true;

            if (LaunchCheck.IsChecked == true)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(Path.Combine(target, "SymbolsTool.exe"))
                    {
                        UseShellExecute = true,
                        WorkingDirectory = target
                    });
                }
                catch
                {
                    // 启动失败不影响安装结果
                }
            }
        }
        catch (Exception ex)
        {
            _installing = false;
            ProgressPage.Visibility = Visibility.Collapsed;
            OptionsPage.Visibility = Visibility.Visible;
            InstallButton.IsEnabled = true;
            CloseButton.IsEnabled = true;
            ShowStatus("安装失败：" + ex.Message, isError: true);
        }
    }

    private void ShowStatus(string message, bool isError)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44))
            : new SolidColorBrush(Color.FromRgb(0xA6, 0xAD, 0xC8));
    }

    private void OpenDir_Click(object sender, RoutedEventArgs e)
    {
        if (_installedDir.Length == 0) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_installedDir}\"") { UseShellExecute = true });
        }
        catch
        {
            // 打开资源管理器失败时忽略
        }
    }

    private void Finish_Click(object sender, RoutedEventArgs e) => Close();
}
