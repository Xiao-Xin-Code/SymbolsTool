using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;

namespace SymbolsTool.Helpers;

public static class MonitorHelper
{
	private delegate bool EnumMonitorsProc(nint hMonitor, nint hdcMonitor, ref RECT lprcMonitor, nint dwData);

	private struct RECT
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	private struct MONITORINFO
	{
		public uint cbSize;

		public RECT rcMonitor;

		public RECT rcWork;

		public uint dwFlags;
	}

	public class MonitorInfo
	{
		public Rect WorkingArea { get; set; } = Rect.Empty;

		public Rect MonitorArea { get; set; } = Rect.Empty;
	}

	private const uint MONITOR_DEFAULTTONEAREA = 2u;

	[DllImport("user32.dll")]
	private static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

	[DllImport("user32.dll")]
	private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

	[DllImport("user32.dll")]
	private static extern bool EnumDisplayMonitors(nint hdc, nint lprcClip, EnumMonitorsProc lpfnEnum, nint dwData);

	public static MonitorInfo? GetMonitorForWindow(nint hwnd)
	{
		nint num = MonitorFromWindow(hwnd, 2u);
		if (num == IntPtr.Zero)
		{
			return null;
		}
		MONITORINFO lpmi = new MONITORINFO
		{
			cbSize = (uint)Marshal.SizeOf<MONITORINFO>()
		};
		if (!GetMonitorInfo(num, ref lpmi))
		{
			return null;
		}
		return new MonitorInfo
		{
			WorkingArea = new Rect(lpmi.rcWork.Left, lpmi.rcWork.Top, lpmi.rcWork.Right - lpmi.rcWork.Left, lpmi.rcWork.Bottom - lpmi.rcWork.Top),
			MonitorArea = new Rect(lpmi.rcMonitor.Left, lpmi.rcMonitor.Top, lpmi.rcMonitor.Right - lpmi.rcMonitor.Left, lpmi.rcMonitor.Bottom - lpmi.rcMonitor.Top)
		};
	}

	public static List<MonitorInfo> EnumAllMonitors()
	{
		List<MonitorInfo> list = new List<MonitorInfo>();
		EnumDisplayMonitors(lpfnEnum: delegate(nint hMon, nint hdc, ref RECT rc, nint data)
		{
			MONITORINFO lpmi = new MONITORINFO
			{
				cbSize = (uint)Marshal.SizeOf<MONITORINFO>()
			};
			GetMonitorInfo(hMon, ref lpmi);
			list.Add(new MonitorInfo
			{
				WorkingArea = new Rect(lpmi.rcWork.Left, lpmi.rcWork.Top, lpmi.rcWork.Right - lpmi.rcWork.Left, lpmi.rcWork.Bottom - lpmi.rcWork.Top),
				MonitorArea = new Rect(lpmi.rcMonitor.Left, lpmi.rcMonitor.Top, lpmi.rcMonitor.Right - lpmi.rcMonitor.Left, lpmi.rcMonitor.Bottom - lpmi.rcMonitor.Top)
			});
			return true;
		}, hdc: IntPtr.Zero, lprcClip: IntPtr.Zero, dwData: IntPtr.Zero);
		return list;
	}
}
