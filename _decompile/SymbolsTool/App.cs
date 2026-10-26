using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SymbolsTool.Services;

namespace SymbolsTool;

public class App : Application
{
	private const int HotkeyIdPanel = 1;

	private FloatingBallWindow? _ball;

	private SymbolPanelWindow? _panel;

	private InputEngine? _inputEngine;

	private TrayIconService? _tray;

	private PreferencesWindow? _prefs;

	private HotkeyService? _hotkeys;

	private AppSettings _settings = new AppSettings();

	private bool _hotkeysEnabled = true;

	private bool _ballEnabled = true;

	private const string InstanceMutexName = "Local\\SymbolsTool.SingleInstance";

	private const string ActivateEventName = "Local\\SymbolsTool.Activate";

	private Mutex? _instanceMutex;

	private bool _ownsMutex;

	private EventWaitHandle? _activateEvent;

	private bool _restartRequested;

	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);
		ShutdownMode = ShutdownMode.OnExplicitShutdown;
		_activateEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, "Local\\SymbolsTool.Activate");
		_instanceMutex = new Mutex(initiallyOwned: true, "Local\\SymbolsTool.SingleInstance", out var createdNew);
		_ownsMutex = createdNew;
		if (!createdNew)
		{
			_activateEvent.Set();
			_activateEvent.Dispose();
			_activateEvent = null;
			Shutdown();
			return;
		}
		StartActivationListener();
		DispatcherUnhandledException += (object s, DispatcherUnhandledExceptionEventArgs args) =>
		{
			SafeLog("Dispatcher", args.Exception);
		};
		try
		{
			_inputEngine = new InputEngine();
			_settings = SettingsService.Load();
			_ballEnabled = _settings.BallEnabled;
			// 启动时先不创建悬浮球/托盘/快捷键：先让后台加载好符号库与彩色字形，
			// 全部就绪后再一次性初始化界面，这样用户看到悬浮球时工具已能立即响应
			StartWarmup();
		}
		catch (Exception ex)
		{
			SafeLog("OnStartup", ex);
			MessageBox.Show("启动失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
			Shutdown();
		}
	}

	/// <summary>
	/// 启动预热：先用后台线程构建符号库（纯数据、不涉及 WPF，可安全并行），
	/// 再回到 UI 线程分批生成彩色字形缓存；全部完成后再初始化悬浮球、快捷键与托盘图标。
	/// </summary>
	private void StartWarmup()
	{
		Task.Run(delegate
		{
			try
			{
				_ = SymbolDatabaseService.Symbols.Count;
			}
			catch (Exception ex)
			{
				SafeLog("WarmupDatabase", ex);
			}
		}).ContinueWith(delegate
		{
			Dispatcher.BeginInvoke(new Action(delegate
			{
				try
				{
					EnsurePanel().PreheatColorGlyphs(InitializeShell);
				}
				catch (Exception ex)
				{
					SafeLog("WarmupGlyphs", ex);
					InitializeShell();
				}
			}), DispatcherPriority.Background);
		});
	}

	/// <summary>预热结束后才创建悬浮球、快捷键与托盘图标（用户此时才能看到/操作）</summary>
	private void InitializeShell()
	{
		try
		{
			_ball = new FloatingBallWindow();
			_ball.Show();
			if (!_ballEnabled)
			{
				_ball.Hide();
			}
			_ball.BallClicked += (object? _, EventArgs __) =>
			{
				TogglePanel();
			};
			_ball.DragStarted += (object? _, EventArgs __) =>
			{
				_panel?.Hide();
			};
			_hotkeys = new HotkeyService(_ball);
			ApplyHotkeys(showError: false);
			_tray = new TrayIconService(_ball, new TrayMenuActions(ShowPanel, TogglePanel, ResetPanelPosition, SetBallEnabled, OpenPreferences, RestartApp, () => (PanelShortcut: _settings.PanelHotkey.Display, HotkeysEnabled: _hotkeysEnabled, BallEnabled: _ballEnabled, CopyOnClick: _settings.CopyOnClick, ShowSymbolNames: _settings.ShowSymbolNames), (bool enabled) =>
			{
				_hotkeysEnabled = enabled;
				ApplyHotkeys(showError: false);
			}, (bool enabled) =>
			{
				_settings.CopyOnClick = enabled;
				SettingsService.Save(_settings);
				if (_panel != null)
				{
					_panel.CopyOnClick = enabled;
				}
			}, (bool enabled) =>
			{
				_settings.ShowSymbolNames = enabled;
				SettingsService.Save(_settings);
				if (_panel != null)
				{
					_panel.ShowSymbolNames = enabled;
				}
			}, () => _settings.ColorSymbols, (bool enabled) =>
			{
				_settings.ColorSymbols = enabled;
				SettingsService.Save(_settings);
				if (_panel != null)
				{
					_panel.UseColorSymbols = enabled;
				}
			}));
		}
		catch (Exception ex)
		{
			SafeLog("InitializeShell", ex);
			MessageBox.Show("启动失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
			Shutdown();
		}
	}

	private void StartActivationListener()
	{
		EventWaitHandle handle = _activateEvent;
		if (handle == null)
		{
			return;
		}
		Task.Run(() =>
		{
			try
			{
				while (true)
				{
					handle.WaitOne();
					if (Dispatcher.HasShutdownStarted)
					{
						break;
					}
					Dispatcher.BeginInvoke(new Action(ActivateFromSecondInstance));
				}
			}
			catch (ObjectDisposedException)
			{
			}
		});
	}

	private void ActivateFromSecondInstance()
	{
		SymbolPanelWindow? panel = _panel;
		if (panel == null || !panel.IsVisible)
		{
			TogglePanel();
		}
	}

	/// <summary>
	/// 懒加载创建符号面板：首次真正需要（或启动后的空闲时间）才构建，
	/// 避免在启动瞬间解析面板 XAML、建目录等造成卡顿。
	/// </summary>
	private SymbolPanelWindow EnsurePanel()
	{
		if (_panel == null)
		{
			SymbolPanelWindow panel = new SymbolPanelWindow(_inputEngine!)
			{
				CopyOnClick = _settings.CopyOnClick,
				ShowSymbolNames = _settings.ShowSymbolNames,
				UseColorSymbols = _settings.ColorSymbols
			};
			panel.IsVisibleChanged += (object _, DependencyPropertyChangedEventArgs __) =>
			{
				SyncBallVisibility();
			};
			_panel = panel;
		}
		return _panel;
	}

	private void TogglePanel()
	{
		if (_ball == null)
		{
			return;
		}
		SymbolPanelWindow panel = EnsurePanel();
		if (panel.IsVisible)
		{
			panel.Hide();
			return;
		}
		ShowPanel();
	}

	/// <summary>
	/// 强制显示面板，不做任何隐藏判断。供托盘图标左键、
	/// 从其它实例唤起、等"只想让它出现"的入口调用。
	/// </summary>
	private void ShowPanel()
	{
		if (_ball == null) return;
		SymbolPanelWindow panel = EnsurePanel();
		Point point = _ball.PointToScreen(new Point(_ball.Width / 2.0, _ball.Height / 2.0));
		DpiScale dpi = VisualTreeHelper.GetDpi(_ball);
		panel.ShowNearBall(new Point(point.X / dpi.DpiScaleX, point.Y / dpi.DpiScaleY));
	}

	/// <summary>
	/// 把面板位置重置回悬浮球附近。供托盘图标双击调用。
	/// </summary>
	private void ResetPanelPosition()
	{
		if (_ball == null) return;
		SymbolPanelWindow panel = EnsurePanel();
		Point point = _ball.PointToScreen(new Point(_ball.Width / 2.0, _ball.Height / 2.0));
		DpiScale dpi = VisualTreeHelper.GetDpi(_ball);
		panel.ShowNearBall(new Point(point.X / dpi.DpiScaleX, point.Y / dpi.DpiScaleY), resetPosition: true);
	}

	private void SetBallEnabled(bool enabled)
	{
		_ballEnabled = enabled;
		_settings.BallEnabled = enabled;
		SettingsService.Save(_settings);
		SyncBallVisibility();
	}

	private void SyncBallVisibility()
	{
		if (_ball == null || _ball.IsClosing)
		{
			return;
		}
		if (!_ballEnabled)
		{
			_ball.ShowAfterPanel(restore: false);
			_ball.Hide();
			return;
		}
		SymbolPanelWindow? panel = _panel;
		if (panel != null && panel.IsVisible)
		{
			_ball.HideForPanel();
			return;
		}
		_ball.ShowAfterPanel();
		if (!_ball.IsVisible)
		{
			_ball.Show();
		}
	}

	private void ApplyHotkeys(bool showError)
	{
		if (_hotkeys == null)
		{
			return;
		}
		_hotkeys.UnregisterAll();
		if (_hotkeysEnabled)
		{
			bool flag = _hotkeys.TryRegister(1, _settings.PanelHotkey, TogglePanel);
			if (showError && !flag)
			{
				MessageBox.Show("快捷键 " + _settings.PanelHotkey.Display + " 已被其它程序占用，注册失败。", "快捷键", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			}
		}
	}

	private void OpenPreferences()
	{
		PreferencesWindow prefs = _prefs;
		if (prefs != null && prefs.IsVisible)
		{
			_prefs.Activate();
			return;
		}
		_prefs = new PreferencesWindow(_settings.PanelHotkey);
		_prefs.Saved += (HotkeyGesture gesture) =>
		{
			_settings.PanelModifiers = gesture.Modifiers;
			_settings.PanelKey = gesture.VirtualKey;
			SettingsService.Save(_settings);
			ApplyHotkeys(showError: true);
		};
		_prefs.Show();
	}

	private void RestartApp()
	{
		_restartRequested = true;
		Shutdown();
	}

	private static void SafeLog(string tag, Exception ex)
	{
		try
		{
			File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "SymbolsTool-crash.log"), $"[{DateTime.Now:HH:mm:ss}] {tag}: {ex}\n\n");
		}
		catch
		{
		}
	}

	protected override void OnExit(ExitEventArgs e)
	{
		_tray?.Dispose();
		_hotkeys?.Dispose();
		_activateEvent?.Dispose();
		_activateEvent = null;
		if (_ownsMutex)
		{
			try
			{
				_instanceMutex?.ReleaseMutex();
			}
			catch
			{
			}
			_ownsMutex = false;
		}
		_instanceMutex?.Dispose();
		_instanceMutex = null;
		if (_restartRequested)
		{
			_restartRequested = false;
			try
			{
				string processPath = Environment.ProcessPath;
				if (!string.IsNullOrEmpty(processPath))
				{
					Process.Start(new ProcessStartInfo(processPath)
					{
						UseShellExecute = true
					});
				}
			}
			catch (Exception ex)
			{
				SafeLog("Restart", ex);
			}
		}
		base.OnExit(e);
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "10.0.13.0")]
	public void InitializeComponent()
	{
		// 不设置 StartupUri：本程序是托盘/悬浮球常驻应用，
		// 启动时显示 MainWindow 会留下一个无用的白色空窗口
	}

	[STAThread]
	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "10.0.13.0")]
	public static void Main()
	{
		App app = new App();
		app.InitializeComponent();
		app.Run();
	}
}
