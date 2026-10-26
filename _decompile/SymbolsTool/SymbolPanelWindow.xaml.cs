using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using SymbolsTool.Services;

namespace SymbolsTool;

public partial class SymbolPanelWindow : Window, IComponentConnector, IStyleConnector
{
	private delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);

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

	private const int GWL_EXSTYLE = -20;

	private const int WS_EX_TOOLWINDOW = 128;

	// 点击面板时不抢前台：系统不会激活面板，正在输入的程序不会失焦
	private const int WS_EX_NOACTIVATE = 0x08000000;

	private const uint SWP_NOSIZE = 1u;

	private const uint SWP_NOMOVE = 2u;

	private const uint SWP_NOACTIVATE = 16u;

	private static readonly nint HWND_TOP = IntPtr.Zero;

	private static readonly nint HWND_NOTOPMOST = new IntPtr(-2);

	private const int WH_MOUSE_LL = 14;

	// 前台窗口变化事件：用来在别的窗口被激活时立刻把面板退回它下面
	private const uint EVENT_SYSTEM_FOREGROUND = 3u;

	private const uint WINEVENT_OUTOFCONTEXT = 0u;

	private const int WM_MOUSEWHEEL = 522;

	private const int WM_LBUTTONDOWN = 513;

	private const int WM_MOUSEACTIVATE = 33;

	// 返回 MA_NOACTIVATE：这次点击不激活面板（鼠标消息仍会投递），
	// 目标程序的输入框因此保持焦点，符号又能直接输入过去
	private const int MA_NOACTIVATE = 3;

	#region 多屏支持

	[StructLayout(LayoutKind.Sequential)]
	private struct RECT
	{
		public int Left; public int Top; public int Right; public int Bottom;
	}

	[DllImport("user32.dll")]
	private static extern nint MonitorFromPoint(POINT pt, uint dwFlags);

	[DllImport("user32.dll")]
	private static extern bool GetMonitorInfoW(nint hMonitor, IntPtr lpmi);

	[DllImport("shcore.dll")]
	private static extern int GetDpiForMonitor(nint hMonitor, uint dpiType, out uint dpiX, out uint dpiY);

	private const uint MONITOR_DEFAULTTONEAREA = 2;

	private const uint MDT_EFFECTIVE_DPI = 0;

	// MONITORINFOEX 实际大小：cbSize(4) + rcMonitor(16) + rcWork(16) + dwFlags(4) + szDevice(64) = 104
	private const int MONITORINFOEX_SIZE = 104;

	/// <summary>按物理像素坐标查该屏幕的工作区（返回 WPF 逻辑坐标 Rect，DPI 安全）</summary>
	private static Rect GetWorkAreaAtPhysical(POINT pt)
	{
		nint hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREA);
		if (hMon == IntPtr.Zero) return SystemParameters.WorkArea;

		var buf = new byte[MONITORINFOEX_SIZE];
		BitConverter.GetBytes(MONITORINFOEX_SIZE).CopyTo(buf, 0);
		var gh = GCHandle.Alloc(buf, GCHandleType.Pinned);
		bool ok = GetMonitorInfoW(hMon, gh.AddrOfPinnedObject());
		gh.Free();
		if (!ok) return SystemParameters.WorkArea;

		// rcWork 位于 cbSize(4) + rcMonitor(16) 之后
		const int offWork = 4 + 16;
		int left = BitConverter.ToInt32(buf, offWork);
		int top = BitConverter.ToInt32(buf, offWork + 4);
		int right = BitConverter.ToInt32(buf, offWork + 8);
		int bottom = BitConverter.ToInt32(buf, offWork + 12);
		if (right <= left || bottom <= top) return SystemParameters.WorkArea;

		// 用该显示器自己的 DPI 换算逻辑坐标
		double dpiScale = 1.0;
		try
		{
			if (GetDpiForMonitor(hMon, MDT_EFFECTIVE_DPI, out uint dx, out _) == 0 && dx > 0)
			{
				dpiScale = dx / 96.0;
			}
		}
		catch
		{
			dpiScale = 1.0;
		}
		return new Rect(left / dpiScale, top / dpiScale,
			(right - left) / dpiScale, (bottom - top) / dpiScale);
	}

	/// <summary>按 WPF 逻辑坐标查该位置所在屏幕的工作区</summary>
	private Rect GetWorkAreaAtLogical(double logicalX, double logicalY)
	{
		DpiScale dpi = VisualTreeHelper.GetDpi(this);
		POINT pt = new POINT
		{
			X = (int)Math.Round(logicalX * dpi.DpiScaleX),
			Y = (int)Math.Round(logicalY * dpi.DpiScaleY)
		};
		return GetWorkAreaAtPhysical(pt);
	}

	#endregion

	private nint _mouseHook = IntPtr.Zero;

	private LowLevelMouseProc? _mouseHookProc;

	private readonly InputEngine _inputEngine;

	private string _currentCategory = "";

	// 首次打开/切换分类时只加载当前分类的第一页（36 个），滚动到底部再加载下一页
	private const int SymbolPageSize = 36;

	private readonly RangeObservableCollection<SymbolItem> _symbolItems = new RangeObservableCollection<SymbolItem>();

	private SymbolTemplateSelector? _templateSelector;

	// 是否显示彩色符号（可在托盘右键菜单切换）
	private bool _useColorSymbols = true;

	private List<SymbolItem> _symbolSource = new List<SymbolItem>();

	// 分批加载：每个 Dispatcher 空闲节拍只加载少量符号，避免切换分类时一次性渲染造成卡顿
	private int _loadToken;

	private int _loadTarget;

	private bool _loadScheduled;

	// 当前列表内容对应的分类；用于隐藏后重新打开时跳过重复加载，避免看到加载过程
	private string? _shownCategory;

	// 当前列表是否是搜索结果（搜索结果在重新打开时需要还原成分类内容）
	private bool _itemsFiltered;

	public static readonly DependencyProperty ShowSymbolNamesProperty = DependencyProperty.Register("ShowSymbolNames", typeof(bool), typeof(SymbolPanelWindow), new PropertyMetadata(true, (DependencyObject d, DependencyPropertyChangedEventArgs e) =>
	{
		ToolTipService.SetIsEnabled(d, (bool)e.NewValue);
	}));

	private bool _symbolMenuOpen;

	private bool _dragging;

	private Point _dragStartCursor;

	private double _dragStartLeft;

	private double _dragStartTop;

	private bool _isPinned;

	// 点击面板后临时置顶：这样才能高过"仍然是前台"的目标窗口；
	// 一旦有其他窗口成为前台，就收回置顶并把面板退到它下面（见 OnForegroundChanged）
	private bool _autoTopmost;

	private nint _pasteTarget = IntPtr.Zero;

	private nint _hwnd = IntPtr.Zero;

	private HwndSource? _source;

	// 上次被用户关闭（或 Hide）时的位置，下次显示时保持它，不再回到悬浮球
	private Point? _savedPos;

	public bool CopyOnClick { get; set; } = true;

	public bool ShowSymbolNames
	{
		get
		{
			return (bool)GetValue(ShowSymbolNamesProperty);
		}
		set
		{
			SetValue(ShowSymbolNamesProperty, value);
		}
	}

	[DllImport("user32.dll")]
	private static extern int GetWindowLong(nint h, int n);

	[DllImport("user32.dll")]
	private static extern int SetWindowLong(nint h, int n, int s);

	[DllImport("user32.dll")]
	private static extern bool GetCursorPos(out POINT lpPoint);

	[DllImport("user32.dll")]
	private static extern bool ScreenToClient(nint hWnd, ref POINT lpPoint);

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(nint hWnd);

	[DllImport("user32.dll")]
	private static extern bool IsWindow(nint hWnd);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

	[DllImport("user32.dll")]
	private static extern bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern nint SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, nint hMod, uint dwThreadId);

	[DllImport("user32.dll")]
	private static extern bool UnhookWindowsHookEx(nint hhk);

	[DllImport("user32.dll")]
	private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

	[DllImport("user32.dll")]
	private static extern nint WindowFromPoint(POINT pt);

	[DllImport("user32.dll")]
	private static extern nint GetAncestor(nint hwnd, uint flags);

	[DllImport("user32.dll")]
	private static extern nint GetShellWindow();

	[DllImport("user32.dll")]
	private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc,
		WinEventProc pfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

	[DllImport("user32.dll")]
	private static extern bool UnhookWinEvent(nint hWinEventHook);

	private delegate void WinEventProc(nint hWinEventHook, uint eventType, nint hwnd,
		int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

	private nint _winEventHook = IntPtr.Zero;

	private WinEventProc? _winEventProc;

	public SymbolPanelWindow(InputEngine inputEngine)
	{
		InitializeComponent();
		_inputEngine = inputEngine;
		SourceInitialized += (object? s, EventArgs e) =>
		{
			_hwnd = new WindowInteropHelper(this).Handle;
			int windowLong = GetWindowLong(_hwnd, -20);
			SetWindowLong(_hwnd, -20, windowLong | 0x80);
			_source = HwndSource.FromHwnd(_hwnd);
			_source?.AddHook(WndProc);
			// 监听前台窗口变化：别的窗口被激活时把面板退回它下面（模拟普通窗口的遮挡规则）
			_winEventProc = OnForegroundChanged;
			_winEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
				_winEventProc, 0u, 0u, WINEVENT_OUTOFCONTEXT);
		};
		PanelBorder.LostMouseCapture += (object s, MouseEventArgs e) =>
		{
			// 拖动结束后记录最终位置：拖动过程中 OnLocationChanged 被 _dragging 抑制，
			// 不在这里补记会导致 _savedPos 停留在拖动前的位置（例如悬浮球附近）
			if (_dragging)
			{
				_savedPos = new Point(Left, Top);
			}
			_dragging = false;
		};
		Deactivated += (object? s, EventArgs e) =>
		{
			// 面板失去激活后恢复"点击不抢前台"：此后点符号不会再让正在输入的程序失焦
			SetNoActivate(true);
			// 面板不再是前台，搜索框也一并失焦（否则光标会一直留在搜索框里）
			Keyboard.ClearFocus();
			Dispatcher.BeginInvoke((Action)(() =>
			{
				nint foregroundWindow = GetForegroundWindow();
				if (!IsOwnProcessWindow(foregroundWindow))
				{
					_pasteTarget = foregroundWindow;
				}
			}), DispatcherPriority.Background);
		};
		Topmost = _isPinned;
		ToolTipService.SetIsEnabled(this, ShowSymbolNames);
		IsVisibleChanged += (object s, DependencyPropertyChangedEventArgs e) =>
		{
			if (IsVisible)
			{
				InstallMouseHook();
			}
			else
			{
				UninstallMouseHook();
			}
		};
		SymbolGrid.ItemsSource = _symbolItems;
		_templateSelector = TryFindResource("SymbolItemTemplateSelector") as SymbolTemplateSelector;
		if (_templateSelector != null)
		{
			_templateSelector.UseColor = _useColorSymbols;
		}
	}

	/// <summary>
	/// 只拦截"左键点在符号按钮上"这一种点击：返回 MA_NOACTIVATE 让面板不被激活，
	/// 目标程序的输入框因此保持焦点；鼠标消息仍会投递给面板，按钮 Click 照常触发。
	/// 点在标题栏、分类标签、搜索框或空白处时走默认行为，面板像普通窗口一样被激活。
	/// </summary>
	private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
	{
		if (msg == WM_MOUSEACTIVATE)
		{
			int mouseMsg = (int)((long)lParam >> 16) & 0xFFFF;
			bool overSymbol = IsCursorOverSymbol();
			if (mouseMsg == WM_LBUTTONDOWN && overSymbol)
			{
				handled = true;
				return MA_NOACTIVATE;
			}
		}
		return IntPtr.Zero;
	}

	/// <summary>
	/// 开关 WS_EX_NOACTIVATE。带上它时点击面板不会激活面板，
	/// 前台窗口（正在输入的程序）不会失活，符号可以消息直发过去、全程不闪。
	/// 需要面板真正激活（打开面板、点标题栏/分类/搜索框）时临时取消。
	/// </summary>
	private void SetNoActivate(bool enabled)
	{
		if (_hwnd == IntPtr.Zero)
		{
			return;
		}
		int style = GetWindowLong(_hwnd, -20);
		int updated = (enabled ? (style | WS_EX_NOACTIVATE) : (style & ~WS_EX_NOACTIVATE));
		if (updated != style)
		{
			SetWindowLong(_hwnd, -20, updated);
		}
	}

	/// <summary>鼠标是否落在符号按钮上（WM_MOUSEACTIVATE 的 lParam 不含坐标，需要自己取）</summary>
	private bool IsCursorOverSymbol()
	{
		DependencyObject? element = GetCursorElement();
		return IsSymbolElement(element);
	}

	/// <summary>鼠标是否落在搜索框内（唯一需要面板真正激活、拿键盘焦点的地方）</summary>
	private bool IsCursorOverSearchBox()
	{
		DependencyObject? element = GetCursorElement();
		while (element != null)
		{
			if (ReferenceEquals(element, SearchBorder))
			{
				return true;
			}
			if (element is not Visual visual)
			{
				return false;
			}
			element = VisualTreeHelper.GetParent(visual);
		}
		return false;
	}

	/// <summary>取鼠标位置下的面板内元素</summary>
	private DependencyObject? GetCursorElement()
	{
		if (_hwnd == IntPtr.Zero || !GetCursorPos(out POINT pt))
		{
			return null;
		}
		ScreenToClient(_hwnd, ref pt);
		DpiScale dpi = VisualTreeHelper.GetDpi(this);
		Point point = new Point((double)pt.X / dpi.DpiScaleX, (double)pt.Y / dpi.DpiScaleY);
		return InputHitTest(point) as DependencyObject;
	}

	private static bool IsSymbolElement(DependencyObject? element)
	{
		while (element != null)
		{
			if (element is Button { DataContext: SymbolItem })
			{
				return true;
			}
			if (element is not Visual visual)
			{
				return false;
			}
			element = VisualTreeHelper.GetParent(visual);
		}
		return false;
	}

	/// <summary>是否显示彩色符号；切换后重建当前分类的可见项以应用新模板</summary>
	public bool UseColorSymbols
	{
		get
		{
			return _useColorSymbols;
		}
		set
		{
			if (_useColorSymbols == value)
			{
				return;
			}
			_useColorSymbols = value;
			if (_templateSelector != null)
			{
				_templateSelector.UseColor = value;
			}
			// 开启彩色时先补齐当前可见项的彩色图形
			if (value)
			{
				foreach (SymbolItem item in _symbolItems)
				{
					if (item.UseColorRenderer && item.ColorImage == null)
					{
						item.ColorImage = SymbolGlyphCache.Get(item.ColorText);
					}
				}
			}
			// DataTemplateSelector 只在内容变化时才重新选择模板，
			// 仅改 UseColor 不会刷新已有容器，这里强制刷新视图让开关立即生效
			CollectionViewSource.GetDefaultView(_symbolItems)?.Refresh();
		}
	}

	private void ShowSymbols(List<SymbolItem> items)
	{
		_symbolSource = items;
		// token +1：之前分类还没跑完的追加加载会自动作废
		_loadToken++;
		_loadTarget = Math.Min(SymbolPageSize, items.Count);
		_loadScheduled = false;
		// 同步把第一页（含彩色图形）全部准备好再一次性替换列表：
		// 调用发生在窗口 Show() 之前，界面一亮就是完整内容，不会看到符号逐行出现
		int end = _loadTarget;
		List<SymbolItem> page = new List<SymbolItem>(end);
		for (int k = 0; k < end; k++)
		{
			SymbolItem item = items[k];
			if (_useColorSymbols && item.UseColorRenderer && item.ColorImage == null)
			{
				item.ColorImage = SymbolGlyphCache.Get(item.ColorText);
			}
			page.Add(item);
		}
		_symbolItems.ReplaceInPlace(page);
		SymbolScroll.ScrollToTop();
	}

	/// <summary>
	/// 启动后的空闲时间预热：先构建符号库（首次访问要解析全部符号），
	/// 再分批生成彩色字形缓存。这样首次打开面板、切换分类时都无需现算，避免卡顿。
	/// 全部完成后回调 completed（用于延后初始化悬浮球等界面）。
	/// </summary>
	public void PreheatColorGlyphs(Action? completed = null)
	{
		List<SymbolItem> all = SymbolDatabaseService.Symbols;
		int index = 0;
		void Step()
		{
			int count = 0;
			while (index < all.Count && count < 6)
			{
				SymbolItem item = all[index++];
				if (_useColorSymbols && item.UseColorRenderer && item.ColorImage == null)
				{
					item.ColorImage = SymbolGlyphCache.Get(item.ColorText);
				}
				count++;
			}
			if (index < all.Count)
			{
				Dispatcher.BeginInvoke(new Action(Step), DispatcherPriority.Background);
				return;
			}
			completed?.Invoke();
		}
		Dispatcher.BeginInvoke(new Action(Step), DispatcherPriority.Background);
	}

	/// <summary>用户滚动到底部时请求再加载一页</summary>
	private void RequestMoreSymbols()
	{
		int target = Math.Min(_loadTarget + SymbolPageSize, _symbolSource.Count);
		if (target <= _loadTarget)
		{
			return;
		}
		_loadTarget = target;
		// 正在准备中的链会自动读到新的 _loadTarget；否则另起一条追加链
		if (!_loadScheduled)
		{
			StartLoadChain(_loadToken, _symbolItems.Count);
		}
	}

	/// <summary>
	/// 分多个空闲节拍准备 [start, _loadTarget) 的符号与彩色图形（每次 6 个，避免卡顿），
	/// 全部就绪后一次性追加，界面上不会出现逐行加载的过程。
	/// </summary>
	private void StartLoadChain(int token, int start)
	{
		_loadScheduled = true;
		int index = start;
		void Step()
		{
			if (token != _loadToken)
			{
				_loadScheduled = false;
				return;
			}
			int end = Math.Min(_loadTarget, _symbolSource.Count);
			int count = 0;
			while (index < end && count < 6)
			{
				SymbolItem item = _symbolSource[index];
				if (_useColorSymbols && item.UseColorRenderer && item.ColorImage == null)
				{
					item.ColorImage = SymbolGlyphCache.Get(item.ColorText);
				}
				index++;
				count++;
			}
			if (index < end)
			{
				Dispatcher.BeginInvoke(new Action(Step), DispatcherPriority.Background);
				return;
			}
			List<SymbolItem> page = new List<SymbolItem>(index - start);
			for (int k = start; k < index; k++)
			{
				page.Add(_symbolSource[k]);
			}
			_symbolItems.AddRange(page);
			_loadScheduled = false;
		}
		Dispatcher.BeginInvoke(new Action(Step), DispatcherPriority.Background);
	}

	private void SymbolScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
	{
		// 只响应用户向下滚动：列表替换/追加也会触发 ScrollChanged，
		// 若不区分就会在打开面板或切换分类时连续加载多页，造成卡顿
		if (e.VerticalChange <= 0.0)
		{
			return;
		}
		if (_symbolItems.Count < _symbolSource.Count && SymbolScroll.VerticalOffset + SymbolScroll.ViewportHeight >= SymbolScroll.ExtentHeight - 120.0)
		{
			RequestMoreSymbols();
		}
	}

	private static bool IsOwnProcessWindow(nint hwnd)
	{
		if (hwnd == IntPtr.Zero)
		{
			return true;
		}
		GetWindowThreadProcessId(hwnd, out var processId);
		return processId == (uint)Environment.ProcessId;
	}

	private void OnPanelMouseDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ChangedButton == MouseButton.Left && GetCursorPos(out var lpPoint))
		{
			_dragging = true;
			_dragStartCursor = new Point(lpPoint.X, lpPoint.Y);
			_dragStartLeft = Left;
			_dragStartTop = Top;
			PanelBorder.CaptureMouse();
			e.Handled = true;
		}
	}

	private void OnPanelMouseMove(object sender, MouseEventArgs e)
	{
		if (_dragging && GetCursorPos(out var lpPoint))
		{
			DpiScale dpi = VisualTreeHelper.GetDpi(this);
			Left = _dragStartLeft + ((double)lpPoint.X - _dragStartCursor.X) / dpi.DpiScaleX;
			Top = _dragStartTop + ((double)lpPoint.Y - _dragStartCursor.Y) / dpi.DpiScaleY;
		}
	}

	private void OnPanelMouseUp(object sender, MouseButtonEventArgs e)
	{
		if (_dragging)
		{
			_dragging = false;
			PanelBorder.ReleaseMouseCapture();
		}
	}

	private void InstallMouseHook()
	{
		if (_mouseHook == IntPtr.Zero)
		{
			_mouseHookProc = MouseHookProc;
			_mouseHook = SetWindowsHookEx(14, _mouseHookProc, IntPtr.Zero, 0u);
		}
	}

	private void UninstallMouseHook()
	{
		if (_mouseHook != IntPtr.Zero)
		{
			UnhookWindowsHookEx(_mouseHook);
			_mouseHook = IntPtr.Zero;
			_mouseHookProc = null;
		}
	}

	private nint MouseHookProc(int nCode, nint wParam, nint lParam)
	{
		if (nCode >= 0 && IsVisible)
		{
			int num = ((IntPtr)wParam).ToInt32();
			if (num == WM_LBUTTONDOWN)
			{
				// 用命中测试判断点击是否真的落在面板上（而不是只看矩形）：
				// 面板被其它窗口遮挡时，被遮挡的那部分不能响应点击
				nint under = GetRootWindowAt(lParam);
				if (under == _hwnd)
				{
					// 面板内只有搜索框需要键盘输入；其它位置（符号、分类、标题栏按钮）
					// 保持"点击不抢前台"，避免正在输入的程序变色/失焦
					bool overSearchBox = IsCursorOverSearchBox();
					SetNoActivate(!overSearchBox);
					// 与普通窗口一致：点击面板内任意位置都把它提到上层
					// （搜索框会正常激活，激活本身就会把面板提到最前）
					if (!overSearchBox)
					{
						RaisePanelWithoutActivating();
						// 面板是"不激活"窗口，WPF 不会自动转移键盘焦点，
						// 点其它位置时手动让搜索框失焦
						Dispatcher.BeginInvoke(new Action(ClearSearchFocus), DispatcherPriority.Input);
					}
				}
				else if (!IsOwnProcessWindow(under) && !_isPinned && !_symbolMenuOpen)
				{
					// 点击面板外部：若被点的窗口本来就是前台（不会触发前台变化事件），
					// 这里先把面板退到它下面；否则交给 OnForegroundChanged 处理
					ReleaseAutoTopmost(under);
					Dispatcher.BeginInvoke(new Action(HandleOutsideClick), DispatcherPriority.Background);
				}
			}
			else if (num == WM_MOUSEWHEEL)
			{
				// 被遮挡时滚轮应交给上层窗口，不能滚动面板
				if (GetRootWindowAt(lParam) != _hwnd)
				{
					return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
				}
				MSLLHOOKSTRUCT mSLLHOOKSTRUCT = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
				DpiScale dpi = VisualTreeHelper.GetDpi(this);
				Point point = new Point((double)mSLLHOOKSTRUCT.pt.X / dpi.DpiScaleX, (double)mSLLHOOKSTRUCT.pt.Y / dpi.DpiScaleY);
				Point point2 = new Point(point.X - Left, point.Y - Top);
				{
					int delta = (short)((mSLLHOOKSTRUCT.mouseData >> 16) & 0xFFFF);
					if (point2.Y < 80.0)
					{
						Dispatcher.BeginInvoke((Action)(() =>
						{
							CategoryScroll.ScrollToHorizontalOffset(CategoryScroll.HorizontalOffset - (double)delta * 0.6);
						}));
					}
					else
					{
						Dispatcher.BeginInvoke((Action)(() =>
						{
							SymbolScroll.ScrollToVerticalOffset(SymbolScroll.VerticalOffset - (double)delta * 0.6);
						}));
					}
					return 1;
				}
			}
		}
		return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
	}

	private void HandleOutsideClick()
	{
		// 记录点击到的前台窗口，供后续粘贴符号使用
		nint foregroundWindow = GetForegroundWindow();
		if (!IsOwnProcessWindow(foregroundWindow))
		{
			_pasteTarget = foregroundWindow;
		}
	}

	/// <summary>取鼠标位置下的顶层窗口</summary>
	private static nint GetRootWindowAt(nint lParam)
	{
		MSLLHOOKSTRUCT info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
		nint hwnd = WindowFromPoint(info.pt);
		return (hwnd != IntPtr.Zero) ? GetAncestor(hwnd, 2u) : IntPtr.Zero;
	}

	/// <summary>
	/// 前台窗口变化（鼠标点击、Alt+Tab、任务栏等任何方式）：
	/// 收起临时置顶，并把面板退到新前台窗口的下面，表现与普通窗口一致。
	/// 面板自己成为前台（点搜索框）时不做处理。
	/// </summary>
	private void OnForegroundChanged(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
	{
		if (hwnd == IntPtr.Zero || IsOwnProcessWindow(hwnd))
		{
			return;
		}
		ReleaseAutoTopmost(hwnd);
	}

	/// <summary>
	/// 收回临时置顶，并把成为前台的那个窗口提到普通窗口层最顶部，
	/// 面板自然就落在它下面（与普通窗口的遮挡结果一致）。
	/// 注意不能反过来"把面板插到该窗口下方"：事件到达时该窗口可能还没被系统抬到顶部，
	/// 那样会把面板插到最底部。
	/// </summary>
	private void ReleaseAutoTopmost(nint foregroundWindow)
	{
		if (!_autoTopmost || _isPinned)
		{
			return;
		}
		_autoTopmost = false;
		Topmost = false;
		if (_hwnd == IntPtr.Zero || foregroundWindow == IntPtr.Zero
			|| IsOwnProcessWindow(foregroundWindow) || foregroundWindow == GetShellWindow())
		{
			// 自己的窗口或桌面/外壳：只收回置顶，不重排
			return;
		}
		SetWindowPos(foregroundWindow, HWND_TOP, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
	}

	public void ShowNearBall(Point ballCenter, bool resetPosition = false)
	{
		nint foregroundWindow = GetForegroundWindow();
		if (!IsOwnProcessWindow(foregroundWindow))
		{
			_pasteTarget = foregroundWindow;
		}

		// 面板已经打开时（如再次单击托盘图标）只把它重新唤醒到上层，
		// 不动位置、也不重置界面状态；只有隐藏状态下唤醒、或显式要求重置位置时才继续
		if (!resetPosition && IsVisible)
		{
			Activate();
			return;
		}

		List<string> categories = SymbolDatabaseService.GetCategories();
		CategoryTabs.ItemsSource = categories;
		// 先清空搜索框：若上次有搜索内容，TextChanged 会把列表还原成当前分类
		SearchBox.Text = "";
		// 只在首次打开或之前选中的分类已不存在时才回退到第一个分类，
		// 同一进程内关闭再打开窗口始终保持用户上次选中的分类
		if (categories.Count > 0)
		{
			if (string.IsNullOrEmpty(_currentCategory) || !categories.Contains(_currentCategory))
			{
				_currentCategory = categories[0];
			}
			// 内容没变就复用已加载好的列表，打开窗口时不会再出现加载过程
			if (_itemsFiltered || !string.Equals(_shownCategory, _currentCategory, StringComparison.Ordinal) || _symbolItems.Count == 0)
			{
				ShowSymbols(SymbolDatabaseService.GetByCategory(_currentCategory));
				_shownCategory = _currentCategory;
				_itemsFiltered = false;
			}
		}

		// 默认保持上次的位置（用户拖动过）；resetPosition=true 时（托盘右键"恢复位置"）
		// 或首次显示、位置已不在任何屏幕内时才回到悬浮球附近
		if (!resetPosition && _savedPos.HasValue && double.IsFinite(_savedPos.Value.X) && double.IsFinite(_savedPos.Value.Y))
		{
			// 按保存的位置所在屏幕检查有效性
			Rect workArea = GetWorkAreaAtLogical(_savedPos.Value.X + ActualWidth / 2, _savedPos.Value.Y + ActualHeight / 2);
			var p = _savedPos.Value;
			if (p.X + ActualWidth > workArea.Left - 20 && p.X < workArea.Right + 20 &&
				p.Y + ActualHeight > workArea.Top - 20 && p.Y < workArea.Bottom + 20)
			{
				Left = p.X;
				Top = p.Y;
			}
			else
			{
				PositionNear(ballCenter);
				_savedPos = new Point(Left, Top);
			}
		}
		else
		{
			PositionNear(ballCenter);
			_savedPos = new Point(Left, Top);
		}

		_autoTopmost = false;
		Topmost = _isPinned;
		// 打开面板时按普通窗口处理：先取消"点击不抢前台"，Show/Activate 才能生效
		SetNoActivate(false);
		Show();
		// 普通窗口行为：显示时激活并置于最前，之后的叠放完全由 Windows 管理
		Activate();
		SymbolScroll.ScrollToTop();
		Dispatcher.BeginInvoke(new Action(UpdateTabStyles), DispatcherPriority.Background);
	}

	protected override void OnLocationChanged(EventArgs e)
	{
		base.OnLocationChanged(e);
		// 用户拖动面板后记住位置
		if (IsVisible && _dragging == false)
		{
			_savedPos = new Point(Left, Top);
		}
	}

	// 必须是 public：App 通过 SymbolPanelWindow 类型的引用调用 _panel.Hide()，
	// protected 的 new Hide 不会被绑定（会走到 Window.Hide），导致位置没被保存，
	// 下次托盘/悬浮球唤醒时判定为"无保存位置"而回到悬浮球附近
	public new void Hide()
	{
		if (IsVisible)
		{
			_savedPos = new Point(Left, Top);
		}
		// 隐藏后回到普通窗口状态
		_autoTopmost = false;
		Topmost = _isPinned;
		base.Hide();
	}

	private void UpdateTabStyles()
	{
		Style style = TryFindResource("CategoryTabStyle") as Style;
		Style style2 = TryFindResource("CategoryTabActiveStyle") as Style;
		Style style3 = TryFindResource("CategoryTabDimmedStyle") as Style;
		if (style == null || style2 == null || style3 == null)
		{
			return;
		}
		bool flag = !string.IsNullOrWhiteSpace(SearchBox.Text);
		for (int i = 0; i < CategoryTabs.Items.Count; i++)
		{
			DependencyObject dependencyObject = CategoryTabs.ItemContainerGenerator.ContainerFromIndex(i);
			if (dependencyObject != null && CategoryTabs.Items[i] is string text)
			{
				Button button = FindChild<Button>(dependencyObject);
				if (button != null)
				{
					Style style4;
					if (flag)
					{
						style4 = style3;
					}
					else
					{
						style4 = ((text == _currentCategory) ? style2 : style);
					}
					// 样式没变就不要重复赋值，避免整排分类标签重新套用样式
					if (button.Style != style4)
					{
						button.Style = style4;
					}
				}
			}
		}
	}

	private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
	{
		int childrenCount = VisualTreeHelper.GetChildrenCount(parent);
		for (int i = 0; i < childrenCount; i++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(parent, i);
			if (child is T result)
			{
				return result;
			}
			T val = FindChild<T>(child);
			if (val != null)
			{
				return val;
			}
		}
		return null;
	}

	private void PositionNear(Point anchor)
	{
		// anchor 是悬浮球中心（WPF 逻辑坐标），查它所在屏幕的工作区
		Rect workArea = GetWorkAreaAtLogical(anchor.X, anchor.Y);
		double val = anchor.X - 288.0;
		double val2 = anchor.Y - 24.0 - 8.0 - 258.0;
		val = Math.Max(workArea.Left + 4.0, Math.Min(val, workArea.Right - 320.0 - 4.0));
		val2 = Math.Max(workArea.Top + 4.0, Math.Min(val2, workArea.Bottom - 258.0 - 4.0));
		Left = val;
		Top = val2;
	}

	private void CategoryTab_Click(object sender, RoutedEventArgs e)
	{
		if (sender is Button { Content: string content })
		{
			_currentCategory = content;
			ShowSymbols(SymbolDatabaseService.GetByCategory(content));
			_shownCategory = content;
			_itemsFiltered = false;
			SymbolScroll.ScrollToTop();
			UpdateTabStyles();
			// 与点符号一致：切换分类后也把面板提到上层（不激活、不抢前台焦点）
			RaisePanelWithoutActivating();
		}
	}

	private void OnPinClick(object sender, RoutedEventArgs e)
	{
		_isPinned = !_isPinned;
		_autoTopmost = false;
		Topmost = _isPinned;
		if (!_isPinned && _hwnd != IntPtr.Zero)
		{
			SetWindowPos(_hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, 19u);
		}
		PinIcon.Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_isPinned ? "#FF6366F1" : "#FFA6ADC8"));
	}

	private void OnCloseClick(object sender, RoutedEventArgs e)
	{
		Hide();
	}

	private async void Symbol_Click(object sender, RoutedEventArgs e)
	{
		if (!(sender is Button { DataContext: SymbolItem dataContext }))
		{
			return;
		}
		string symbol = ((dataContext.InputText.Length > 0) ? dataContext.InputText : dataContext.Char);
		nint target = _pasteTarget;
		// 点在符号上不会激活面板（见 WndProc），此时真正的前台窗口就是输入目标；
		// 以它为准，避免用户中途切换窗口后仍把符号发给缓存的旧窗口
		if (!IsActive)
		{
			nint foregroundWindow = GetForegroundWindow();
			if (foregroundWindow != IntPtr.Zero && !IsOwnProcessWindow(foregroundWindow))
			{
				_pasteTarget = foregroundWindow;
				target = foregroundWindow;
			}
		}
		if (target != IntPtr.Zero && (IsOwnProcessWindow(target) || !IsWindow(target)))
		{
			target = IntPtr.Zero;
		}
		// 目标窗口本来就在前台（点符号不会改变前台）：直接模拟按键/粘贴。
		// 这种输入方式对浏览器等不做消息处理的程序才有效，且不需要切换窗口，不会闪烁
		if (target != IntPtr.Zero && GetForegroundWindow() == target)
		{
			if (CopyOnClick)
			{
				_inputEngine.TryPaste(symbol);
			}
			else
			{
				_inputEngine.TryInput(symbol);
			}
			RaisePanelWithoutActivating();
			return;
		}
		// 目标在后台时优先消息直发，避免把前台切走
		if (target != IntPtr.Zero)
		{
			bool sent = (CopyOnClick ? _inputEngine.TryPasteTo(target, symbol) : _inputEngine.TryInputTo(target, symbol));
			if (sent)
			{
				RaisePanelWithoutActivating();
				return;
			}
		}
		// 目标窗口不支持消息输入时，才回退到"切前台 + 模拟按键"
		if (target != IntPtr.Zero && GetForegroundWindow() != target)
		{
			SetForegroundWindow(target);
			for (int i = 0; i < 10; i++)
			{
				if (GetForegroundWindow() == target)
				{
					break;
				}
				await Task.Delay(30);
			}
			if (GetForegroundWindow() != target)
			{
				await Task.Delay(60);
			}
		}
		if (CopyOnClick)
		{
			_inputEngine.TryPaste(symbol);
		}
		else
		{
			_inputEngine.TryInput(symbol);
		}
		// 回退路径会临时把前台切给目标窗口，输入完成后把面板提回最前，
		// 但又不再抢焦点，避免"点一下符号面板就被压到后面"
		RaisePanelWithoutActivating();
	}

	/// <summary>
	/// 把面板提到最上层但不激活它（目标程序的输入焦点保持不变）。
	/// 因为面板是"不激活"窗口，系统不会因点击而提层，只能手动临时置顶；
	/// 其它窗口一成为前台就由 OnForegroundChanged 收回置顶并让位，保持普通窗口的遮挡效果。
	/// </summary>
	private void RaisePanelWithoutActivating()
	{
		if (!IsVisible || _hwnd == IntPtr.Zero || _isPinned || _autoTopmost)
		{
			return;
		}
		_autoTopmost = true;
		Topmost = true;
	}

	private void Symbol_RightMouseUp(object sender, MouseButtonEventArgs e)
	{
		if (sender is Button placementTarget)
		{
			e.Handled = true;
			if (FindResource("SymbolCopyMenu") is ContextMenu contextMenu)
			{
				contextMenu.PlacementTarget = placementTarget;
				contextMenu.Placement = PlacementMode.Bottom;
				contextMenu.VerticalOffset = 2.0;
				contextMenu.HorizontalOffset = -6.0;
				contextMenu.IsOpen = true;
			}
		}
	}

	private void SymbolMenu_Opened(object sender, RoutedEventArgs e)
	{
		_symbolMenuOpen = true;
	}

	private void SymbolMenu_Closed(object sender, RoutedEventArgs e)
	{
		_symbolMenuOpen = false;
	}

	private void CopySymbol_Click(object sender, RoutedEventArgs e)
	{
		if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: Button { DataContext: SymbolItem dataContext } } })
		{
			try
			{
				Clipboard.SetText((dataContext.InputText.Length > 0) ? dataContext.InputText : dataContext.Char);
			}
			catch
			{
			}
		}
	}

	private void SearchBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
	{
		BeginSearchInput();
	}

	private void BeginSearchInput()
	{
		if (!IsActive)
		{
			nint foregroundWindow = GetForegroundWindow();
			if (foregroundWindow != IntPtr.Zero && foregroundWindow != _hwnd)
			{
				_pasteTarget = foregroundWindow;
			}
			Activate();
		}
	}

	/// <summary>让搜索框失去焦点（面板是"不激活"窗口，点击其它位置时 WPF 不会自动转移焦点）</summary>
	private void ClearSearchFocus()
	{
		if (SearchBox.IsKeyboardFocusWithin)
		{
			Keyboard.ClearFocus();
		}
	}

	private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		string text = SearchBox.Text?.Trim() ?? "";
		ClearBtn.Visibility = ((text.Length <= 0) ? Visibility.Collapsed : Visibility.Visible);
		SearchPlaceholder.Visibility = ((text.Length > 0) ? Visibility.Collapsed : Visibility.Visible);
		Color color = (Color)ColorConverter.ConvertFromString((text.Length > 0) ? "#FF6366F1" : "#FF45475A");
		SearchBorder.BorderBrush = new SolidColorBrush(color);
		List<SymbolItem> items = ((text.Length == 0) ? SymbolDatabaseService.GetByCategory(_currentCategory) : SymbolDatabaseService.Search(text));
		ShowSymbols(items);
		_itemsFiltered = text.Length > 0;
		_shownCategory = ((text.Length == 0) ? _currentCategory : null);
		SymbolScroll.ScrollToTop();
		UpdateTabStyles();
	}

	private void ClearBtn_Click(object sender, MouseButtonEventArgs e)
	{
		BeginSearchInput();
		SearchBox.Text = "";
		SearchBox.Focus();
	}

	private void OnKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Escape)
		{
			Hide();
		}
	}
}

/// <summary>
/// 支持一次性替换/批量添加的 ObservableCollection：
/// 批量操作只触发一次 Reset 通知，避免逐项 Add 造成多次布局（符号一行一行出现）。
/// </summary>
internal sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
	private bool _suppress;

	public void ReplaceAll(IEnumerable<T> items)
	{
		_suppress = true;
		try
		{
			Items.Clear();
			foreach (T item in items)
			{
				Items.Add(item);
			}
		}
		finally
		{
			_suppress = false;
		}
		NotifyReset();
	}

	/// <summary>
	/// 数量相同时逐项替换（Replace 通知）而不是 Reset：
	/// Reset 会销毁并重建所有容器，切换分类时明显卡顿；逐项替换复用现有容器，开销小得多。
	/// </summary>
	public void ReplaceInPlace(IList<T> items)
	{
		if (items.Count != Items.Count)
		{
			ReplaceAll(items);
			return;
		}
		for (int i = 0; i < items.Count; i++)
		{
			if (!ReferenceEquals(Items[i], items[i]))
			{
				this[i] = items[i];
			}
		}
	}

	public void AddRange(IList<T> items)
	{
		// 不能用自定义的区间 Add 通知：ListCollectionView 的索引校验会抛异常。
		// 这里在同一调用内逐项追加，布局只会在一轮 dispatcher 结束后执行一次，
		// 界面依然是一次性出现，不会逐行渲染。
		foreach (T item in items)
		{
			Add(item);
		}
	}

	private void NotifyReset()
	{
		OnPropertyChanged(new PropertyChangedEventArgs("Count"));
		OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
		OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
	}

	protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
	{
		if (!_suppress)
		{
			base.OnCollectionChanged(e);
		}
	}
}
