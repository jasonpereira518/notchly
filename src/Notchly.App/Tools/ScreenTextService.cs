// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/QuickTools/ScreenTextService.swift: the shortcut opens a frozen screen, a dragged area is
// read for codes first and then for text, all on this PC. A code opens its own panel and is never copied by itself; text is copied
// and a toast says so. Only the newest request may write, a newer copy by the person is never overwritten, and a feature turned off
// while it reads loses its result. Not ported: the second "fast" recognition pass (Windows' engine has one mode) and the capture
// chooser of upstream's unified capture (M10).
using System.Windows.Threading;
using Notchly.App.Platform;
using Notchly.App.Platform.Capture;
using Notchly.App.Platform.Input;
using Notchly.App.Platform.Ocr;
using Notchly.App.Shell;
using Notchly.Core.Settings;
using Notchly.Core.Shortcuts;
using Notchly.Core.Tools;
using Serilog;

namespace Notchly.App.Tools;

public sealed class ScreenTextService(SettingsStore settings, HotkeyService hotkeys, Dispatcher dispatcher) : IFeatureService
{
    public const string Purpose = "Copy text from screen";
    public const string Hint = "Drag across the text or code to read. Press Esc to cancel.";
    public const string Copied = "Text copied", NoText = "No text found";

    private IDisposable? _registration;
    private string? _registered;
    private CaptureOverlay? _overlay;
    private int _generation;
    private bool _disposed;
    private QrResultWindow? _result;

    public bool ShortcutUnavailable { get; private set; }

    public void Sync()
    {
        var wanted = settings.GetBool(DefaultsKey.ScreenOcrShortcutEnabled) ? settings.GetString(DefaultsKey.ScreenOcrShortcut) : "";
        if (wanted == _registered) return;
        _registration?.Dispose();
        _registration = null;
        _registered = wanted;
        ShortcutUnavailable = false;
        if (wanted.Length == 0 || Shortcut.Parse(wanted) is not { } shortcut) return;
        _registration = hotkeys.Register(shortcut, () => dispatcher.BeginInvoke(Capture));
        ShortcutUnavailable = _registration is null;
    }

    /// <summary>Opens the area picker. A second call while it is open closes it.</summary>
    public void Capture()
    {
        if (_overlay is not null) { _overlay.Cancel(); return; }
        _result?.Close();
        var overlay = new CaptureOverlay(CaptureMode.Region, Purpose, Hint);
        overlay.RegionCompleted += region => { if (region is not null) _ = RecognizeAsync(region.Image); };
        overlay.Closed += () => _overlay = null;
        _overlay = overlay;
        try
        {
            if (!overlay.Open()) { _overlay = null; QuickToolHud.Show("The screen could not be read"); }
        }
        catch (Exception e)
        {
            _overlay = null;
            Log.Warning(e, "Copy text from screen could not open");
        }
    }

    private async Task RecognizeAsync(CapturedImage image)
    {
        var generation = ++_generation;
        var clipboardAtStart = Native.GetClipboardSequenceNumber();
        var detectCodes = settings.GetBool(DefaultsKey.ScreenOcrDetectQrCodes);
        var removeBreaks = settings.GetBool(DefaultsKey.ScreenOcrRemoveLineBreaks);
        try
        {
            string codes = "", text = "";
            if (detectCodes) codes = ScreenTextSupport.JoinBarcodePayloads(await Task.Run(() => QrReader.Read(image)));
            if (codes.Length == 0)
                text = ScreenTextSupport.JoinRecognizedText(await TextRecognizer.ReadLinesAsync(image), removeBreaks);
            // Back on the UI thread. A newer request, or the feature switched off meanwhile, drops this result.
            if (_disposed || generation != _generation) return;
            var outcome = ScreenTextSupport.Outcome(codes, text);
            switch (outcome.Kind)
            {
                case ScreenTextKind.Qr:
                    _result = new QrResultWindow(outcome.Value);
                    _result.Closed += (_, _) => _result = null;
                    _result.Show();
                    break;
                case ScreenTextKind.Text:
                    if (!ScreenTextSupport.MayWrite(generation, _generation, !_disposed, clipboardAtStart, Native.GetClipboardSequenceNumber())) return;
                    if (ClipboardWrite.TrySetText(outcome.Value)) QuickToolHud.Show(Copied);
                    else QuickToolHud.Show("The text could not be copied");
                    break;
                default:
                    QuickToolHud.Show(NoText);
                    break;
            }
        }
        catch (Exception e)
        {
            Log.Warning(e, "Copy text from screen failed");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _generation++;
        _registration?.Dispose();
        _overlay?.Cancel();
        _result?.Close();
    }
}
