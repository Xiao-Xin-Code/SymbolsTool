using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SymbolsTool.Helpers;

namespace SymbolsTool.Services;

public sealed class TrayIconService : IDisposable
{
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct NOTIFYICONDATA
	{
		public int cbSize;

		public nint hWnd;

		public uint uID;

		public uint uFlags;

		public uint uCallbackMessage;

		public nint hIcon;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
		public string szTip;

		public uint dwState;

		public uint dwStateMask;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
		public string szInfo;

		public uint uTimeoutOrVersion;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
		public string szInfoTitle;

		public uint dwInfoFlags;

		public Guid guidItem;

		public nint hBalloonIcon;
	}

	private struct POINT
	{
		public int X;

		public int Y;
	}

	private struct MSLLHOOKSTRUCT
	{
		public POINT pt;

		public int mouseData;

		public int flags;

		public int time;

		public nint dwExtraInfo;
	}

	private delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);

	private const int SM_CXSMICON = 49;

	private const uint NIM_ADD = 0u;

	private const uint NIM_MODIFY = 1u;

	private const uint NIM_DELETE = 2u;

	private const uint NIF_MESSAGE = 1u;

	private const uint NIF_ICON = 2u;

	private const uint NIF_TIP = 4u;

	private const uint NIF_GUID = 16u;

	private const int WM_TRAYICON = 32769;

	private const int WM_LBUTTONUP = 514;

	private const int WM_RBUTTONUP = 517;

	private const int WM_CONTEXTMENU = 123;

	private const int WH_MOUSE_LL = 14;

	private const int WM_LBUTTONDOWN = 513;

	private const int WM_RBUTTONDOWN = 516;

	private readonly FloatingBallWindow _ball;

	private readonly TrayMenuActions _actions;

	private readonly Icon _icon;

	private readonly HwndSource? _source;

	private readonly nint _hwnd;

	private readonly uint _taskbarCreatedMsg;

	private readonly uint _uID;

	private static readonly Guid TrayIconGuid = new Guid("7F1B3C52-9D64-4F0A-9C1E-2B5A8D6E4F31");

	private bool _added;

	private ContextMenu? _menu;

	private nint _menuHook = IntPtr.Zero;

	private LowLevelMouseProc? _menuHookProc;

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern uint RegisterWindowMessage(string name);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern nint SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, nint hMod, uint dwThreadId);

	[DllImport("user32.dll")]
	private static extern bool UnhookWindowsHookEx(nint hhk);

	[DllImport("user32.dll")]
	private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

	[DllImport("kernel32.dll", CharSet = CharSet.Auto)]
	private static extern nint GetModuleHandle(string? lpModuleName);

	[DllImport("user32.dll")]
	private static extern int GetSystemMetrics(int nIndex);

	public TrayIconService(FloatingBallWindow ball, TrayMenuActions actions)
	{
		_ball = ball;
		_actions = actions;
		_icon = AppIcon.CreateTrayIcon(GetSystemMetrics(49));
		_hwnd = new WindowInteropHelper(ball).Handle;
		_taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
		if (_hwnd != IntPtr.Zero)
		{
			_source = HwndSource.FromHwnd(_hwnd);
			_source?.AddHook(WndProc);
		}
		AddIcon();
	}

	private void AddIcon()
	{
		if (_hwnd != IntPtr.Zero)
		{
			NOTIFYICONDATA data = new NOTIFYICONDATA
			{
				cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
				hWnd = _hwnd,
				uID = _uID,
				uFlags = 23u,
				uCallbackMessage = 32769u,
				hIcon = _icon.Handle,
				szTip = "符号工具 · SymbolsTool",
				guidItem = TrayIconGuid
			};
			_added = Shell_NotifyIcon(0u, ref data);
			if (_added)
			{
				Shell_NotifyIcon(1u, ref data);
			}
		}
	}

	private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
	{
		if (_taskbarCreatedMsg != 0 && msg == (int)_taskbarCreatedMsg)
		{
			AddIcon();
			handled = true;
			return IntPtr.Zero;
		}
		if (msg == 32769)
		{
			switch ((int)(((IntPtr)lParam).ToInt64() & 0xFFFF))
			{
			case 514:
				// 左键点击托盘图标：永远"唤醒"面板，保持面板上次的位置
				_actions.ShowPanel();
				handled = true;
				break;
			case 123:
			case 517:
				ShowMenu();
				handled = true;
				break;
			}
		}
		return IntPtr.Zero;
	}

	private void ShowMenu()
	{
		(string, bool, bool, bool, bool) tuple = _actions.GetState();
		ContextMenu menu = new ContextMenu
		{
			PlacementTarget = _ball,
			Placement = PlacementMode.MousePoint
		};
		MenuItem menuItem = new MenuItem
		{
			Header = "显示/隐藏符号面板",
			InputGestureText = tuple.Item1
		};
		menuItem.Click += (object s, RoutedEventArgs e) =>
		{
			_actions.TogglePanel();
		};
		menu.Items.Add(menuItem);
		MenuItem itemResetPos = new MenuItem
		{
			Header = "恢复面板到悬浮球位置"
		};
		itemResetPos.Click += (object s, RoutedEventArgs e) =>
		{
			_actions.ResetPanelPosition();
		};
		menu.Items.Add(itemResetPos);
		menu.Items.Add(new Separator());
		MenuItem itemDisable = new MenuItem
		{
			Header = "禁用快捷键",
			IsCheckable = true,
			IsChecked = !tuple.Item2
		};
		itemDisable.Click += (object s, RoutedEventArgs e) =>
		{
			_actions.SetHotkeysEnabled(!itemDisable.IsChecked);
		};
		menu.Items.Add(itemDisable);
		MenuItem itemBall = new MenuItem
		{
			Header = "使用悬浮球",
			IsCheckable = true,
			IsChecked = tuple.Item3
		};
		itemBall.Click += (object s, RoutedEventArgs e) =>
		{
			_actions.SetBallEnabled(itemBall.IsChecked);
		};
		menu.Items.Add(itemBall);
		MenuItem itemCopy = new MenuItem
		{
			Header = "点击符号复制",
			IsCheckable = true,
			IsChecked = tuple.Item4
		};
		itemCopy.Click += (object s, RoutedEventArgs e) =>
		{
			_actions.SetCopyOnClick(itemCopy.IsChecked);
		};
		menu.Items.Add(itemCopy);
		MenuItem itemShowNames = new MenuItem
		{
			Header = "显示符号名称",
			IsCheckable = true,
			IsChecked = tuple.Item5
		};
		itemShowNames.Click += (object s, RoutedEventArgs e) =>
		{
			_actions.SetShowSymbolNames(itemShowNames.IsChecked);
		};
		menu.Items.Add(itemShowNames);
		MenuItem itemColorSymbols = new MenuItem
		{
			Header = "彩色符号显示",
			IsCheckable = true,
			IsChecked = _actions.GetColorSymbols()
		};
		itemColorSymbols.Click += (object s, RoutedEventArgs e) =>
		{
			_actions.SetColorSymbols(itemColorSymbols.IsChecked);
		};
		menu.Items.Add(itemColorSymbols);
		menu.Items.Add(new Separator());
		MenuItem menuItem2 = new MenuItem
		{
			Header = "首选项…"
		};
		menuItem2.Click += (object s, RoutedEventArgs e) =>
		{
			_actions.OpenPreferences();
		};
		menu.Items.Add(menuItem2);
		MenuItem menuItem3 = new MenuItem
		{
			Header = "重新启动"
		};
		menuItem3.Click += (object s, RoutedEventArgs e) =>
		{
			_actions.Restart();
		};
		menu.Items.Add(menuItem3);
		menu.Items.Add(new Separator());
		MenuItem menuItem4 = new MenuItem
		{
			Header = "退出"
		};
		menuItem4.Click += (object s, RoutedEventArgs e) =>
		{
			Application.Current.Shutdown();
		};
		menu.Items.Add(menuItem4);
		menu.Closed += (object s, RoutedEventArgs e) =>
		{
			UninstallMenuHook();
			if (_menu == menu)
			{
				_menu = null;
			}
		};
		_menu = menu;
		menu.IsOpen = true;
		InstallMenuHook();
	}

	private void InstallMenuHook()
	{
		if (_menuHook == IntPtr.Zero)
		{
			_menuHookProc = MenuHookProc;
			_menuHook = SetWindowsHookEx(14, _menuHookProc, GetModuleHandle(null), 0u);
		}
	}

	private void UninstallMenuHook()
	{
		if (_menuHook != IntPtr.Zero)
		{
			UnhookWindowsHookEx(_menuHook);
			_menuHook = IntPtr.Zero;
			_menuHookProc = null;
		}
	}

	private nint MenuHookProc(int nCode, nint wParam, nint lParam)
	{
		try
		{
			if (nCode >= 0 && (wParam == 513 || wParam == 516))
			{
				ContextMenu menu = _menu;
				if (menu != null && menu.IsOpen && !IsPointInMenu(Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam).pt))
				{
					ContextMenu menu2 = _menu;
					menu2.Dispatcher.BeginInvoke((Action)(() =>
					{
						if (menu2.IsOpen)
						{
							menu2.IsOpen = false;
						}
					}), DispatcherPriority.Input);
				}
			}
		}
		catch
		{
		}
		return CallNextHookEx(_menuHook, nCode, wParam, lParam);
	}

	private bool IsPointInMenu(POINT screenPoint)
	{
		if (_menu == null || !_menu.IsOpen)
		{
			return false;
		}
		try
		{
			System.Windows.Point point = _menu.PointToScreen(new System.Windows.Point(0.0, 0.0));
			DpiScale dpi = VisualTreeHelper.GetDpi(_menu);
			return new Rect(point.X, point.Y, _menu.ActualWidth * dpi.DpiScaleX, _menu.ActualHeight * dpi.DpiScaleY).Contains(new System.Windows.Point(screenPoint.X, screenPoint.Y));
		}
		catch
		{
			return false;
		}
	}

	public void Dispose()
	{
		UninstallMenuHook();
		if (_added && _hwnd != IntPtr.Zero)
		{
			NOTIFYICONDATA data = new NOTIFYICONDATA
			{
				cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
				hWnd = _hwnd,
				uID = _uID,
				uFlags = 16u,
				guidItem = TrayIconGuid
			};
			Shell_NotifyIcon(2u, ref data);
			_added = false;
		}
		_source?.RemoveHook(WndProc);
		_icon.Dispose();
	}
}
