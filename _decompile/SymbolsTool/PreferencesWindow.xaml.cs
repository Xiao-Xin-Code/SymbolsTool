using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Markup;
using SymbolsTool.Helpers;
using SymbolsTool.Services;

namespace SymbolsTool;

public partial class PreferencesWindow : Window, IComponentConnector
{
	private HotkeyGesture _panel;

	public event Action<HotkeyGesture>? Saved;

	public PreferencesWindow(HotkeyGesture panel)
	{
		InitializeComponent();
		Icon = AppIcon.WindowIcon;
		_panel = panel;
		UpdateBox();
	}

	private void UpdateBox()
	{
		PanelHotkeyBox.Text = _panel.Display;
	}

	private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
	{
		e.Handled = true;
		Key key = ((e.Key == Key.System) ? e.SystemKey : e.Key);
		if (((uint)(key - 70) > 2u && (uint)(key - 116) > 5u) || 1 == 0)
		{
			uint num = 0u;
			ModifierKeys modifiers = Keyboard.Modifiers;
			if (modifiers.HasFlag(ModifierKeys.Control))
			{
				num |= 2;
			}
			if (modifiers.HasFlag(ModifierKeys.Alt))
			{
				num |= 1;
			}
			if (modifiers.HasFlag(ModifierKeys.Shift))
			{
				num |= 4;
			}
			if (modifiers.HasFlag(ModifierKeys.Windows))
			{
				num |= 8;
			}
			if ((num & 3) != 0)
			{
				_panel = new HotkeyGesture(num, (uint)KeyInterop.VirtualKeyFromKey(key));
				UpdateBox();
			}
		}
	}

	private void OnResetDefaults(object sender, RoutedEventArgs e)
	{
		_panel = HotkeyGesture.DefaultPanel;
		UpdateBox();
	}

	private void OnSave(object sender, RoutedEventArgs e)
	{
		Saved?.Invoke(_panel);
		Close();
	}

	private void OnCancel(object sender, RoutedEventArgs e)
	{
		Close();
	}

	private void OnClose(object sender, RoutedEventArgs e)
	{
		Close();
	}

	private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
	{
		if (e.ChangedButton == MouseButton.Left)
		{
			DragMove();
		}
	}
}
