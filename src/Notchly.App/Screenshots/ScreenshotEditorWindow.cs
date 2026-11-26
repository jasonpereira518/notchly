// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream's ScreenshotEditorController + ScreenshotEditorView: the annotation editor. All behaviour (tools, drags, hit tests,
// crop, canvas expansion, image layers, undo, text selection) is Core's CanvasModel; this window is the tool rail, the style strip, the
// canvas, the Beautify panel (backdrop and watermark) and the actions (copy, save, pin). Text is typed in a box laid over the canvas.
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Notchly.App.Platform;
using Notchly.App.Platform.Capture;
using Notchly.App.Platform.Ocr;
using Notchly.Core.Capture;
using Notchly.Core.Notch;
using Notchly.Core.Settings;
using Serilog;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Cursors = System.Windows.Input.Cursors;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;
using Orientation = System.Windows.Controls.Orientation;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace Notchly.App.Screenshots;

internal sealed class ScreenshotEditorWindow : FluentWindow, ICanvasHost
{
    private static readonly Dictionary<ScreenshotTool, string> Names = new()
    {
        [ScreenshotTool.Select] = "Select", [ScreenshotTool.Arrow] = "Arrow", [ScreenshotTool.Pixelate] = "Blur", [ScreenshotTool.Crop] = "Crop",
        [ScreenshotTool.Text] = "Text", [ScreenshotTool.Sticker] = "Sticker", [ScreenshotTool.Rect] = "Box", [ScreenshotTool.Highlight] = "Marker",
        [ScreenshotTool.Freehand] = "Pen", [ScreenshotTool.Line] = "Line", [ScreenshotTool.Ellipse] = "Oval", [ScreenshotTool.Counter] = "Count",
        [ScreenshotTool.Redact] = "Redact", [ScreenshotTool.Spotlight] = "Spotlight", [ScreenshotTool.ExpandCanvas] = "Expand", [ScreenshotTool.Image] = "Image",
    };

    private readonly ScreenshotService _service;
    private readonly SettingsStore _settings;
    private readonly CanvasModel _model;
    private readonly EditorCanvas _canvas;
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Canvas _overlay = new() { IsHitTestVisible = true };
    private readonly WrapPanel _rail = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly WrapPanel _strip = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly Border _beautify = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(10), CornerRadius = new CornerRadius(8) };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, Margin = new Thickness(10, 0, 0, 0) };
    private readonly Dictionary<ScreenshotTool, Border> _railButtons = [];
    private readonly List<Action> _stripRefreshers = [];
    private BackdropStyle _backdrop;
    private WatermarkStyle _watermark;
    private BitmapSource? _backdropPicture, _watermarkPicture;
    private TextBox? _editor;
    private string _stripSignature = "";
    private bool _fit = true;
    private bool _closingConfirmed;

    public ScreenshotEditorWindow(ScreenshotService service, CapturedImage image, double scale)
    {
        _service = service;
        _settings = service.Settings;
        _backdrop = BackdropStyle.Decoded(_settings.GetString(DefaultsKey.ScreenshotBackdropStyle));
        _watermark = WatermarkStyle.Decoded(_settings.GetString(DefaultsKey.ScreenshotWatermarkStyle));
        _backdropPicture = LoadPicture(_backdrop.ImagePath);
        _watermarkPicture = LoadPicture(_watermark.ImagePath);
        var prefs = new CanvasPreferences(
            _settings.GetString(DefaultsKey.ScreenshotLastTool), _settings.GetString(DefaultsKey.ScreenshotLastColor), _settings.GetString(DefaultsKey.ScreenshotLastStroke),
            _settings.GetInt(DefaultsKey.ScreenshotLastTextSize), _settings.GetInt(DefaultsKey.ScreenshotLastBlurLevel),
            _settings.GetString(DefaultsKey.ScreenshotLastBlurStyle), _settings.GetBool(DefaultsKey.ScreenshotLastBlurTextOnly),
            _settings.GetDouble(DefaultsKey.ScreenshotSpotlightDim, 0.5), _settings.GetString(DefaultsKey.ScreenshotLastArrowStyle),
            _settings.GetString(DefaultsKey.ScreenshotLastSticker), _settings.GetBool(DefaultsKey.ScreenshotAnnotationShadows));
        _model = new CanvasModel(EditorPicture.Asset(image), scale, prefs, this)
        {
            BackdropKey = _backdrop.Encoded(), WatermarkKey = _watermark.Encoded(),
        };
        _model.RefreshDirtyState();
        _model.PreferenceChanged += Remember;
        _model.PropertyChanged += (_, e) => OnModelChanged(e.PropertyName);
        _canvas = new EditorCanvas(Scene);
        _canvas.PointerPressed += CommitTextEditing;
        _canvas.ZoomRequested += factor => { _fit = false; _model.AdjustZoom(factor); ApplyZoom(); };

        Title = "Screenshot editor";
        Width = Math.Min(SystemParameters.WorkArea.Width - 60, Math.Max(900, image.Width / Math.Max(1, scale) + 80));
        Height = Math.Min(SystemParameters.WorkArea.Height - 40, Math.Max(640, image.Height / Math.Max(1, scale) + 240));
        MinWidth = 720;
        MinHeight = 480;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var host = new Grid { Children = { _canvas, _overlay } };
        _scroll.Content = new Border { Padding = new Thickness(60), Child = host };
        _scroll.SizeChanged += (_, _) => ApplyZoom();
        _overlay.Width = double.NaN;
        var scrollBorder = new Border { CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), Child = _scroll };

        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 2), Children = { _rail, _strip } };
        var footer = BuildFooter();
        var bottom = new StackPanel { Children = { footer, _beautify } };
        var layout = new DockPanel { Margin = new Thickness(14, 40, 14, 12), LastChildFill = true };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        layout.Children.Add(top);
        layout.Children.Add(bottom);
        layout.Children.Add(scrollBorder);
        Content = layout;
        BuildRail();
        RebuildStrip();
        BuildBeautify();

        PreviewKeyDown += OnKey;
        Closing += OnClosing;
        Loaded += (_, _) => { ApplyZoom(); _ = RecognizeAsync(_model.BaseImage); };
    }

    internal static void Open(ScreenshotService service, CapturedImage image, double scale)
    {
        var window = new ScreenshotEditorWindow(service, image, scale);
        window.Show();
        window.Activate();
    }

    private EditorScene Scene() => new()
    {
        Model = _model, Backdrop = _backdrop, Watermark = _watermark, BackdropPicture = _backdropPicture, WatermarkPicture = _watermarkPicture,
        Downscale = _settings.GetBool(DefaultsKey.ScreenshotDownscale),
    };

    private void Remember(string key, object value)
    {
        switch (value)
        {
            case string s: _settings.Set(key, s); break;
            case int i: _settings.Set(key, i); break;
            case double d: _settings.Set(key, d); break;
            case bool b: _settings.Set(key, b); break;
        }
    }

    // MARK: ICanvasHost

    SizeD ICanvasHost.MeasureText(string text, int textSize, double scale) => EditorRenderer.MeasureText(text, textSize, scale);

    ImageAsset? ICanvasHost.ExpandImage(ImageAsset image, RectD rect, CanvasFill fill)
    {
        if (image.Payload is not EditorPicture picture) return null;
        int width = (int)Math.Round(rect.Width), height = (int)Math.Round(rect.Height);
        if (width < 1 || height < 1 || (long)width * height > 120_000_000) return null;
        var pixels = new byte[width * height * 4];
        var b = (byte)Math.Round(fill.Blue * 255);
        var g = (byte)Math.Round(fill.Green * 255);
        var r = (byte)Math.Round(fill.Red * 255);
        var a = fill.IsTransparent ? (byte)0 : (byte)255;
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = a; }
        var old = picture.Pixels;
        var offsetX = (int)Math.Round(-rect.X);
        var offsetY = (int)Math.Round(-rect.Y);
        for (var row = 0; row < old.Height; row++)
        {
            var y = row + offsetY;
            if (y < 0 || y >= height) continue;
            var fromX = Math.Max(0, -offsetX);
            var toX = Math.Min(old.Width, width - offsetX);
            if (toX <= fromX) continue;
            Buffer.BlockCopy(old.Pixels, old.Offset + row * old.Stride + fromX * 4, pixels, (y * width + fromX + offsetX) * 4, (toX - fromX) * 4);
        }
        return EditorPicture.Asset(new CapturedImage(pixels, width, height, width * 4));
    }

    ImageAsset? ICanvasHost.CropImage(ImageAsset image, RectD rect)
    {
        if (image.Payload is not EditorPicture picture || rect.Width < 1 || rect.Height < 1) return null;
        return EditorPicture.Asset(picture.Pixels.Crop(rect));
    }

    void ICanvasHost.RecognizeText() => _ = RecognizeAsync(_model.BaseImage);

    void ICanvasHost.Beep() => SystemSounds.Beep.Play();

    private async Task RecognizeAsync(ImageAsset asset)
    {
        try
        {
            if (!TextRecognizer.IsAvailable || asset.Payload is not EditorPicture picture) return;
            var tiles = ScreenshotRecognition.Tiles(asset.PixelWidth, asset.PixelHeight);
            var reads = new List<IReadOnlyList<BandWord>?>();
            foreach (var tile in tiles)
                reads.Add(await TextRecognizer.ReadWordsAsync(picture.Pixels.Crop(tile.Rect), tile.Rect.X, tile.Rect.Y));
            var merged = ScreenshotRecognition.Merge(reads, tiles);
            await Dispatcher.InvokeAsync(() =>
            {
                if (_model.ApplyRecognition(asset, merged.Words, merged.Runs)) _canvas.Refresh();
            });
        }
        catch (Exception e)
        {
            Log.Debug(e, "The editor could not read the text of the capture");
        }
    }

    // MARK: Model changes

    private void OnModelChanged(string? name)
    {
        if (name is nameof(CanvasModel.EditingTextId)) BeginOrEndTextEditing();
        if (name is nameof(CanvasModel.BaseImage)) { Dispatcher.BeginInvoke(ApplyZoom); }
        var signature = Signature();
        if (signature != _stripSignature) RebuildStrip();
        foreach (var refresh in _stripRefreshers) refresh();
        foreach (var (tool, button) in _railButtons)
            button.Background = tool == _model.Tool ? new SolidColorBrush(Color.FromArgb(0x66, 0x2E, 0x8C, 0xFF)) : Brushes.Transparent;
        _canvas.Refresh();
        Title = _model.IsDirty ? "Screenshot editor •" : "Screenshot editor";
        _status.Text = $"{_model.BaseImage.PixelWidth} × {_model.BaseImage.PixelHeight}";
    }

    private string Signature() => string.Join("|", _model.Tool, _model.ShowsColorControls, _model.ShowsBlurControls, _model.ShowsSpotlightControls,
        _model.ShowsTextSizeControls, _model.ShowsArrowStyleControls, _model.ShowsLayerControls, _model.SelectedId is not null, _model.CropDraft is not null, _model.BlurStyle);

    private void ApplyZoom()
    {
        if (_scroll.ViewportWidth <= 0 || _scroll.ViewportHeight <= 0) return;
        var width = Math.Max(100, _scroll.ViewportWidth - 120);
        var height = Math.Max(100, _scroll.ViewportHeight - 120);
        var layout = Scene().Layout();
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var fit = Math.Min(1 / dpi, Math.Min(width / layout.Canvas.Width, height / layout.Canvas.Height));
        var zoom = _fit || _model.ZoomOverride is null ? fit : _model.ZoomOverride.Value / dpi;
        if (_fit) _model.ZoomOverride = null;
        _model.CurrentDisplayZoom = zoom * dpi;
        _canvas.Zoom = zoom;
        _overlay.Width = layout.Canvas.Width * zoom;
        _overlay.Height = layout.Canvas.Height * zoom;
    }

    // MARK: Tool rail and strip

    private void BuildRail()
    {
        _rail.Children.Clear();
        _railButtons.Clear();
        var order = ScreenshotTools.Ordered(_settings.GetString(DefaultsKey.ScreenshotToolOrder));
        var number = 1;
        foreach (var tool in order)
        {
            var label = number <= ScreenshotTools.ShortcutLimit && _settings.GetBool(DefaultsKey.ScreenshotToolShortcutsEnabled) ? $"{number}  {Names[tool]}" : Names[tool];
            number++;
            var button = Chip(label, () => _model.Tool = tool);
            _railButtons[tool] = button;
            _rail.Children.Add(button);
        }
        OnModelChanged(null);
    }

    private static Border Chip(string text, Action click, string? tip = null)
    {
        var label = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var chip = new Border
        {
            Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 4, 4), CornerRadius = new CornerRadius(7), Cursor = Cursors.Hand,
            Child = label, Background = Brushes.Transparent, BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)), BorderThickness = new Thickness(1),
            ToolTip = tip,
        };
        chip.MouseLeftButtonUp += (_, _) => click();
        return chip;
    }

    private void RebuildStrip()
    {
        _stripSignature = Signature();
        _strip.Children.Clear();
        _stripRefreshers.Clear();
        if (_model.ShowsColorControls)
        {
            var swatches = new Dictionary<AnnotationColor, Border>();
            foreach (var color in Enum.GetValues<AnnotationColor>())
            {
                var swatch = new Border
                {
                    Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Margin = new Thickness(2, 0, 2, 4), Cursor = Cursors.Hand,
                    Background = new SolidColorBrush(EditorRenderer.ColorOf(color)), BorderThickness = new Thickness(2),
                };
                swatch.MouseLeftButtonUp += (_, _) => _model.Color = color;
                swatches[color] = swatch;
                _strip.Children.Add(swatch);
            }
            _stripRefreshers.Add(() => { foreach (var (c, s) in swatches) s.BorderBrush = c == _model.Color ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)); });
            _strip.Children.Add(Gap());
            if (_model.Tool is not (ScreenshotTool.Highlight or ScreenshotTool.Redact or ScreenshotTool.Counter or ScreenshotTool.Text or ScreenshotTool.Sticker))
                foreach (var stroke in Enum.GetValues<StrokeWidth>())
                {
                    var chip = Chip(stroke switch { StrokeWidth.Small => "Thin", StrokeWidth.Medium => "Medium", _ => "Thick" }, () => _model.Stroke = stroke);
                    _stripRefreshers.Add(() => chip.Background = _model.Stroke == stroke ? new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255)) : Brushes.Transparent);
                    _strip.Children.Add(chip);
                }
        }
        if (_model.ShowsTextSizeControls)
        {
            var combo = new ComboBox { Width = 80, Margin = new Thickness(0, 0, 8, 4) };
            foreach (var size in AnnotationSizes.TextSizes) combo.Items.Add(size);
            combo.SelectedItem = _model.TextSize;
            combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is int size) _model.TextSize = size; };
            _stripRefreshers.Add(() => { if (combo.SelectedItem as int? != _model.TextSize) combo.SelectedItem = _model.TextSize; });
            _strip.Children.Add(combo);
        }
        if (_model.ShowsArrowStyleControls)
        {
            var combo = new ComboBox { Width = 130, Margin = new Thickness(0, 0, 8, 4) };
            foreach (var style in Enum.GetValues<ArrowStyle>()) combo.Items.Add(style);
            combo.SelectedItem = _model.ArrowStyle;
            combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ArrowStyle style) _model.ArrowStyle = style; };
            _stripRefreshers.Add(() => { if (combo.SelectedItem as ArrowStyle? != _model.ArrowStyle) combo.SelectedItem = _model.ArrowStyle; });
            _strip.Children.Add(combo);
        }
        if (_model.ShowsBlurControls)
        {
            foreach (var style in Enum.GetValues<BlurStyle>())
            {
                var chip = Chip(style.ToString(), () => _model.BlurStyle = style);
                _stripRefreshers.Add(() => chip.Background = _model.BlurStyle == style ? new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255)) : Brushes.Transparent);
                _strip.Children.Add(chip);
            }
            if (_model.BlurStyle.UsesStrength())
            {
                var slider = new Slider { Minimum = 1, Maximum = 5, Width = 110, TickFrequency = 1, IsSnapToTickEnabled = true, Value = _model.BlurLevel, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 4) };
                slider.ValueChanged += (_, _) => _model.BlurLevel = (int)slider.Value;
                _stripRefreshers.Add(() => { if ((int)slider.Value != _model.BlurLevel) slider.Value = _model.BlurLevel; });
                _strip.Children.Add(slider);
            }
            var textOnly = new CheckBox { Content = "Text only", IsChecked = _model.BlurTextOnly, Margin = new Thickness(6, 0, 6, 4), VerticalAlignment = VerticalAlignment.Center };
            textOnly.Click += (_, _) => _model.BlurTextOnly = textOnly.IsChecked == true;
            _stripRefreshers.Add(() => { textOnly.IsEnabled = TextRecognizer.IsAvailable; if (textOnly.IsChecked != _model.BlurTextOnly) textOnly.IsChecked = _model.BlurTextOnly; });
            _strip.Children.Add(textOnly);
        }
        if (_model.ShowsSpotlightControls)
        {
            _strip.Children.Add(new TextBlock { Text = "Dim", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 4) });
            var slider = new Slider { Minimum = 0, Maximum = 1, Width = 140, Value = _model.SpotlightDimStrength, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 4) };
            slider.ValueChanged += (_, _) => _model.SetSpotlightDimStepped(slider.Value);
            _strip.Children.Add(slider);
        }
        if (_model.Tool == ScreenshotTool.Sticker)
            foreach (var sticker in Enum.GetValues<StickerKind>())
            {
                var chip = Chip(sticker.Glyph(), () => _model.Sticker = sticker);
                _stripRefreshers.Add(() => chip.Background = _model.Sticker == sticker ? new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255)) : Brushes.Transparent);
                _strip.Children.Add(chip);
            }
        if (_model.Tool == ScreenshotTool.Crop)
        {
            _strip.Children.Add(Chip("Apply crop (Enter)", () => _model.ApplyCrop()));
            _strip.Children.Add(Chip("Cancel", () => _model.CropDraft = null));
        }
        if (_model.Tool == ScreenshotTool.ExpandCanvas)
        {
            _strip.Children.Add(new TextBlock { Text = "Drag the edges. Fill:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 4) });
            _strip.Children.Add(Chip("White", () => _model.ExpansionFill = CanvasFill.White));
            _strip.Children.Add(Chip("Black", () => _model.ExpansionFill = CanvasFill.Color(0, 0, 0)));
            _strip.Children.Add(Chip("Transparent", () => _model.ExpansionFill = CanvasFill.Transparent));
        }
        if (_model.SelectedId is not null)
        {
            _strip.Children.Add(Gap());
            if (_model.ShowsLayerControls)
            {
                _strip.Children.Add(Chip("Forward", () => _model.MoveSelected(LayerMove.Forward)));
                _strip.Children.Add(Chip("Backward", () => _model.MoveSelected(LayerMove.Backward)));
            }
            _strip.Children.Add(Chip("Delete", () => _model.DeleteSelected()));
        }
        var shadows = new CheckBox { Content = "Shadows", IsChecked = _model.AnnotationShadowsEnabled, Margin = new Thickness(10, 0, 0, 4), VerticalAlignment = VerticalAlignment.Center };
        shadows.Click += (_, _) => _model.AnnotationShadowsEnabled = shadows.IsChecked == true;
        _strip.Children.Add(shadows);
    }

    private static FrameworkElement Gap() => new Border { Width = 10 };

    // MARK: Text typing

    private void BeginOrEndTextEditing()
    {
        if (_model.EditingTextId is not { } id)
        {
            if (_editor is not null) { _overlay.Children.Remove(_editor); _editor = null; }
            return;
        }
        var mark = _model.Annotations.FirstOrDefault(a => a.Id == id);
        if (mark is null) return;
        _overlay.Children.Remove(_editor);
        var zoom = _canvas.Zoom;
        var at = _canvas.ToView(new PointD(mark.Rect.X, mark.Rect.Y));
        var editor = new TextBox
        {
            Text = mark.Text, AcceptsReturn = false, FontSize = Math.Max(8, mark.TextSize * _model.Scale * zoom), FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.SemiBold, MinWidth = 40, Padding = new Thickness(2, 0, 2, 0), BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(EditorRenderer.ColorOf(mark.Color)), Background = new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)),
            CaretBrush = Brushes.White,
        };
        Canvas.SetLeft(editor, at.X);
        Canvas.SetTop(editor, at.Y);
        editor.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape) { e.Handled = true; CommitTextEditing(); _canvas.Focus(); }
        };
        editor.LostKeyboardFocus += (_, _) => CommitTextEditing();
        _overlay.Children.Add(editor);
        _editor = editor;
        editor.Dispatcher.BeginInvoke(() => { editor.Focus(); editor.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CommitTextEditing()
    {
        if (_editor is null || _model.EditingTextId is not { } id) return;
        var text = _editor.Text;
        var editor = _editor;
        _editor = null;
        _overlay.Children.Remove(editor);
        _model.CommitText(id, text);
    }

    // MARK: Keys

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_editor is not null) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (Keyboard.FocusedElement is TextBox or ComboBox) return;
        e.Handled = true;
        switch (key)
        {
            case Key.Z when ctrl && shift: _model.Redo(); break;
            case Key.Z when ctrl: _model.Undo(); break;
            case Key.Y when ctrl: _model.Redo(); break;
            case Key.C when ctrl: CopyAction(); break;
            case Key.S when ctrl: SaveAction(); break;
            case Key.V when ctrl: PasteAction(); break;
            case Key.D0 when ctrl: _fit = true; ApplyZoom(); break;
            case Key.OemPlus or Key.Add when ctrl: _fit = false; _model.AdjustZoom(1.2); ApplyZoom(); break;
            case Key.OemMinus or Key.Subtract when ctrl: _fit = false; _model.AdjustZoom(1 / 1.2); ApplyZoom(); break;
            case Key.Delete or Key.Back: _model.DeleteSelected(); break;
            case Key.Enter when _model.CropDraft is not null: _model.ApplyCrop(); break;
            case Key.Escape:
                if (_model.CropDraft is not null) _model.CropDraft = null;
                else if (_model.SelectedId is not null) _model.SelectedId = null;
                else Close();
                break;
            default:
                var digit = key is >= Key.D1 and <= Key.D9 ? key - Key.D0 : key is >= Key.NumPad1 and <= Key.NumPad9 ? key - Key.NumPad0 : 0;
                if (!ctrl && digit > 0 && ScreenshotTools.ShortcutTool(digit, _settings.GetString(DefaultsKey.ScreenshotToolOrder),
                        _settings.GetBool(DefaultsKey.ScreenshotToolShortcutsEnabled)) is { } tool) _model.Tool = tool;
                else e.Handled = false;
                break;
        }
    }

    // MARK: Footer and actions

    private FrameworkElement BuildFooter()
    {
        var bar = new DockPanel { LastChildFill = false };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(Action("Undo", () => _model.Undo()));
        left.Children.Add(Action("Redo", () => _model.Redo()));
        left.Children.Add(Action("Fit", () => { _fit = true; ApplyZoom(); }));
        left.Children.Add(Action("Beautify", () => _beautify.Visibility = _beautify.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible));
        left.Children.Add(Action("Add image", AddImageAction));
        left.Children.Add(_status);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(Action("Pin", PinAction));
        right.Children.Add(Action("Copy", CopyAction));
        right.Children.Add(Action("Save", SaveAction));
        right.Children.Add(Action("Save as…", SaveAsAction));
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        bar.Children.Add(left);
        return bar;
    }

    private static Button Action(string text, Action click)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(12, 5, 12, 5) };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>The picture as it will be saved: backdrop, marks, watermark.</summary>
    private CapturedImage RenderExport(out double scale)
    {
        CommitTextEditing();
        var scene = Scene();
        var layout = scene.Layout();
        var width = Math.Max(1, (int)Math.Round(layout.Output.Width));
        var height = Math.Max(1, (int)Math.Round(layout.Output.Height));
        var k = layout.Output.Width / layout.Canvas.Width;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) EditorRenderer.DrawFrame(dc, scene, layout, k, new BlurPatches(), forExport: true);
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        scale = layout.OutputScale;
        return ImageCodec.FromBitmap(target);
    }

    private void CopyAction()
    {
        if (_model.SelectedWordIndexes.Count > 0 && _model.SelectedText.Length > 0)
        {
            if (Notchly.App.Tools.ClipboardWrite.TrySetText(_model.SelectedText)) Notchly.App.Tools.QuickToolHud.Show("Text copied");
            return;
        }
        var image = RenderExport(out var scale);
        if (_service.CopyToClipboard(image, scale)) { _model.MarkExported(); Notchly.App.Tools.QuickToolHud.Show("Screenshot copied"); }
    }

    private void SaveAction()
    {
        var image = RenderExport(out var scale);
        if (_service.SaveToFolder(image, scale) is { } path)
        {
            _model.MarkExported();
            _service.History.Add(image, scale, WindowArea());
            Notchly.App.Tools.QuickToolHud.Show("Saved " + Path.GetFileName(path));
        }
    }

    private void SaveAsAction()
    {
        var dialog = new SaveFileDialog { Filter = "PNG|*.png|JPEG|*.jpg", FileName = "Screenshot", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        var image = RenderExport(out var scale);
        try
        {
            var jpeg = dialog.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || dialog.FileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
            File.WriteAllBytes(dialog.FileName, jpeg ? ImageCodec.EncodeJpeg(image, scale) : ImageCodec.EncodePng(image, scale));
            _model.MarkExported();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Notchly.App.Tools.QuickToolHud.Show("The picture could not be saved");
        }
    }

    private void PinAction()
    {
        var image = RenderExport(out var scale);
        _service.Pin(image, scale, WindowArea());
    }

    private Native.RECT WindowArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        Native.GetWindowRect(handle, out var rect);
        return new Native.RECT { Left = rect.Left + 40, Top = rect.Top + 60, Right = rect.Left + 40 + 10, Bottom = rect.Top + 60 + 10 };
    }

    private void PasteAction()
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsImage()) return;
            var picture = ImageCodec.FromBitmap(System.Windows.Clipboard.GetImage());
            if (!_model.PasteImage(EditorPicture.Asset(picture))) SystemSounds.Beep.Play();
        }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    private void AddImageAction()
    {
        var dialog = new OpenFileDialog { Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        if (ImageCodec.DecodeFile(dialog.FileName) is not { } picture) { SystemSounds.Beep.Play(); return; }
        if (!_model.PasteImage(EditorPicture.Asset(picture))) SystemSounds.Beep.Play();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closingConfirmed || !_model.IsDirty) return;
        var result = System.Windows.MessageBox.Show(this, "Close without saving your changes?", "Screenshot editor", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes) _closingConfirmed = true;
        else e.Cancel = true;
    }

    // MARK: Beautify

    private static BitmapSource? LoadPicture(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or FileFormatException or UriFormatException) { return null; }
    }

    private void StyleChanged()
    {
        _backdrop = _backdrop.Sanitized();
        _watermark = _watermark.Sanitized();
        _settings.Set(DefaultsKey.ScreenshotBackdropStyle, _backdrop.Encoded());
        _settings.Set(DefaultsKey.ScreenshotWatermarkStyle, _watermark.Encoded());
        _model.BackdropKey = _backdrop.Encoded();
        _model.WatermarkKey = _watermark.Encoded();
        ApplyZoom();
        _canvas.Refresh();
    }

    private void BuildBeautify()
    {
        _beautify.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        var panel = new StackPanel();
        // Backdrop.
        var backdropRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        backdropRow.Children.Add(new TextBlock { Text = "Backdrop", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        var kinds = new ComboBox { Width = 130, Margin = new Thickness(0, 0, 8, 0) };
        string[] kindNames = ["None", "Ocean", "Sunset", "Forest", "Candy", "Graphite", "Solid", "Gradient", "Image"];
        foreach (var name in kindNames) kinds.Items.Add(name);
        kinds.SelectedIndex = KindIndex(_backdrop);
        kinds.SelectionChanged += (_, _) =>
        {
            var index = kinds.SelectedIndex;
            _backdrop = index switch
            {
                0 => _backdrop with { Kind = BackdropKind.None },
                >= 1 and <= 5 => _backdrop with { Kind = BackdropKind.Preset, PresetId = kindNames[index].ToLowerInvariant() },
                6 => _backdrop with { Kind = BackdropKind.Solid, Colors = [[0.12, 0.12, 0.16]] },
                7 => _backdrop with { Kind = BackdropKind.Gradient, Colors = [[0.35, 0.2, 0.9], [0.95, 0.4, 0.5]] },
                _ => PickBackdropImage(),
            };
            kinds.SelectedIndex = KindIndex(_backdrop);
            StyleChanged();
        };
        backdropRow.Children.Add(kinds);
        backdropRow.Children.Add(Labeled("Padding", _backdrop.Padding, v => { _backdrop = _backdrop with { Padding = v }; StyleChanged(); }));
        backdropRow.Children.Add(Labeled("Corners", _backdrop.CornerRadius, v => { _backdrop = _backdrop with { CornerRadius = v }; StyleChanged(); }));
        backdropRow.Children.Add(Labeled("Blur", _backdrop.Blur, v => { _backdrop = _backdrop with { Blur = v }; StyleChanged(); }));
        panel.Children.Add(backdropRow);
        // Watermark.
        var markRow = new WrapPanel();
        markRow.Children.Add(new TextBlock { Text = "Watermark", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        var markKind = new ComboBox { Width = 90, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var name in new[] { "None", "Text", "Image" }) markKind.Items.Add(name);
        markKind.SelectedIndex = (int)_watermark.Kind;
        var text = new TextBox { Width = 160, Text = _watermark.Text, Margin = new Thickness(0, 0, 8, 0), MaxLength = WatermarkStyle.TextLimit };
        text.TextChanged += (_, _) => { _watermark = _watermark with { Text = text.Text }; StyleChanged(); };
        markKind.SelectionChanged += (_, _) =>
        {
            var kind = (WatermarkKind)Math.Max(0, markKind.SelectedIndex);
            if (kind == WatermarkKind.Image && !PickWatermarkImage()) { markKind.SelectedIndex = (int)_watermark.Kind; return; }
            _watermark = _watermark with { Kind = kind };
            StyleChanged();
        };
        var anchors = new ComboBox { Width = 130, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var anchor in Enum.GetValues<WatermarkAnchor>()) anchors.Items.Add(anchor);
        anchors.SelectedItem = _watermark.Anchor;
        anchors.SelectionChanged += (_, _) => { if (anchors.SelectedItem is WatermarkAnchor a) { _watermark = _watermark with { Anchor = a }; StyleChanged(); } };
        var colors = new ComboBox { Width = 90, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var name in WatermarkColors.Names) colors.Items.Add(name);
        colors.SelectedItem = _watermark.Color;
        colors.SelectionChanged += (_, _) => { if (colors.SelectedItem is string c) { _watermark = _watermark with { Color = c }; StyleChanged(); } };
        markRow.Children.Add(markKind);
        markRow.Children.Add(text);
        markRow.Children.Add(colors);
        markRow.Children.Add(anchors);
        markRow.Children.Add(Labeled("Size", _watermark.Size, v => { _watermark = _watermark with { Size = v }; StyleChanged(); }));
        markRow.Children.Add(Labeled("Opacity", _watermark.Opacity, v => { _watermark = _watermark with { Opacity = Math.Max(WatermarkStyle.OpacityMinimum, v) }; StyleChanged(); }));
        markRow.Children.Add(Labeled("Tilt", (_watermark.Rotation + WatermarkStyle.RotationLimit) / (2 * WatermarkStyle.RotationLimit),
            v => { _watermark = _watermark with { Rotation = v * 2 * WatermarkStyle.RotationLimit - WatermarkStyle.RotationLimit }; StyleChanged(); }));
        panel.Children.Add(markRow);
        // Presets.
        var presets = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        presets.Children.Add(Action("Save backdrop preset", () => SavePreset(backdrop: true)));
        presets.Children.Add(Action("Save watermark preset", () => SavePreset(backdrop: false)));
        presets.Children.Add(Action("Load preset…", ShowPresets));
        panel.Children.Add(presets);
        _beautify.Child = panel;
    }

    private static int KindIndex(BackdropStyle style) => style.Kind switch
    {
        BackdropKind.None => 0,
        BackdropKind.Preset => BackdropIds.Parse(style.PresetId) is { } id ? (int)id : 0,
        BackdropKind.Solid => 6,
        BackdropKind.Gradient => 7,
        _ => 8,
    };

    private static FrameworkElement Labeled(string label, double value, Action<double> changed)
    {
        var slider = new Slider { Minimum = 0, Maximum = 1, Width = 90, Value = Math.Clamp(value, 0, 1), VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, _) => changed(slider.Value);
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 0),
            Children = { new TextBlock { Text = label, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 }, slider },
        };
    }

    private BackdropStyle PickBackdropImage()
    {
        var dialog = new OpenFileDialog { Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*" };
        if (dialog.ShowDialog(this) != true || LoadPicture(dialog.FileName) is not { } picture) return _backdrop;
        _backdropPicture = picture;
        return _backdrop with { Kind = BackdropKind.Image, ImagePath = dialog.FileName };
    }

    private bool PickWatermarkImage()
    {
        var dialog = new OpenFileDialog { Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*" };
        if (dialog.ShowDialog(this) != true || LoadPicture(dialog.FileName) is not { } picture) return false;
        _watermarkPicture = picture;
        _watermark = _watermark with { ImagePath = dialog.FileName };
        return true;
    }

    private void SavePreset(bool backdrop)
    {
        if (backdrop)
        {
            var list = ScreenshotPresets.DecodeBackdrops(_settings.GetString(DefaultsKey.ScreenshotBackdropPresets)).ToList();
            list.Insert(0, _backdrop);
            _settings.Set(DefaultsKey.ScreenshotBackdropPresets, ScreenshotPresets.EncodeBackdrops(list.Take(ScreenshotPresets.Limit)));
        }
        else
        {
            var list = ScreenshotPresets.DecodeWatermarks(_settings.GetString(DefaultsKey.ScreenshotWatermarkPresets)).ToList();
            list.Insert(0, _watermark);
            _settings.Set(DefaultsKey.ScreenshotWatermarkPresets, ScreenshotPresets.EncodeWatermarks(list.Take(ScreenshotPresets.Limit)));
        }
        Notchly.App.Tools.QuickToolHud.Show("Preset saved");
    }

    private void ShowPresets()
    {
        var menu = new ContextMenu { PlacementTarget = _beautify, IsOpen = true };
        foreach (var style in ScreenshotPresets.DecodeBackdrops(_settings.GetString(DefaultsKey.ScreenshotBackdropPresets)))
        {
            var item = new MenuItem { Header = $"Backdrop · {style.Kind} {style.PresetId}" };
            var captured = style;
            item.Click += (_, _) => { _backdrop = captured; _backdropPicture = LoadPicture(captured.ImagePath); StyleChanged(); RebuildBeautify(); };
            menu.Items.Add(item);
        }
        foreach (var style in ScreenshotPresets.DecodeWatermarks(_settings.GetString(DefaultsKey.ScreenshotWatermarkPresets)))
        {
            var item = new MenuItem { Header = $"Watermark · {style.Kind} {(style.Kind == WatermarkKind.Text ? style.Text : "")}" };
            var captured = style;
            item.Click += (_, _) => { _watermark = captured; _watermarkPicture = LoadPicture(captured.ImagePath); StyleChanged(); RebuildBeautify(); };
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "No presets saved yet", IsEnabled = false });
    }

    private void RebuildBeautify() => BuildBeautify();
}
