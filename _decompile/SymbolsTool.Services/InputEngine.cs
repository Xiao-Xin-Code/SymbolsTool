using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using SymbolsTool.Helpers;

namespace SymbolsTool.Services;

public class InputEngine
{
	private const int WM_CHAR = 258;

	private const int WM_PASTE = 770;

	private const uint SMTO_ABORTIFHUNG = 2u;

	[StructLayout(LayoutKind.Sequential)]
	private struct RECT
	{
		public int Left; public int Top; public int Right; public int Bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct GUITHREADINFO
	{
		public int cbSize;
		public int flags;
		public nint hwndActive;
		public nint hwndFocus;
		public nint hwndCapture;
		public nint hwndMenuOwner;
		public nint hwndMoveSize;
		public nint hwndCaret;
		public RECT rcCaret;
	}

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

	[DllImport("user32.dll")]
	private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO info);

	[DllImport("user32.dll")]
	private static extern nint SendMessageTimeout(nint hWnd, int msg, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

	public bool TryPaste(string symbol)
	{
		try
		{
			Clipboard.SetText(symbol);
			return KeyboardSimulator.SendCtrlV() != 0;
		}
		catch (Exception value)
		{
			File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "SymbolsTool-crash.log"), $"[{DateTime.Now:HH:mm:ss}] InputEngine.Paste: {value}\n\n");
			return false;
		}
	}

	public bool TryInput(string symbol)
	{
		try
		{
			return KeyboardSimulator.SendUnicodeText(symbol) != 0;
		}
		catch (Exception value)
		{
			File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "SymbolsTool-crash.log"), $"[{DateTime.Now:HH:mm:ss}] InputEngine.Input: {value}\n\n");
			return false;
		}
	}

	/// <summary>
	/// 不切换前台窗口，直接把剪贴板内容粘贴到目标窗口当前的输入控件。
	/// 这样符号面板不会因为"临时激活目标程序"而闪烁或改变叠放层级。
	/// </summary>
	public bool TryPasteTo(nint target, string symbol)
	{
		try
		{
			nint focus = GetFocusedControl(target);
			if (focus == IntPtr.Zero)
			{
				return false;
			}
			Clipboard.SetText(symbol);
			SendMessageTimeout(focus, WM_PASTE, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 100u, out var _);
			return true;
		}
		catch (Exception value)
		{
			File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "SymbolsTool-crash.log"), $"[{DateTime.Now:HH:mm:ss}] InputEngine.PasteTo: {value}\n\n");
			return false;
		}
	}

	/// <summary>
	/// 不切换前台窗口，直接把字符消息发给目标窗口当前的输入控件。
	/// </summary>
	public bool TryInputTo(nint target, string symbol)
	{
		try
		{
			nint focus = GetFocusedControl(target);
			if (focus == IntPtr.Zero)
			{
				return false;
			}
			foreach (char ch in symbol)
			{
				SendMessageTimeout(focus, WM_CHAR, ch, 1, SMTO_ABORTIFHUNG, 100u, out var _);
			}
			return true;
		}
		catch (Exception value)
		{
			File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "SymbolsTool-crash.log"), $"[{DateTime.Now:HH:mm:ss}] InputEngine.InputTo: {value}\n\n");
			return false;
		}
	}

	/// <summary>取目标窗口所在线程当前拥有键盘焦点的控件</summary>
	private static nint GetFocusedControl(nint target)
	{
		uint processId;
		uint threadId = GetWindowThreadProcessId(target, out processId);
		if (threadId == 0)
		{
			return IntPtr.Zero;
		}
		GUITHREADINFO info = default(GUITHREADINFO);
		info.cbSize = Marshal.SizeOf<GUITHREADINFO>();
		if (!GetGUIThreadInfo(threadId, ref info))
		{
			return IntPtr.Zero;
		}
		return info.hwndFocus;
	}
}
