using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace SymbolsTool;

public partial class FloatingBallWindow : Window, IComponentConnector
{
	private struct POINT
	{
		public int X;

		public int Y;
	}

	private enum SnapSide
	{
		None,
		Left,
		Right,
		Top,
		Bottom
	}

	private delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);

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

	private const int WS_EX_TOPMOST = 8;

	private const int WS_EX_NOACTIVATE = 134217728;

	private const int WM_MOUSEACTIVATE = 33;

	private const int MA_NOACTIVATE = 3;

	private const double BallRadius = 24.0;

	private const double WindowPad = 18.0;

	private const double SnapDistance = 80.0;

	private const double UnfoldGap = 12.0;

	private const double DragThreshold = 4.0;

	private bool _isPressing;

	private bool _isDragging;

	private Point _pressCursor;

	private double _pressLeft;

	private double _pressTop;

	private DragTrailWindow? _trail;

	private SnapSide _currentSnap;

	private bool _isUnfolded;

	private SnapSide _dragPreviewSnap;

	/// <summary>吸附所在屏幕的工作区（用于悬停展开判断与裁剪）</summary>
	private Rect _snapWorkArea;

	private bool _hidden;

	private bool _closing;

	private SnapSide _savedSnap;

	private double _savedLeft;

	private double _savedTop;

	private const int WH_MOUSE_LL = 14;

	private const int WM_MOUSEMOVE = 512;

	private nint _mouseHook = IntPtr.Zero;

	private LowLevelMouseProc? _mouseHookProc;

	private readonly DispatcherTimer _hoverFallback = new DispatcherTimer
	{
		Interval = TimeSpan.FromMilliseconds(80L)
	};

	private double _foldLeft;

	private double _foldTop;

	private double _unfoldLeft;

	private double _unfoldTop;

	private Rect _lastClip = Rect.Empty;

	private static readonly DependencyProperty AnimLeftProperty = DependencyProperty.Register("AnimLeft", typeof(double), typeof(FloatingBallWindow), new PropertyMetadata(0.0, (DependencyObject d, DependencyPropertyChangedEventArgs e) =>
	{
		FloatingBallWindow floatingBallWindow = (FloatingBallWindow)d;
		floatingBallWindow.Left = (double)e.NewValue;
		floatingBallWindow.SyncClipToWorkArea();
	}));

	private static readonly DependencyProperty AnimTopProperty = DependencyProperty.Register("AnimTop", typeof(double), typeof(FloatingBallWindow), new PropertyMetadata(0.0, (DependencyObject d, DependencyPropertyChangedEventArgs e) =>
	{
		FloatingBallWindow floatingBallWindow = (FloatingBallWindow)d;
		floatingBallWindow.Top = (double)e.NewValue;
		floatingBallWindow.SyncClipToWorkArea();
	}));

	public bool IsClosing => _closing;

	private double AnimLeft
	{
		get
		{
			return (double)GetValue(AnimLeftProperty);
		}
		set
		{
			SetValue(AnimLeftProperty, value);
		}
	}

	private double AnimTop
	{
		get
		{
			return (double)GetValue(AnimTopProperty);
		}
		set
		{
			SetValue(AnimTopProperty, value);
		}
	}

	public event EventHandler? BallClicked;

	public event EventHandler? DragStarted;

	#region 多屏支持 Win32 API

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct MONITORINFOEX
	{
		public int cbSize;
		public RECT rcMonitor;   // 整个显示器区域（物理像素）
		public RECT rcWork;      // 工作区（排除任务栏，物理像素）
		public int dwFlags;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
		public string szDevice;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct RECT
	{
		public int Left; public int Top; public int Right; public int Bottom;
	}

	[DllImport("user32.dll")]
	private static extern nint MonitorFromPoint(POINT pt, uint dwFlags);

	[DllImport("user32.dll")]
	private static extern bool GetMonitorInfoW(nint hMonitor, IntPtr lpmi);

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

	[DllImport("shcore.dll")]
	private static extern int GetDpiForMonitor(nint hMonitor, uint dpiType, out uint dpiX, out uint dpiY);

	[DllImport("user32.dll")]
	private static extern bool EnumDisplayMonitors(nint hDC, IntPtr lprcClip, EnumMonitorsProc lpfnEnum, int dwData);

	private delegate int EnumMonitorsProc(nint hMon, nint hdcMon, IntPtr lprcMon, int lParam);

	private const uint MONITOR_DEFAULTTONEAREA = 2;
	private const uint MDT_EFFECTIVE_DPI = 0;

	/// <summary>鼠标所在屏幕的工作区（逻辑坐标）</summary>
	private static Rect GetWorkAreaOfCursor()
	{
		if (!GetCursorPos(out var pt))
		{
			return SystemParameters.WorkArea;
		}
		return GetWorkAreaAtPhysical(pt);
	}

	/// <summary>悬浮球当前所在屏幕的工作区（逻辑坐标）</summary>
	private Rect GetWorkAreaOfWindow()
	{
		nint hwnd = new WindowInteropHelper(this).Handle;
		if (hwnd == IntPtr.Zero) return SystemParameters.WorkArea;
		if (!GetWindowRect(hwnd, out var rect)) return SystemParameters.WorkArea;

		POINT centerPhy = new POINT
		{
			X = (rect.Left + rect.Right) / 2,
			Y = (rect.Top + rect.Bottom) / 2
		};
		return GetWorkAreaAtPhysical(centerPhy);
	}

	/// <summary>按物理像素 POINT 查所在屏幕的逻辑坐标工作区（MonitorFromPoint 兜底遍历）</summary>
	private static Rect GetWorkAreaAtPhysical(POINT pt)
	{
		// MONITORINFOEX 大小 = sizeof(MONITORINFO) + 32 WCHAR 设备名 = 40 + 64 = 104 字节（Unicode 下）
		const int MONITORINFOEX_SIZE = 104;

		nint hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREA);
		bool fromApi = hMon != IntPtr.Zero;
		if (!fromApi)
		{
			nint foundMon = IntPtr.Zero;
			int Enum(nint hm, nint hd, IntPtr lp, int lp2) {
				var rc = Marshal.PtrToStructure<RECT>(lp);
				if (pt.X >= rc.Left && pt.X < rc.Right && pt.Y >= rc.Top && pt.Y < rc.Bottom)
				{
					foundMon = hm;
					return 0;
				}
				return 1;
			}
			EnumDisplayMonitors(0, IntPtr.Zero, Enum, 0);
			hMon = foundMon;
		}
		if (hMon == IntPtr.Zero) return SystemParameters.WorkArea;

		var buf = new byte[MONITORINFOEX_SIZE];
		BitConverter.GetBytes(MONITORINFOEX_SIZE).CopyTo(buf, 0);
		GCHandle handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
		try
		{
			bool ok = GetMonitorInfoW(hMon, handle.AddrOfPinnedObject());
			RECT rcWork = new RECT();
			if (ok)
			{
				// cbSize(4) + rcMonitor(16) + rcWork(16) + dwFlags(4) = 40 偏移开始
				int offWork = 4 + 16;
				rcWork = new RECT
				{
					Left = BitConverter.ToInt32(buf, offWork),
					Top = BitConverter.ToInt32(buf, offWork + 4),
					Right = BitConverter.ToInt32(buf, offWork + 8),
					Bottom = BitConverter.ToInt32(buf, offWork + 12),
				};
			}
			else
			{
				return SystemParameters.WorkArea;
			}

			double dpiScale = 1.0;
			try
			{
				if (GetDpiForMonitor(hMon, MDT_EFFECTIVE_DPI, out uint dx, out _) == 0 && dx > 0)
					dpiScale = dx / 96.0;
			}
			catch { dpiScale = 1.0; }

			if (rcWork.Left == 0 && rcWork.Top == 0 && rcWork.Right == 0 && rcWork.Bottom == 0)
				return SystemParameters.WorkArea;

			return new Rect(rcWork.Left / dpiScale, rcWork.Top / dpiScale,
				(rcWork.Right - rcWork.Left) / dpiScale,
				(rcWork.Bottom - rcWork.Top) / dpiScale);
		}
		finally
		{
			handle.Free();
		}
	}

	#endregion

	[DllImport("user32.dll")]
	private static extern int GetWindowLong(nint h, int n);

	[DllImport("user32.dll")]
	private static extern int SetWindowLong(nint h, int n, int s);

	[DllImport("user32.dll")]
	private static extern bool GetCursorPos(out POINT lpPoint);

	[DllImport("gdi32.dll")]
	private static extern nint CreateRectRgn(int x1, int y1, int x2, int y2);

	[DllImport("user32.dll")]
	private static extern int SetWindowRgn(nint hWnd, nint hRgn, bool bRedraw);

	[DllImport("gdi32.dll")]
	private static extern bool DeleteObject(nint hObject);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern nint SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, nint hMod, uint dwThreadId);

	[DllImport("user32.dll")]
	private static extern bool UnhookWindowsHookEx(nint hhk);

	[DllImport("user32.dll")]
	private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

	[DllImport("kernel32.dll", CharSet = CharSet.Auto)]
	private static extern nint GetModuleHandle(string? lpModuleName);

	public FloatingBallWindow()
	{
		InitializeComponent();
		BallBrush.Opacity = 0.3;
		_mouseHookProc = MouseHookProc;
		_mouseHook = SetWindowsHookEx(14, _mouseHookProc, GetModuleHandle(null), 0u);
		_hoverFallback.Tick += (object? s, EventArgs e) =>
		{
			PollSnapHover();
		};
		_hoverFallback.Start();
	}

	private void PollSnapHover()
	{
		if (!_hidden && _currentSnap != SnapSide.None && !_isPressing && !_isDragging && GetCursorPos(out var lpPoint))
		{
			EvaluateSnapHover(lpPoint.X, lpPoint.Y);
		}
	}

	protected override void OnClosing(CancelEventArgs e)
	{
		_closing = true;
		base.OnClosing(e);
	}

	protected override void OnClosed(EventArgs e)
	{
		if (_mouseHook != IntPtr.Zero)
		{
			UnhookWindowsHookEx(_mouseHook);
			_mouseHook = IntPtr.Zero;
		}
		_mouseHookProc = null;
		base.OnClosed(e);
	}

	private void AnimateWindowTo(double left, double top, int ms = 140)
	{
		QuadraticEase easingFunction = new QuadraticEase
		{
			EasingMode = EasingMode.EaseOut
		};
		DoubleAnimation animation = new DoubleAnimation(Left, left, TimeSpan.FromMilliseconds(ms))
		{
			EasingFunction = easingFunction
		};
		DoubleAnimation animation2 = new DoubleAnimation(Top, top, TimeSpan.FromMilliseconds(ms))
		{
			EasingFunction = easingFunction
		};
		BeginAnimation(AnimLeftProperty, animation);
		BeginAnimation(AnimTopProperty, animation2);
	}

	private void StopWindowAnimation()
	{
		SetValue(AnimLeftProperty, Left);
		SetValue(AnimTopProperty, Top);
		BeginAnimation(AnimLeftProperty, null);
		BeginAnimation(AnimTopProperty, null);
	}

	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);
		nint handle = new WindowInteropHelper(this).Handle;
		int windowLong = GetWindowLong(handle, -20);
		SetWindowLong(handle, -20, windowLong | 0x80 | 8 | 0x8000000);
		HwndSource.FromHwnd(handle)?.AddHook(BallWndProc);
	}

	private nint BallWndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
	{
		if (msg == 33)
		{
			handled = true;
			return 3;
		}
		return IntPtr.Zero;
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		Rect workArea = SystemParameters.WorkArea;
		Left = workArea.Right - Width - 80.0;
		Top = workArea.Bottom - Height - 80.0;
	}

	private void OnMouseEnter(object sender, MouseEventArgs e)
	{
		if (!_isPressing && !_isDragging && _currentSnap == SnapSide.None)
		{
			SetBallOpacity(1.0);
			SetBallScale(1.12);
			SetShadow(strong: true);
		}
	}

	private void OnMouseLeave(object sender, MouseEventArgs e)
	{
		if (!_isPressing && !_isDragging && _currentSnap == SnapSide.None)
		{
			SetBallOpacity(0.3);
			SetBallScale(1.0);
			SetShadow(strong: false);
		}
	}

	private nint MouseHookProc(int nCode, nint wParam, nint lParam)
	{
		try
		{
			if (nCode >= 0 && wParam == 512 && !_hidden && _currentSnap != SnapSide.None && !_isPressing && !_isDragging)
			{
				MSLLHOOKSTRUCT mSLLHOOKSTRUCT = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
				EvaluateSnapHover(mSLLHOOKSTRUCT.pt.X, mSLLHOOKSTRUCT.pt.Y);
			}
		}
		catch
		{
		}
		return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
	}

	private void EvaluateSnapHover(int screenX, int screenY)
	{
		if (_currentSnap == SnapSide.None)
		{
			return;
		}
		DpiScale dpi = VisualTreeHelper.GetDpi(this);
		Point point = new Point((double)screenX / dpi.DpiScaleX, (double)screenY / dpi.DpiScaleY);
		// 用吸附时所在屏幕的工作区判断悬停展开/折叠
		Rect workArea = _snapWorkArea.IsEmpty ? GetWorkAreaOfWindow() : _snapWorkArea;
		Rect rect = new Rect(_foldLeft + 18.0, _foldTop + 18.0, 48.0, 48.0);
		Rect rect2 = new Rect(_unfoldLeft + 18.0, _unfoldTop + 18.0, 48.0, 48.0);
		rect.Intersect(workArea);
		rect2.Intersect(workArea);
		if (rect.IsEmpty || rect2.IsEmpty)
		{
			return;
		}
		if (!_isUnfolded)
		{
			Rect rect3 = rect;
			rect3.Inflate(2.0, 2.0);
			rect3.Intersect(workArea);
			if (rect3.Contains(point))
			{
				UnfoldSnap();
			}
		}
		else
		{
			Rect rect4 = rect2;
			rect4.Inflate(2.0, 2.0);
			if (!rect4.Contains(point))
			{
				FoldSnap();
			}
		}
	}

	protected override void OnLostMouseCapture(MouseEventArgs e)
	{
		base.OnLostMouseCapture(e);
		_isPressing = false;
		_isDragging = false;
		_trail?.Hide();
	}

	private void OnMouseDown(object sender, MouseButtonEventArgs e)
	{
		if (_currentSnap != SnapSide.None)
		{
			StopWindowAnimation();
			Left = _unfoldLeft;
			Top = _unfoldTop;
			_isUnfolded = false;
			SyncClipToWorkArea();
		}
		GetCursorPos(out var lpPoint);
		_pressCursor = new Point(lpPoint.X, lpPoint.Y);
		_pressLeft = Left;
		_pressTop = Top;
		_isPressing = true;
		_isDragging = false;
		_dragPreviewSnap = SnapSide.None;
		CaptureMouse();
	}

	private void OnMouseMove(object sender, MouseEventArgs e)
	{
		if (!_isPressing)
		{
			return;
		}
		GetCursorPos(out var lpPoint);
		DpiScale dpi = VisualTreeHelper.GetDpi(this);
		double num = ((double)lpPoint.X - _pressCursor.X) / dpi.DpiScaleX;
		double num2 = ((double)lpPoint.Y - _pressCursor.Y) / dpi.DpiScaleY;
		if (!_isDragging)
		{
			if (Math.Abs(num) < 4.0 && Math.Abs(num2) < 4.0)
			{
				return;
			}
			_isDragging = true;
			_currentSnap = SnapSide.None;
			_isUnfolded = false;
			ClearClip();
			DragStarted?.Invoke(this, EventArgs.Empty);
			ShowTrail();
		}
		double num3 = _pressLeft + num;
		double num4 = _pressTop + num2;

		// 吸附边以"鼠标所在屏幕"为准：鼠标在哪块屏，就吸附哪块屏的边
		SnapSide snapSideAt = GetSnapSideAt(num3 + Width / 2.0, num4 + Height / 2.0);
		Rect cursorWorkArea = GetWorkAreaOfCursor();

		if (snapSideAt != _dragPreviewSnap)
		{
			_dragPreviewSnap = snapSideAt;
			if (snapSideAt == SnapSide.None)
			{
				SetBallOpacity(1.0);
				SetBallScale(1.12);
				SetShadow(strong: true);
			}
			else
			{
				// 与真吸附一致的外观
				SetBallOpacity(0.75);
				SetBallScale(1.0);
				SetShadow(strong: false);
			}
		}

		if (snapSideAt == SnapSide.None)
		{
			// 自由拖动：不做裁剪，允许穿过屏幕边界
			Left = num3;
			Top = num4;
			ClearClip();
		}
		else
		{
			// 伪吸附：位置与真吸附完全一致（一半超出边界、一半留在当前屏），
			// 但仍可继续拖动；裁剪到当前屏工作区，使超出的一半不显示
			var (snapL, snapT) = GetSnappedPosition(snapSideAt, num3, num4, Width, Height);
			Left = snapL;
			Top = snapT;
			ClipToWorkArea(cursorWorkArea);
		}

		if (_trail != null)
		{
			_trail.Left = Left + (Width - _trail.Width) / 2.0;
			_trail.Top = Top + (Height - _trail.Height) / 2.0;
			// 虚环与悬浮球用同一块屏幕的裁剪，保证截半位置一致
			if (snapSideAt == SnapSide.None)
			{
				_trail.ClearWorkAreaClip();
			}
			else
			{
				_trail.ClipToWorkArea(cursorWorkArea);
			}
		}
	}

	private void OnMouseUp(object sender, MouseButtonEventArgs e)
	{
		if (_isPressing)
		{
			bool isDragging = _isDragging;
			_isPressing = false;
			_isDragging = false;
			ReleaseMouseCapture();
			if (!isDragging)
			{
				ClickBall();
				return;
			}
			_trail?.Hide();
			SnapToNearestEdge();
		}
	}

	public void HideForPanel()
	{
		if (!_closing && !_hidden)
		{
			if (!IsVisible)
			{
				_hidden = false;
				return;
			}
			_hidden = true;
			_savedSnap = _currentSnap;
			_savedLeft = Left;
			_savedTop = Top;
			StopWindowAnimation();
			_isPressing = false;
			_isDragging = false;
			_isUnfolded = false;
			_trail?.Hide();
			Hide();
		}
	}

	public void ShowAfterPanel(bool restore = true)
	{
		if (_closing || !_hidden)
		{
			return;
		}
		_hidden = false;
		if (restore)
		{
			if (_savedSnap != SnapSide.None)
			{
				Show();
				SnapTo(_savedSnap, animate: false, _snapWorkArea.IsEmpty ? null : _snapWorkArea);
				return;
			}
			StopWindowAnimation();
			_currentSnap = SnapSide.None;
			_isUnfolded = false;
			Left = _savedLeft;
			Top = _savedTop;
			ClearClip();
			SetBallOpacity(0.3);
			SetBallScale(1.0);
			SetShadow(strong: false);
			Show();
		}
	}

	private void ShowTrail()
	{
		if (_trail == null)
		{
			_trail = new DragTrailWindow();
		}
		_trail.Left = Left + (Width - _trail.Width) / 2.0;
		_trail.Top = Top + (Height - _trail.Height) / 2.0;
		_trail.Show();
		_trail.ClipToWorkArea(GetWorkAreaOfWindow());
	}

	private SnapSide GetSnapSideAt(double cx, double cy)
	{
		// 以鼠标所在屏幕的工作区为准，判断悬浮球中心离哪条边最近
		Rect workArea = GetWorkAreaOfCursor();

		double num = cx - workArea.Left;
		double num2 = workArea.Right - cx;
		double num3 = cy - workArea.Top;
		double val = workArea.Bottom - cy;
		double num4 = Math.Min(Math.Min(num, num2), Math.Min(num3, val));
		// 球中心超出该屏边缘过远（鼠标在该屏、但球还离得远），不做吸附
		if (num4 > 60.0 || num4 < -60.0)
		{
			return SnapSide.None;
		}
		if (num4 != num)
		{
			if (num4 != num2)
			{
				if (num4 != num3)
				{
					return SnapSide.Bottom;
				}
				return SnapSide.Top;
			}
			return SnapSide.Right;
		}
		return SnapSide.Left;
	}

	private (double Left, double Top) GetSnappedPosition(SnapSide side, double baseLeft, double baseTop, double width, double height, Rect? workAreaOverride = null)
	{
		// 以鼠标所在屏幕的工作区计算吸附位置（与真吸附、伪吸附同一套坐标）
		Rect workArea = workAreaOverride ?? GetWorkAreaOfCursor();
		double item = baseLeft;
		double item2 = baseTop;
		// 贴边隐藏设计：悬浮球一半留在屏内、一半伸出屏幕外（由裁剪负责隐藏）
		switch (side)
		{
		case SnapSide.Left:
			item = workArea.Left - 24.0 - 18.0;
			item2 = Clamp(baseTop, workArea.Top, workArea.Bottom - height);
			break;
		case SnapSide.Right:
			item = workArea.Right - 24.0 - 18.0;
			item2 = Clamp(baseTop, workArea.Top, workArea.Bottom - height);
			break;
		case SnapSide.Top:
			item2 = workArea.Top - 24.0 - 18.0;
			item = Clamp(baseLeft, workArea.Left, workArea.Right - width);
			break;
		case SnapSide.Bottom:
			item2 = workArea.Bottom - 24.0 - 18.0;
			item = Clamp(baseLeft, workArea.Left, workArea.Right - width);
			break;
		}
		return (Left: item, Top: item2);
	}

	private void SnapToNearestEdge()
	{
		SnapSide snapSideAt = GetSnapSideAt(Left + Width / 2.0, Top + Height / 2.0);
		if (snapSideAt == SnapSide.None)
		{
			_currentSnap = SnapSide.None;
			_snapWorkArea = Rect.Empty;
			_isUnfolded = false;
			ClearClip();
			StopWindowAnimation();
			RefreshHoverByCursor();
		}
		else
		{
			SnapTo(snapSideAt, animate: true);
		}
	}

	private void SnapTo(SnapSide side, bool animate, Rect? workAreaOverride = null)
	{
		// 以鼠标所在屏幕为基准（鼠标在哪块屏，就吸附到哪块屏的边）
		Rect workArea = workAreaOverride ?? GetWorkAreaOfCursor();
		_snapWorkArea = workArea;
		(double Left, double Top) snappedPosition = GetSnappedPosition(side, Left, Top, Width, Height, workArea);
		double item = snappedPosition.Left;
		double item2 = snappedPosition.Top;
		_currentSnap = side;
		_isUnfolded = false;
		_foldLeft = item;
		_foldTop = item2;
		_unfoldLeft = side switch
		{
			SnapSide.Left => workArea.Left + 12.0 - 18.0, 
			SnapSide.Right => workArea.Right - 12.0 - 18.0 - 48.0, 
			_ => item, 
		};
		_unfoldTop = side switch
		{
			SnapSide.Top => workArea.Top + 12.0 - 18.0, 
			SnapSide.Bottom => workArea.Bottom - 12.0 - 18.0 - 48.0, 
			_ => item2, 
		};
		if (animate)
		{
			AnimateWindowTo(item, item2);
		}
		else
		{
			StopWindowAnimation();
			Left = item;
			Top = item2;
		}
		SetBallOpacity(0.75);
		SetBallScale(1.0);
		SetShadow(strong: false);
		ClipToWorkArea(workArea);
		if (GetCursorPos(out var lpPoint))
		{
			EvaluateSnapHover(lpPoint.X, lpPoint.Y);
		}
	}

	private void UnfoldSnap()
	{
		if (_currentSnap != SnapSide.None && !_isUnfolded)
		{
			_isUnfolded = true;
			AnimateWindowTo(_unfoldLeft, _unfoldTop);
			SetBallOpacity(1.0);
			SetBallScale(1.12);
			SetShadow(strong: true);
		}
	}

	private void FoldSnap()
	{
		if (_currentSnap != SnapSide.None && _isUnfolded)
		{
			SnapTo(_currentSnap, animate: true);
		}
	}

	private static double Clamp(double v, double min, double max)
	{
		if (!(v < min))
		{
			if (!(v > max))
			{
				return v;
			}
			return max;
		}
		return min;
	}

	private void SyncClipToWorkArea()
	{
		// 拖动中由 OnMouseMove 自行决定裁剪（伪吸附时裁到鼠标所在屏），这里不处理
		if (_isDragging)
		{
			ClearClip();
			return;
		}
		ClipToWorkArea(_snapWorkArea.IsEmpty ? GetWorkAreaOfWindow() : _snapWorkArea);
	}

	/// <summary>把窗口裁剪到指定屏幕工作区内（超出部分不显示）</summary>
	private void ClipToWorkArea(Rect workArea)
	{
		nint handle = new WindowInteropHelper(this).Handle;
		if (handle == IntPtr.Zero)
		{
			return;
		}
		double left = Left;
		double top = Top;
		double width = Width;
		double height = Height;
		double num = Math.Max(0.0, workArea.Left - left);
		double num2 = Math.Max(0.0, workArea.Top - top);
		double num3 = Math.Min(width, workArea.Right - left);
		double num4 = Math.Min(height, workArea.Bottom - top);
		if ((num <= 0.01 && num2 <= 0.01 && num3 >= width - 0.01 && num4 >= height - 0.01) || num3 <= num || num4 <= num2)
		{
			ClearClip();
			return;
		}
		Rect rect = new Rect(num, num2, num3 - num, num4 - num2);
		if (!(rect == _lastClip))
		{
			_lastClip = rect;
			DpiScale dpi = VisualTreeHelper.GetDpi(this);
			nint num5 = CreateRectRgn((int)Math.Round(num * dpi.DpiScaleX), (int)Math.Round(num2 * dpi.DpiScaleY), (int)Math.Round(num3 * dpi.DpiScaleX), (int)Math.Round(num4 * dpi.DpiScaleY));
			if (SetWindowRgn(handle, num5, bRedraw: true) == 0)
			{
				DeleteObject(num5);
			}
		}
	}

	private void ClearClip()
	{
		nint handle = new WindowInteropHelper(this).Handle;
		if (handle != IntPtr.Zero && !(_lastClip == Rect.Empty))
		{
			_lastClip = Rect.Empty;
			SetWindowRgn(handle, IntPtr.Zero, bRedraw: true);
		}
	}

	private void SetBallOpacity(double target)
	{
		int num = ((target < 1.0) ? 200 : 120);
		DoubleAnimation animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(num))
		{
			EasingFunction = new QuadraticEase
			{
				EasingMode = EasingMode.EaseInOut
			}
		};
		BallBrush.BeginAnimation(Brush.OpacityProperty, animation);
	}

	private void SetBallScale(double target)
	{
		DoubleAnimation animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(120L))
		{
			EasingFunction = new QuadraticEase
			{
				EasingMode = EasingMode.EaseInOut
			}
		};
		BallScale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
		BallScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
	}

	private void RefreshHoverByCursor()
	{
		if (GetCursorPos(out var lpPoint))
		{
			DpiScale dpi = VisualTreeHelper.GetDpi(this);
			Point point = new Point((double)lpPoint.X / dpi.DpiScaleX, (double)lpPoint.Y / dpi.DpiScaleY);
			if (new Rect(Left + 18.0, Top + 18.0, 48.0, 48.0).Contains(point))
			{
				SetBallOpacity(1.0);
				SetBallScale(1.12);
				SetShadow(strong: true);
			}
			else
			{
				SetBallOpacity(0.3);
				SetBallScale(1.0);
				SetShadow(strong: false);
			}
		}
	}

	private void SetShadow(bool strong)
	{
		DoubleAnimation animation = new DoubleAnimation(strong ? 12 : 8, TimeSpan.FromMilliseconds(140L));
		BallShadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, animation);
		BallShadow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(strong ? 0.7 : 0.5, TimeSpan.FromMilliseconds(140L)));
	}

	private void ClickBall()
	{
		BallClicked?.Invoke(this, EventArgs.Empty);
	}
}
