using System.Collections.Generic;
using System.Windows.Input;

namespace SymbolsTool.Services;

public readonly record struct HotkeyGesture(uint Modifiers, uint VirtualKey)
{
	public bool IsValid
	{
		get
		{
			if (VirtualKey != 0)
			{
				return Modifiers != 0;
			}
			return false;
		}
	}

	public string Display
	{
		get
		{
			if (!IsValid)
			{
				return "未设置";
			}
			List<string> list = new List<string>(5);
			if ((Modifiers & 2) != 0)
			{
				list.Add("Ctrl");
			}
			if ((Modifiers & 1) != 0)
			{
				list.Add("Alt");
			}
			if ((Modifiers & 4) != 0)
			{
				list.Add("Shift");
			}
			if ((Modifiers & 8) != 0)
			{
				list.Add("Win");
			}
			list.Add(KeyInterop.KeyFromVirtualKey((int)VirtualKey).ToString());
			return string.Join(" + ", list);
		}
	}

	public static HotkeyGesture DefaultPanel => new HotkeyGesture(3u, 83u);

	public static HotkeyGesture DefaultBall => new HotkeyGesture(3u, 66u);
}
