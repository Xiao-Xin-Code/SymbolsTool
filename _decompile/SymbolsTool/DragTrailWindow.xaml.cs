using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SymbolsTool;

public partial class DragTrailWindow : Window, IComponentConnector
{
	private Rect _lastClip = Rect.Empty;

	[DllImport("gdi32.dll")]
	private static extern nint CreateRectRgn(int x1, int y1, int x2, int y2);

	[DllImport("user32.dll")]
	private static extern int SetWindowRgn(nint hWnd, nint hRgn, bool bRedraw);

	[DllImport("gdi32.dll")]
	private static extern bool DeleteObject(nint hObject);

	public DragTrailWindow()
	{
		InitializeComponent();
		Loaded += OnLoaded;
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		DoubleAnimation animation = new DoubleAnimation(0.0, 360.0, TimeSpan.FromSeconds(4L))
		{
			RepeatBehavior = RepeatBehavior.Forever
		};
		TrailRotate.BeginAnimation(RotateTransform.AngleProperty, animation);
		DoubleAnimation animation2 = new DoubleAnimation(1.0, 1.15, TimeSpan.FromMilliseconds(600L))
		{
			EasingFunction = new QuadraticEase
			{
				EasingMode = EasingMode.EaseInOut
			},
			AutoReverse = true,
			RepeatBehavior = RepeatBehavior.Forever
		};
		TrailScale.BeginAnimation(ScaleTransform.ScaleXProperty, animation2);
		TrailScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation2);
	}

	/// <summary>把虚环裁剪到指定屏幕工作区内（超出部分不显示）</summary>
	public void ClipToWorkArea(Rect workArea)
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

	/// <summary>取消裁剪（自由拖动时使用）</summary>
	public void ClearWorkAreaClip()
	{
		ClearClip();
	}

	private void ClearClip()
	{
		nint handle = new WindowInteropHelper(this).Handle;
		if (handle != IntPtr.Zero && !_lastClip.IsEmpty)
		{
			_lastClip = Rect.Empty;
			SetWindowRgn(handle, IntPtr.Zero, bRedraw: true);
		}
	}
}
