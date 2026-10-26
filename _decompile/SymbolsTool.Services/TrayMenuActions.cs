using System;

namespace SymbolsTool.Services;

public sealed record TrayMenuActions(
    Action ShowPanel,
    Action TogglePanel,
    Action ResetPanelPosition,
    Action<bool> SetBallEnabled,
    Action OpenPreferences,
    Action Restart,
    Func<(string PanelShortcut, bool HotkeysEnabled, bool BallEnabled, bool CopyOnClick, bool ShowSymbolNames)> GetState,
    Action<bool> SetHotkeysEnabled,
    Action<bool> SetCopyOnClick,
    Action<bool> SetShowSymbolNames,
    Func<bool> GetColorSymbols,
    Action<bool> SetColorSymbols);
