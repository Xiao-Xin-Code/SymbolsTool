using System.Windows;
using System.Windows.Markup;
using SymbolsTool.Helpers;

namespace SymbolsTool;

public partial class MainWindow : Window, IComponentConnector
{
	public MainWindow()
	{
		InitializeComponent();
		Icon = AppIcon.WindowIcon;
	}
}
