namespace SymbolsTool.Services;

public sealed class AppSettings
{
	public uint PanelModifiers { get; set; } = 3u;

	public uint PanelKey { get; set; } = 83u;

	public bool BallEnabled { get; set; } = true;

	public bool CopyOnClick { get; set; } = true;

	public bool ShowSymbolNames { get; set; } = true;

	/// <summary>是否以彩色图形显示系统 Emoji 字体支持的符号（关闭则全部单色显示）</summary>
	public bool ColorSymbols { get; set; } = true;

	public HotkeyGesture PanelHotkey => new HotkeyGesture(PanelModifiers, PanelKey);
}
