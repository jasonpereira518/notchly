// SPDX-License-Identifier: GPL-3.0-or-later
// The notification-area icon: the Windows counterpart of upstream's status item
// (App/StatusItemController.swift). Left click toggles the panel, right click opens a menu.
// The icon is drawn at runtime (our own mark, not upstream's glyph) for light and dark taskbars.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using Microsoft.Win32;

namespace Islet.App.Shell;

public sealed class TrayIconService : IDisposable
{
    private readonly TaskbarIcon _icon;

    public TrayIconService(App app)
    {
        _icon = new TaskbarIcon
        {
            ToolTipText = Islet.Core.AppIdentity.Name,
            Icon = CreateIcon(TaskbarIsLight()),
            NoLeftClickDelay = true,
            ContextMenu = BuildMenu(app),
        };
        _icon.TrayLeftMouseUp += (_, _) => app.TogglePanel();
        // Efficiency mode would throttle the island's animations and the input hooks.
        _icon.ForceCreate(false);
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
    }

    public void Notify(string title, string message) => _icon.ShowNotification(title, message);

    private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
            Application.Current.Dispatcher.BeginInvoke(() => _icon.Icon = CreateIcon(TaskbarIsLight()));
    }

    private static ContextMenu BuildMenu(App app)
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }
        menu.Items.Add(Item("Open panel", app.TogglePanel));
        menu.Items.Add(Item("Settings…", () => app.ShowSettings(SettingsPage.General)));
        menu.Items.Add(Item("Features…", () => app.ShowSettings(SettingsPage.Features)));
        menu.Items.Add(Item("Dynamic Island…", () => app.ShowSettings(SettingsPage.Island)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit " + Islet.Core.AppIdentity.Name, app.Quit));
        return menu;
    }

    private static bool TaskbarIsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
    }

    /// <summary>A small island pill with a dot, as a multi-size ICO built from PNG frames.</summary>
    private static System.Drawing.Icon CreateIcon(bool lightTaskbar)
    {
        int[] sizes = [16, 20, 24, 32, 48];
        var frames = sizes.Select(size => RenderPng(size, lightTaskbar)).ToList();
        using var ico = new MemoryStream();
        using (var writer = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((short)0); writer.Write((short)1); writer.Write((short)frames.Count);
            var offset = 6 + 16 * frames.Count;
            for (var i = 0; i < frames.Count; i++)
            {
                var size = sizes[i];
                writer.Write((byte)(size >= 256 ? 0 : size)); writer.Write((byte)(size >= 256 ? 0 : size));
                writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((short)1); writer.Write((short)32);
                writer.Write(frames[i].Length); writer.Write(offset);
                offset += frames[i].Length;
            }
            foreach (var frame in frames) writer.Write(frame);
        }
        ico.Position = 0;
        return new System.Drawing.Icon(ico);
    }

    private static byte[] RenderPng(int size, bool lightTaskbar)
    {
        var ink = lightTaskbar ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Colors.White;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var s = size / 16.0;
            // A rounded island hanging from a line, with the camera dot inside.
            dc.DrawRoundedRectangle(new SolidColorBrush(ink), null, new Rect(1.5 * s, 4.5 * s, 13 * s, 7 * s), 3.5 * s, 3.5 * s);
            var hole = lightTaskbar ? Colors.White : Color.FromRgb(0x1A, 0x1A, 0x1A);
            dc.DrawEllipse(new SolidColorBrush(hole), null, new Point(11 * s, 8 * s), 1.5 * s, 1.5 * s);
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        _icon.Dispose();
    }
}
