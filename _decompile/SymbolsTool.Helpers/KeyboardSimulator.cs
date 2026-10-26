using System.Runtime.InteropServices;

namespace SymbolsTool.Helpers;

public static class KeyboardSimulator
{
	private struct INPUT
	{
		public uint type;

		public InputUnion U;
	}

	[StructLayout(LayoutKind.Explicit)]
	private struct InputUnion
	{
		[FieldOffset(0)]
		public MOUSEINPUT mi;

		[FieldOffset(0)]
		public KEYBDINPUT ki;
	}

	private struct MOUSEINPUT
	{
		public int dx;

		public int dy;

		public uint mouseData;

		public uint dwFlags;

		public uint time;

		public nint dwExtraInfo;
	}

	private struct KEYBDINPUT
	{
		public ushort wVk;

		public ushort wScan;

		public uint dwFlags;

		public uint time;

		public nint dwExtraInfo;
	}

	private const uint INPUT_KEYBOARD = 1u;

	private const uint KEYEVENTF_KEYUP = 2u;

	private const uint KEYEVENTF_UNICODE = 4u;

	private const int VK_CONTROL = 17;

	[DllImport("user32.dll", SetLastError = true)]
	private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

	public static uint SendCtrlV()
	{
		INPUT[] array = new INPUT[4]
		{
			MakeKey(17, up: false),
			MakeKey(86, up: false),
			MakeKey(86, up: true),
			MakeKey(17, up: true)
		};
		return SendInput((uint)array.Length, array, Marshal.SizeOf<INPUT>());
	}

	public static uint SendUnicodeText(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return 0u;
		}
		INPUT[] array = new INPUT[text.Length * 2];
		int num = 0;
		foreach (char ch in text)
		{
			array[num++] = MakeUnicode(ch, up: false);
			array[num++] = MakeUnicode(ch, up: true);
		}
		return SendInput((uint)array.Length, array, Marshal.SizeOf<INPUT>());
	}

	private static INPUT MakeKey(ushort vk, bool up)
	{
		return new INPUT
		{
			type = 1u,
			U = 
			{
				ki = new KEYBDINPUT
				{
					wVk = vk,
					dwFlags = (up ? 2u : 0u)
				}
			}
		};
	}

	private static INPUT MakeUnicode(char ch, bool up)
	{
		return new INPUT
		{
			type = 1u,
			U = 
			{
				ki = new KEYBDINPUT
				{
					wVk = 0,
					wScan = ch,
					dwFlags = (uint)(4 | (up ? 2 : 0))
				}
			}
		};
	}
}
