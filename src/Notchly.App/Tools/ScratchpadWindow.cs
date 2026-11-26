// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream UI/MenuPanel/ScratchpadPanelView.swift and ScratchpadService's panel: a floating pad (380 by 300, at least 280 by
// 220) always on top, with named tabs, a Markdown format bar, a preview with checkable task boxes, find, pin, copy all, clear and
// export to a text file. Esc or a click elsewhere closes it (unless pinned); where you leave it is remembered. The text is plain
// Markdown: the format bar only inserts the marks, and the preview is drawn from Core's own block parser.
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Notchly.App.Platform;
using Notchly.App.Platform.Windowing;
using Notchly.Core.Settings;
using Notchly.Core.Tools;
using Serilog;

namespace Notchly.App.Tools;

internal sealed class ScratchpadPadWindow : Window
{
    public const double DefaultWidth = 380, DefaultHeight = 300, MinimumWidth = 280, MinimumHeight = 220;
    public const string Placeholder = "Type anything. It saves by itself.";

    private static readonly SolidColorBrush Text = Brush(0xF2, 0xF2, 0xF4), Soft = Brush(0xF2, 0xF2, 0xF4, 0x99), Chip = Brush(255, 255, 255, 0x1F),
                                           ChipOn = Brush(255, 255, 255, 0x40), Accent = Brush(0x4C, 0x8D, 0xFF);

    private static SolidColorBrush Brush(byte r, byte g, byte b, byte a = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private readonly ScratchpadService _service;
    private readonly SettingsStore _settings;
    private readonly Border _frame = new() { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), BorderBrush = Brush(255, 255, 255, 0x30), Margin = new Thickness(0) };
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    private readonly TextBox _editor = new();
    private readonly TextBlock _placeholder = new() { Text = Placeholder, IsHitTestVisible = false, Foreground = Soft };
    private readonly FlowDocumentScrollViewer _preview = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
    private readonly Grid _body = new();
    private readonly StackPanel _formatBar = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
    private readonly Grid _findBar = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBox _find = new();
    private readonly TextBlock _status = new() { FontSize = 11, Foreground = Brush(0xFF, 0x9F, 0x0A), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
    private const string NewPadGlyph = "";
    private Border _pinButton = null!, _previewButton = null!, _addButton = null!;
    private bool _pinned, _previewing, _updating, _exportOpen, _closing;
    private Guid _renaming;

    public ScratchpadPadWindow(ScratchpadService service, SettingsStore settings)
    {
        _service = service;
        _settings = settings;
        Title = "Scratchpad";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Topmost = true;
        Width = DefaultWidth;
        Height = DefaultHeight;
        MinWidth = MinimumWidth;
        MinHeight = MinimumHeight;
        Left = -32000;
        Top = -32000;

        var size = ScratchpadSupport.SanitizedTextSize(settings.GetDouble(DefaultsKey.ScratchpadTextSize));
        _editor.Style = null;
        _find.Style = null;
        _editor.AcceptsReturn = true;
        _editor.AcceptsTab = true;
        _editor.TextWrapping = TextWrapping.Wrap;
        _editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _editor.FontSize = size;
        _editor.Background = Brushes.Transparent;
        _editor.BorderThickness = new Thickness(0);
        _editor.Foreground = Text;
        _editor.CaretBrush = Text;
        _editor.Padding = new Thickness(2);
        _editor.SelectionBrush = Accent;
        _placeholder.FontSize = size;
        _placeholder.Margin = new Thickness(6, 2, 0, 0);
        _preview.Document = new FlowDocument();
        _body.Children.Add(_editor);
        _body.Children.Add(_placeholder);
        _body.Children.Add(_preview);

        BuildFormatBar();
        BuildFindBar();
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        _addButton = Glyph(NewPadGlyph, "New scratchpad", NewPad);
        _previewButton = Glyph("\uE7B3", "Preview", () => TogglePreview());
        _pinButton = Glyph("\uE718", "Keep open", () => { _pinned = !_pinned; Style(_pinButton, _pinned); });
        tools.Children.Add(_addButton);
        tools.Children.Add(_previewButton);
        tools.Children.Add(_pinButton);
        tools.Children.Add(Glyph("\uE8C8", "Copy all", CopyAll));
        tools.Children.Add(Glyph("\uE74E", "Save as file", Export));
        tools.Children.Add(Glyph("\uE894", "Clear", Clear));
        DockPanel.SetDock(tools, Dock.Right);
        top.Children.Add(tools);
        top.Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _tabs });

        var column = new DockPanel { Margin = new Thickness(12, 10, 12, 8) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_formatBar, Dock.Top);
        DockPanel.SetDock(_findBar, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        column.Children.Add(top);
        column.Children.Add(_formatBar);
        column.Children.Add(_findBar);
        column.Children.Add(_status);
        column.Children.Add(_body);
        _frame.Child = column;
        Content = _frame;
        ApplyOpacity();

        _editor.TextChanged += OnTextChanged;
        _editor.PreviewKeyDown += OnEditorKey;
        _editor.PreviewTextInput += OnEditorText;
        PreviewKeyDown += OnWindowKey;
        Deactivated += OnDeactivated;
        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is Border or Grid or DockPanel or StackPanel) { try { DragMove(); } catch (InvalidOperationException) { } } };
        SourceInitialized += (_, _) => Place();
        Loaded += (_, _) => { Rebuild(); _editor.Focus(); };
        Closing += (_, _) => { _closing = true; SavePosition(); _service.Flush(); };
        service.SaveStateChanged += ShowSaveState;
        Closed += (_, _) => service.SaveStateChanged -= ShowSaveState;
    }

    private ScratchpadDocument Document => _service.Document!;

    // MARK: Chrome

    private void ApplyOpacity()
    {
        // 0 is the translucent look and 1 is solid; a dark plate under both keeps the text readable.
        var opacity = ScratchpadSupport.SanitizedBackgroundOpacity(_settings.GetDouble(DefaultsKey.ScratchpadBackgroundOpacity));
        _frame.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * (0.78 + 0.22 * opacity)), 0x1C, 0x1C, 0x1E));
    }

    private static Border Glyph(string glyph, string tip, Action click)
    {
        var button = new Border
        {
            Width = 28, Height = 26, CornerRadius = new CornerRadius(7), Margin = new Thickness(2, 0, 0, 0), Cursor = Cursors.Hand, ToolTip = tip, Background = Brushes.Transparent,
            Child = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 13, Foreground = Text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        button.MouseEnter += (_, _) => button.Background = Chip;
        button.MouseLeave += (_, _) => { if (!Equals(button.Tag, true)) button.Background = Brushes.Transparent; };
        button.MouseLeftButtonUp += (_, e) => { click(); e.Handled = true; };
        return button;
    }

    private static void Style(Border button, bool on)
    {
        button.Tag = on;
        button.Background = on ? ChipOn : Brushes.Transparent;
    }

    private void BuildFormatBar()
    {
        string[] labels = ["B", "I", "S", "H", "•", "1.", "❝", "</>", "🔗"];
        string[] tips = ["Bold", "Italic", "Strikethrough", "Heading", "Bulleted list", "Numbered list", "Quote", "Code", "Link"];
        for (var i = 0; i < ScratchpadMarks.All.Count; i++)
        {
            var mark = ScratchpadMarks.All[i];
            var label = new TextBlock
            {
                Text = labels[i], FontSize = 12, Foreground = Text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                FontWeight = mark == ScratchpadMark.Bold ? FontWeights.Bold : FontWeights.Normal, FontStyle = mark == ScratchpadMark.Italic ? FontStyles.Italic : FontStyles.Normal,
                TextDecorations = mark == ScratchpadMark.Strikethrough ? TextDecorations.Strikethrough : null,
            };
            var button = new Border { Width = 28, Height = 24, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 3, 0), Cursor = Cursors.Hand, ToolTip = tips[i], Background = Chip, Child = label };
            button.MouseLeftButtonDown += (_, e) => { Apply(mark); e.Handled = true; };
            _formatBar.Children.Add(button);
        }
    }

    private void BuildFindBar()
    {
        _find.Padding = new Thickness(6, 3, 6, 3);
        _find.FontSize = 12;
        _find.Background = Chip;
        _find.Foreground = Text;
        _find.CaretBrush = Text;
        _find.BorderThickness = new Thickness(0);
        _find.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { FindNext(!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); e.Handled = true; }
            else if (e.Key == Key.Escape) { CloseFind(); e.Handled = true; }
        };
        _findBar.ColumnDefinitions.Add(new ColumnDefinition());
        _findBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var next = Glyph("\uE70D", "Next", () => FindNext(true));
        var previous = Glyph("\uE70E", "Previous", () => FindNext(false));
        Grid.SetColumn(previous, 1);
        var steps = new StackPanel { Orientation = Orientation.Horizontal, Children = { previous, next } };
        Grid.SetColumn(steps, 1);
        _findBar.Children.Add(_find);
        _findBar.Children.Add(steps);
    }

    // MARK: Position

    private void Place()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (TryStoredOrigin(out var x, out var y))
        {
            Native.SetWindowPos(handle, Native.HWND_TOPMOST, x, y, 0, 0, 0x0001);
            return;
        }
        Native.GetCursorPos(out var p);
        var display = Displays.AtPoint(p.X, p.Y) ?? Displays.All().FirstOrDefault();
        if (display is null) return;
        var width = (int)Math.Round(DefaultWidth * display.Scale);
        var height = (int)Math.Round(DefaultHeight * display.Scale);
        var work = display.WorkArea;
        Native.SetWindowPos(handle, Native.HWND_TOPMOST, work.Left + (work.Width - width) / 2, work.Top + (work.Height - height) / 3, width, height, 0);
    }

    /// <summary>Where it was left, as long as that is still on a display.</summary>
    private bool TryStoredOrigin(out int x, out int y)
    {
        x = y = 0;
        var parts = _settings.GetString(DefaultsKey.ScratchpadWindowOrigin).Split(',');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out x) || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out y)) return false;
        int left = x, top = y;
        return Displays.All().Any(d => left + 40 >= d.Bounds.Left && left + 40 < d.Bounds.Right && top + 20 >= d.Bounds.Top && top + 20 < d.Bounds.Bottom);
    }

    private void SavePosition()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && Native.GetWindowRect(handle, out var rect))
            _settings.Set(DefaultsKey.ScratchpadWindowOrigin, $"{rect.Left},{rect.Top}");
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_closing) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing || IsActive || !IsVisible) return;
            if (_settings.GetBool(DefaultsKey.ScratchpadCloseOnClickOutside) && ScratchpadSupport.DismissesOnOutsideClick(_pinned, _exportOpen)) Close();
        }, DispatcherPriority.Background);
    }

    // MARK: Tabs

    private void Rebuild()
    {
        _updating = true;
        _tabs.Children.Clear();
        var document = Document;
        foreach (var pad in document.Pads)
        {
            var selected = pad.Id == document.SelectedId;
            UIElement label;
            if (_renaming == pad.Id)
            {
                var box = new TextBox { Text = pad.Name, MinWidth = 80, FontSize = 12, Padding = new Thickness(2), Background = Chip, Foreground = Text, CaretBrush = Text, BorderThickness = new Thickness(0) };
                void Commit()
                {
                    var id = _renaming;
                    _renaming = Guid.Empty;
                    if (Document.Renaming(id, box.Text) is { } renamed) _service.Update(renamed);
                    Rebuild();
                }
                box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } else if (e.Key == Key.Escape) { _renaming = Guid.Empty; Rebuild(); e.Handled = true; } };
                box.LostKeyboardFocus += (_, _) => { if (_renaming == pad.Id) Commit(); };
                box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
                label = box;
            }
            else label = new TextBlock { Text = pad.Name, FontSize = 12, Foreground = selected ? Text : Soft, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 130, TextTrimming = TextTrimming.CharacterEllipsis };
            var close = new TextBlock { Text = "\uE711", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 9, Foreground = Soft, Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, ToolTip = "Close scratchpad" };
            close.MouseLeftButtonUp += (_, e) => { ClosePad(pad); e.Handled = true; };
            var tab = new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 4, 8, 4), Margin = new Thickness(0, 0, 4, 0), Cursor = Cursors.Hand,
                Background = selected ? ChipOn : Chip, Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { label, close } },
            };
            tab.MouseLeftButtonUp += (_, _) => { if (!selected && Document.Selecting(pad.Id) is { } next) { _service.Update(next); Rebuild(); } };
            tab.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { _renaming = pad.Id; Rebuild(); e.Handled = true; } };
            _tabs.Children.Add(tab);
        }
        var canAdd = document.Pads.Count < ScratchpadDocument.MaximumPadCount;
        _addButton.IsEnabled = canAdd;
        _addButton.Opacity = canAdd ? 1 : 0.4;

        _editor.Text = document.SelectedPad?.Text ?? "";
        _editor.IsReadOnly = _service.LoadError is not null;
        _placeholder.Visibility = _editor.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _updating = false;
        ShowSaveState();
        if (_previewing) RenderPreview();
    }

    private void NewPad()
    {
        if (Document.AddingPad(ScratchpadService.DefaultName) is { } next) { _service.Update(next); Rebuild(); _editor.Focus(); }
    }

    private void ClosePad(ScratchpadPad pad)
    {
        var next = Document.Removing(pad.Id);
        if (next is null) return;
        if (ScratchpadSupport.RequiresCloseConfirmation(pad)
            && System.Windows.MessageBox.Show(this, $"Close \"{pad.Name}\"? Its text will be deleted.", "Scratchpad", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        _service.Update(next);
        Rebuild();
    }

    // MARK: Editing

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;
        _placeholder.Visibility = _editor.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _service.Update(Document.UpdatingSelectedText(_editor.Text, DateTimeOffset.Now));
        if (_previewing) RenderPreview();
    }

    /// <summary>The text range replaced and the caret after it, through the box's own undo stack.</summary>
    private void Replace(ScratchpadRange range, string replacement, ScratchpadRange selection)
    {
        _editor.Select(range.Start, range.Length);
        _editor.SelectedText = replacement;
        _editor.Select(selection.Start, selection.Length);
        _editor.Focus();
    }

    private void Apply(ScratchpadMark mark)
    {
        if (_editor.IsReadOnly || _previewing) return;
        var edit = ScratchpadSupport.Edit(mark, _editor.Text, new ScratchpadRange(_editor.SelectionStart, _editor.SelectionLength));
        Replace(edit.Range, edit.Replacement, edit.Selection);
    }

    private void OnEditorKey(object sender, KeyEventArgs e)
    {
        // Return keeps a list or quote going; a second Return on an empty item ends it.
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && !_editor.IsReadOnly)
        {
            var edit = ScratchpadSupport.LineBreakEdit(_editor.Text, new ScratchpadRange(_editor.SelectionStart, _editor.SelectionLength));
            _editor.Select(edit.Range.Start, edit.Range.Length);
            _editor.SelectedText = edit.Replacement;
            _editor.CaretIndex = edit.Caret;
            e.Handled = true;
        }
    }

    private void OnEditorText(object sender, TextCompositionEventArgs e)
    {
        // Typing "]" inside an empty "[ " box closes it as "[ ]".
        if (e.Text != "]" || _editor.IsReadOnly) return;
        var caret = _editor.CaretIndex;
        var lineStart = _editor.GetCharacterIndexFromLineIndex(_editor.GetLineIndexFromCharacterIndex(caret));
        if (caret >= lineStart && ScratchpadSupport.CompletesEmptyTaskBox(_editor.Text[lineStart..caret]))
        {
            _editor.SelectedText = " ]";
            _editor.CaretIndex = caret + 2;
            e.Handled = true;
        }
    }

    private void OnWindowKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            if (_findBar.Visibility == Visibility.Visible) CloseFind();
            else if (_renaming != Guid.Empty) { _renaming = Guid.Empty; Rebuild(); }
            else Close();
            e.Handled = true;
            return;
        }
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var character = key is >= Key.A and <= Key.Z ? ((char)('a' + (key - Key.A))).ToString() : null;
        var action = ScratchpadFocusedShortcut.Action(character, ctrl, shift, Document.Pads.Count < ScratchpadDocument.MaximumPadCount, Document.Pads.Count > 1);
        switch (action)
        {
            case ScratchpadShortcutAction.CreatePad: NewPad(); break;
            case ScratchpadShortcutAction.CloseSelectedPad: if (Document.SelectedPad is { } pad) ClosePad(pad); break;
            case ScratchpadShortcutAction.HidePad: Close(); break;
            case ScratchpadShortcutAction.Find: OpenFind(); break;
            case ScratchpadShortcutAction.FindNext: FindNext(true); break;
            case ScratchpadShortcutAction.FindPrevious: FindNext(false); break;
            default: return;
        }
        e.Handled = true;
    }

    // MARK: Find

    private void OpenFind()
    {
        _findBar.Visibility = Visibility.Visible;
        _find.Focus();
        _find.SelectAll();
    }

    private void CloseFind()
    {
        _findBar.Visibility = Visibility.Collapsed;
        _editor.Focus();
    }

    private void FindNext(bool forward)
    {
        var needle = _find.Text;
        if (needle.Length == 0) { OpenFind(); return; }
        var text = _editor.Text;
        var from = forward ? _editor.SelectionStart + _editor.SelectionLength : _editor.SelectionStart - 1;
        int at;
        if (forward)
        {
            at = text.IndexOf(needle, Math.Clamp(from, 0, text.Length), StringComparison.CurrentCultureIgnoreCase);
            if (at < 0) at = text.IndexOf(needle, 0, StringComparison.CurrentCultureIgnoreCase);
        }
        else
        {
            at = from >= 0 ? text.LastIndexOf(needle, Math.Min(from + needle.Length - 1, text.Length - 1), StringComparison.CurrentCultureIgnoreCase) : -1;
            if (at < 0) at = text.LastIndexOf(needle, StringComparison.CurrentCultureIgnoreCase);
        }
        if (at < 0) return;
        if (_previewing) TogglePreview();
        _editor.Select(at, needle.Length);
        _editor.ScrollToLine(_editor.GetLineIndexFromCharacterIndex(at));
    }

    // MARK: Actions

    private void CopyAll()
    {
        if (ClipboardWrite.TrySetText(_editor.Text)) QuickToolHud.Show("Copied");
    }

    private void Clear()
    {
        if (_editor.Text.Length == 0 || _editor.IsReadOnly) return;
        _editor.SelectAll();
        _editor.SelectedText = "";
    }

    private void Export()
    {
        if (Document.SelectedPad is not { } pad) return;
        var dialog = new SaveFileDialog { FileName = ScratchpadSupport.ExportFileName(pad.Name, DateTimeOffset.Now), Filter = "Text|*.txt", DefaultExt = ".txt" };
        _exportOpen = true;
        try
        {
            if (dialog.ShowDialog(this) != true) return;
            File.WriteAllText(dialog.FileName, pad.Text, new System.Text.UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning(e, "The scratchpad could not be exported");
            QuickToolHud.Show("The file could not be saved");
        }
        finally { _exportOpen = false; }
    }

    private void ShowSaveState()
    {
        var message = _service.LoadError ?? (_service.SaveFailedLast ? ScratchpadService.SaveFailed : null);
        _status.Text = message ?? "";
        _status.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // MARK: Preview

    private void TogglePreview()
    {
        _previewing = !_previewing;
        Style(_previewButton, _previewing);
        _editor.Visibility = _previewing ? Visibility.Collapsed : Visibility.Visible;
        _placeholder.Visibility = !_previewing && _editor.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _preview.Visibility = _previewing ? Visibility.Visible : Visibility.Collapsed;
        _formatBar.Opacity = _previewing ? 0.4 : 1;
        if (_previewing) RenderPreview(); else _editor.Focus();
    }

    private void RenderPreview()
    {
        var text = _editor.Text;
        var size = ScratchpadSupport.SanitizedTextSize(_settings.GetDouble(DefaultsKey.ScratchpadTextSize));
        var document = new FlowDocument { FontFamily = new FontFamily("Segoe UI"), FontSize = size, Foreground = Text, Background = Brushes.Transparent, PagePadding = new Thickness(2) };
        foreach (var block in ScratchpadSupport.MarkdownPreview(text))
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 4), LineHeight = size * 1.4 };
            var kind = block.Kind;
            var indent = Math.Max(0, kind.Depth - 1) * 18.0;
            switch (kind.Type)
            {
                case ScratchpadBlockType.Heading:
                    paragraph.FontSize = size * (kind.Level switch { 1 => 1.7, 2 => 1.4, 3 => 1.2, _ => 1.1 });
                    paragraph.FontWeight = FontWeights.SemiBold;
                    paragraph.Margin = new Thickness(0, 6, 0, 4);
                    break;
                case ScratchpadBlockType.UnorderedListItem:
                    paragraph.Margin = new Thickness(indent + 18, 0, 0, 2);
                    paragraph.TextIndent = -14;
                    paragraph.Inlines.Add(new Run("•  "));
                    break;
                case ScratchpadBlockType.OrderedListItem:
                    paragraph.Margin = new Thickness(indent + 22, 0, 0, 2);
                    paragraph.TextIndent = -20;
                    paragraph.Inlines.Add(new Run($"{kind.Ordinal}.  "));
                    break;
                case ScratchpadBlockType.TaskListItem:
                    paragraph.Margin = new Thickness(indent + 4, 0, 0, 2);
                    paragraph.Inlines.Add(new InlineUIContainer(TaskBox(kind)) { BaselineAlignment = BaselineAlignment.Center });
                    paragraph.Inlines.Add(new Run("  "));
                    break;
                case ScratchpadBlockType.Quote:
                    paragraph.Margin = new Thickness(Math.Max(0, kind.Depth - 1) * 12, 0, 0, 4);
                    paragraph.Padding = new Thickness(10, 0, 0, 0);
                    paragraph.BorderBrush = Soft;
                    paragraph.BorderThickness = new Thickness(2, 0, 0, 0);
                    paragraph.Foreground = Soft;
                    break;
                case ScratchpadBlockType.Code:
                    paragraph.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                    paragraph.Background = Chip;
                    paragraph.Padding = new Thickness(6, 4, 6, 4);
                    break;
                case ScratchpadBlockType.ThematicBreak:
                    paragraph.BorderBrush = Soft;
                    paragraph.BorderThickness = new Thickness(0, 0, 0, 1);
                    document.Blocks.Add(paragraph);
                    continue;
            }
            foreach (var run in block.Runs) paragraph.Inlines.Add(Inline(run));
            document.Blocks.Add(paragraph);
        }
        _preview.Document = document;
    }

    private CheckBox TaskBox(ScratchpadBlockKind kind)
    {
        var box = new CheckBox { IsChecked = kind.Checked, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, -3) };
        box.Click += (_, _) =>
        {
            if (ScratchpadSupport.TaskToggleEdit(kind.Box, _editor.Text) is not { } edit) return;
            _updating = true;
            _editor.Select(edit.Range.Start, edit.Range.Length);
            _editor.SelectedText = edit.Text;
            _updating = false;
            OnTextChanged(_editor, null!);
        };
        return box;
    }

    private Inline Inline(ScratchpadInlineRun run)
    {
        Inline inline = new Run(run.Text);
        if (run.Code) { inline.FontFamily = new FontFamily("Cascadia Mono, Consolas"); inline.Background = Chip; }
        if (run.Bold) inline.FontWeight = FontWeights.Bold;
        if (run.Italic) inline.FontStyle = FontStyles.Italic;
        if (run.Strikethrough) inline = new Span(inline) { TextDecorations = TextDecorations.Strikethrough };
        if (run.Link is { } target && Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            var link = new Hyperlink(inline) { NavigateUri = uri, Foreground = Accent };
            link.RequestNavigate += (_, e) => { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); e.Handled = true; };
            return link;
        }
        return inline;
    }
}
