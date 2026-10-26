using System;

namespace SymbolsTool.Services;

[Flags]
public enum HotkeyModifiers : uint
{
	None = 0u,
	Alt = 1u,
	Control = 2u,
	Shift = 4u,
	Win = 8u
}
