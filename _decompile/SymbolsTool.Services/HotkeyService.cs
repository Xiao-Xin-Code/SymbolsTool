using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SymbolsTool.Services;

public sealed class HotkeyService : IDisposable
{
	private const int WM_HOTKEY = 786;

	private readonly nint _hwnd;

	private readonly HwndSource? _source;

	private readonly Dictionary<int, Action> _actions = new Dictionary<int, Action>();

	private readonly HashSet<int> _registered = new HashSet<int>();

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool UnregisterHotKey(nint hWnd, int id);

	public HotkeyService(Window host)
	{
		_hwnd = new WindowInteropHelper(host).Handle;
		if (_hwnd != IntPtr.Zero)
		{
			_source = HwndSource.FromHwnd(_hwnd);
			_source?.AddHook(WndProc);
		}
	}

	public bool TryRegister(int id, HotkeyGesture gesture, Action onPress)
	{
		Unregister(id);
		if (_hwnd == IntPtr.Zero || !gesture.IsValid)
		{
			return false;
		}
		if (!RegisterHotKey(_hwnd, id, gesture.Modifiers, gesture.VirtualKey))
		{
			return false;
		}
		_registered.Add(id);
		_actions[id] = onPress;
		return true;
	}

	public void Unregister(int id)
	{
		_actions.Remove(id);
		if (_registered.Remove(id) && _hwnd != IntPtr.Zero)
		{
			UnregisterHotKey(_hwnd, id);
		}
	}

	public void UnregisterAll()
	{
		foreach (int item in new List<int>(_registered))
		{
			Unregister(item);
		}
	}

	private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
	{
		if (msg == 786 && _actions.TryGetValue(((IntPtr)wParam).ToInt32(), out Action value))
		{
			value();
			handled = true;
		}
		return IntPtr.Zero;
	}

	public void Dispose()
	{
		UnregisterAll();
		_source?.RemoveHook(WndProc);
	}
}
