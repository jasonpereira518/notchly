// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream UI/MenuPanel/ClipboardQuickPanelView.swift: a window over whatever you are doing, with the history in it. It is for
// when the island is off or you would rather not open it: 560 by 420 (at least 560 by 300), the size you leave it at is kept, Esc or
// a click elsewhere closes it, and choosing an entry puts it into the window you came from.
using System.Windows;
using System.Windows.Input;
using Notchly.App.Platform.Windowing;
using Notchly.Core.Clipboard;
using Notchly.Core.Settings;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Notchly.App.Pasteboard;

internal sealed class ClipboardQuickWindow : FluentWindow
{
    public const double DefaultWidth = 560, DefaultHeight = 420, MinHeightValue = 300;

    private readonly SettingsStore _settings;
    private readonly ClipboardView _view;
    private bool _choosing;

    public ClipboardQuickWindow(ClipboardHistoryService history, SettingsStore settings, ClipboardPaster paster, IntPtr target)
    {
        _settings = settings;
        Title = "Clipboard history";
        var width = settings.GetDouble(DefaultsKey.ClipboardHistoryWindowWidth);
        var height = settings.GetDouble(DefaultsKey.ClipboardHistoryWindowHeight);
        Width = width >= DefaultWidth ? width : DefaultWidth;
        Height = height >= MinHeightValue ? height : DefaultHeight;
        MinWidth = DefaultWidth;
        MinHeight = MinHeightValue;
        Topmost = true;
        ShowInTaskbar = false;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Acrylic;

        var dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
        _view = new ClipboardView(history, settings, dark ? ClipboardPalette.Dark : ClipboardPalette.Light, offerWindow: false) { Visible = false };
        _view.Root.Margin = new Thickness(16, 36, 16, 14);
        Content = _view.Root;

        _view.Chosen += async (entries, paste) =>
        {
            _choosing = true;
            Close();
            await paster.PasteAsync(history, entries, target, paste);
        };
        PreviewKeyDown += (_, e) =>
        {
            if (_view.HandleKey(e)) { e.Handled = true; return; }
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };
        PreviewTextInput += (_, e) => { if (_view.HandleText(e.Text)) e.Handled = true; };
        Loaded += (_, _) => { Place(target); _view.Visible = true; Activate(); };
        Deactivated += (_, _) => { if (IsVisible && !_choosing) Close(); };
        Closing += (_, _) =>
        {
            _view.Visible = false;
            if (WindowState == WindowState.Normal) { settings.Set(DefaultsKey.ClipboardHistoryWindowWidth, ActualWidth); settings.Set(DefaultsKey.ClipboardHistoryWindowHeight, ActualHeight); }
        };
    }

    /// <summary>Centred on the screen the window you came from is on.</summary>
    private void Place(IntPtr target)
    {
        var display = (target != IntPtr.Zero ? WindowFrame.DisplayOf(target) : null) ?? Displays.All().FirstOrDefault(d => d.IsPrimary);
        if (display is null) return;
        var scale = display.Scale <= 0 ? 1 : display.Scale;
        Left = (display.WorkArea.Left + display.WorkArea.Width / 2.0) / scale - ActualWidth / 2;
        Top = (display.WorkArea.Top + display.WorkArea.Height / 2.0) / scale - ActualHeight / 2;
    }
}
