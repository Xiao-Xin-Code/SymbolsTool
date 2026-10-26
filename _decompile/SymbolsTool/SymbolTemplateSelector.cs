using System.Windows;
using System.Windows.Controls;
using SymbolsTool.Services;

namespace SymbolsTool;

public class SymbolTemplateSelector : DataTemplateSelector
{
	public DataTemplate? PlainTemplate { get; set; }

	public DataTemplate? ColorTemplate { get; set; }

	/// <summary>是否启用彩色符号显示（关闭后全部走单色文本渲染）</summary>
	public bool UseColor { get; set; } = true;

	public override DataTemplate? SelectTemplate(object item, DependencyObject container)
	{
		// 启用彩色且该符号有彩色图形时走彩色模板；
		// 否则走 TextBlock + 系统字体渲染（与输出到目标应用时的效果一致）
		if (UseColor && item is SymbolItem symbol && symbol.UseColorRenderer && symbol.ColorImage != null)
		{
			return ColorTemplate;
		}
		return PlainTemplate;
	}
}
