using System;
using System.Windows.Media;

namespace SymbolsTool.Services;

public class SymbolItem
{
	public string Char { get; set; } = "";

	public string Category { get; set; } = "";

	public string EnName { get; set; } = "";

	public string ZhName { get; set; } = "";

	public string Aliases { get; set; } = "";

	public string ToolTipText { get; set; } = "";

	public bool UseColorRenderer { get; set; }

	public string ColorText { get; set; } = "";

	public string InputText { get; set; } = "";

	public DrawingImage? ColorImage { get; set; }

	internal int Order { get; set; }

	internal string NormChar { get; set; } = "";

	internal string NormCategory { get; set; } = "";

	internal string NormEnName { get; set; } = "";

	internal string[] NormEnTokens { get; set; } = Array.Empty<string>();

	internal string[] NormZhNames { get; set; } = Array.Empty<string>();
}
