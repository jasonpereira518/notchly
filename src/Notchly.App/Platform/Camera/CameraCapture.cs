// SPDX-License-Identifier: GPL-3.0-or-later
// The capture half of upstream Services/QuickTools/CameraPreviewService.swift (startSession, stopSession,
// refreshDevices). Windows-specific: WPF has no camera element like AVFoundation's preview layer, so a
// MediaCapture in shared, video-only mode feeds a MediaFrameReader whose BGRA frames are copied into one reused
// WriteableBitmap on the UI thread. Nothing is recorded or kept: each frame is overwritten by the next.
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Notchly.Core.Camera;
using Serilog;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace Notchly.App.Platform.Camera;

/// <summary>A camera Windows offers for video capture.</summary>
public sealed record CameraDevice(string Id, string Name);

/// <summary>
/// One camera session at a time: start it for a device, read its frames as a <see cref="WriteableBitmap"/>,
/// stop it to release the device (and its light). Use it from the UI thread; stopping twice is harmless.
/// </summary>
public sealed class CameraCapture : IDisposable
{
    /// <summary>The largest frame the mirror asks for; bigger camera formats are scaled down by Windows.</summary>
    public const int MaximumWidth = 1280, MaximumHeight = 720;

    private readonly Dispatcher _dispatcher;
    private readonly object _gate = new();
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private int _generation;
    // Frame hand-off: the reader's thread fills _pixels only while no copy is waiting for the UI thread.
    private byte[] _pixels = [];
    private int _frameWidth, _frameHeight, _frameStride;
    private int _copyPending, _framesArrived;

    public CameraCapture(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public CameraPreviewState State { get; private set; } = CameraPreviewState.Idle;

    /// <summary>The cameras found when the session last started.</summary>
    public IReadOnlyList<CameraDevice> Devices { get; private set; } = [];

    /// <summary>The camera the session runs on, or null when stopped.</summary>
    public string? DeviceId { get; private set; }

    /// <summary>The latest frame; a new bitmap only when the frame size changes.</summary>
    public WriteableBitmap? Frame { get; private set; }

    /// <summary>Frames shown since the session started (frames the UI was too busy for are dropped, not counted).</summary>
    public int FramesShown { get; private set; }

    /// <summary>Frames the camera delivered since the session started, shown or dropped.</summary>
    public int FramesArrived => Volatile.Read(ref _framesArrived);

    /// <summary>The camera's own format as the session found it, for the log ("1920×1080 MJPG at 30 fps").</summary>
    public string SourceFormat { get; private set; } = "";

    public event Action? StateChanged;

    /// <summary>Raised on the UI thread after <see cref="Frame"/> took a new image.</summary>
    public event Action? FrameShown;

    /// <summary>Video capture devices that are turned on, in Windows' order.</summary>
    public static async Task<IReadOnlyList<CameraDevice>> FindDevicesAsync()
    {
        try
        {
            var found = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            return found.Where(d => d.IsEnabled).Select(d => new CameraDevice(d.Id, d.Name)).ToList();
        }
        catch (Exception e)
        {
            Log.Warning(e, "Listing cameras failed");
            return [];
        }
    }

    /// <summary>
    /// Starts the camera <paramref name="deviceId"/>, or the first one when it is gone or null. A session already
    /// running stops first, so two never fight over a camera.
    /// </summary>
    public async Task StartAsync(string? deviceId)
    {
        var generation = ++_generation;
        await ReleaseAsync();
        SetState(CameraPreviewState.Starting);
        Devices = await FindDevicesAsync();
        if (generation != _generation) return;
        var device = Devices.FirstOrDefault(d => d.Id == deviceId) ?? Devices.FirstOrDefault();
        if (device is null) { SetState(CameraPreviewState.NoCamera); return; }
        DeviceId = device.Id;

        var capture = new MediaCapture();
        try
        {
            // Shared and read-only: another app (the call itself) can open the camera too.
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = device.Id,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly,
            });
        }
        catch (UnauthorizedAccessException)
        {
            capture.Dispose();
            if (generation == _generation) SetState(CameraPreviewState.Denied);
            return;
        }
        catch (Exception e)
        {
            capture.Dispose();
            Log.Warning("Camera could not start: {Message} (0x{Code:X8})", e.Message, e.HResult);
            if (generation == _generation) SetState(CameraPreviewState.Unavailable);
            return;
        }
        if (generation != _generation) { capture.Dispose(); return; }

        MediaFrameReader? reader = null;
        var stored = false;
        try
        {
            var source = capture.FrameSources.Values
                .Where(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
                .OrderBy(s => s.Info.MediaStreamType switch
                {
                    MediaStreamType.VideoPreview => 0,
                    MediaStreamType.VideoRecord => 1,
                    _ => 2,
                })
                .FirstOrDefault();
            if (source is null) throw new InvalidOperationException("The camera offers no colour stream.");
            // Shared mode cannot choose the camera's format: the reader scales whatever it runs at to fit 1280x720.
            var size = OutputSize(source.CurrentFormat);
            SourceFormat = Describe(source.CurrentFormat);
            reader = size is { } scaled
                ? await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8, scaled)
                : await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            if (generation != _generation) throw new OperationCanceledException();
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            reader.FrameArrived += OnFrameArrived;
            capture.Failed += OnCaptureFailed;
            lock (_gate)
            {
                _capture = capture;
                _reader = reader;
            }
            stored = true;
            var status = await reader.StartAsync();
            // Stopped (or started again) meanwhile: that already released this session.
            if (generation != _generation) return;
            if (status != MediaFrameReaderStartStatus.Success) throw new InvalidOperationException($"The frame reader said {status}.");
        }
        catch (Exception e)
        {
            bool ours;
            lock (_gate)
            {
                ours = !stored || _reader == reader;
                if (stored && ours) { _reader = null; _capture = null; }
            }
            if (ours)
            {
                if (reader is not null) { reader.FrameArrived -= OnFrameArrived; reader.Dispose(); }
                capture.Failed -= OnCaptureFailed;
                capture.Dispose();
            }
            if (e is not OperationCanceledException) Log.Warning("Camera could not start: {Message}", e.Message);
            if (generation == _generation) SetState(CameraPreviewState.Unavailable);
            return;
        }
        if (generation != _generation) return;
        FramesShown = 0;
        Interlocked.Exchange(ref _framesArrived, 0);
        Log.Information("Camera started: {Name}, {Format}", device.Name, SourceFormat);
        SetState(CameraPreviewState.Running);
    }

    /// <summary>Stops the camera and releases it at once, so its light goes off.</summary>
    public async Task StopAsync()
    {
        ++_generation;
        await ReleaseAsync();
        DeviceId = null;
        SetState(CameraPreviewState.Idle);
    }

    /// <summary>A frame size within 1280×720 that keeps the camera's shape; null when it already fits.</summary>
    private static BitmapSize? OutputSize(MediaFrameFormat? format)
    {
        if (format?.VideoFormat is not { Width: > 0, Height: > 0 } video) return null;
        var scale = Math.Min(1.0, Math.Min((double)MaximumWidth / video.Width, (double)MaximumHeight / video.Height));
        if (scale >= 1) return null;
        // Even sizes, as video scalers prefer.
        return new BitmapSize
        {
            Width = (uint)Math.Max(2, (int)(video.Width * scale) & ~1),
            Height = (uint)Math.Max(2, (int)(video.Height * scale) & ~1),
        };
    }

    private static string Describe(MediaFrameFormat? format)
    {
        if (format?.VideoFormat is not { } video) return "unknown format";
        var rate = format.FrameRate is { Denominator: > 0 } r ? $" at {(double)r.Numerator / r.Denominator:0.#} fps" : "";
        return $"{video.Width}×{video.Height} {format.Subtype}{rate}";
    }

    private async Task ReleaseAsync()
    {
        MediaCapture? capture;
        MediaFrameReader? reader;
        lock (_gate)
        {
            capture = _capture;
            reader = _reader;
            _capture = null;
            _reader = null;
        }
        if (reader is not null)
        {
            reader.FrameArrived -= OnFrameArrived;
            try { await reader.StopAsync(); }
            catch (Exception e) { Log.Debug(e, "Stopping the camera's frame reader failed"); }
            reader.Dispose();
        }
        if (capture is not null)
        {
            capture.Failed -= OnCaptureFailed;
            capture.Dispose();
            Log.Information("Camera released");
        }
    }

    /// <summary>The reader's thread: copy the newest frame unless the UI still has the last one to show.</summary>
    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        lock (_gate)
        {
            if (sender != _reader) return;
            using var frame = sender.TryAcquireLatestFrame();
            var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap is null) return;
            Interlocked.Increment(ref _framesArrived);
            if (Interlocked.CompareExchange(ref _copyPending, 1, 0) != 0) return; // the UI is busy: drop it
            var generation = _generation;
            try
            {
                using var converted = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8 && bitmap.BitmapAlphaMode == BitmapAlphaMode.Premultiplied
                    ? null : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                var source = converted ?? bitmap;
                int stride;
                using (var locked = source.LockBuffer(BitmapBufferAccessMode.Read))
                    stride = locked.GetPlaneDescription(0).Stride;
                var length = stride * source.PixelHeight;
                if (_pixels.Length != length) _pixels = new byte[length];
                source.CopyToBuffer(_pixels.AsBuffer());
                _frameWidth = source.PixelWidth;
                _frameHeight = source.PixelHeight;
                _frameStride = stride;
            }
            catch (Exception e)
            {
                Interlocked.Exchange(ref _copyPending, 0);
                Log.Debug(e, "A camera frame could not be read");
                return;
            }
            finally { bitmap.Dispose(); }
            _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => ShowFrame(generation)));
        }
    }

    /// <summary>The UI thread: put the copied frame on screen, then let the next one in.</summary>
    private void ShowFrame(int generation)
    {
        try
        {
            if (generation != _generation || State != CameraPreviewState.Running) return;
            // The reader's thread leaves the pixels alone until this copy lets it in again.
            if (_frameWidth <= 0 || _frameHeight <= 0) return;
            if (Frame is not { } frame || frame.PixelWidth != _frameWidth || frame.PixelHeight != _frameHeight)
                Frame = frame = new WriteableBitmap(_frameWidth, _frameHeight, 96, 96, PixelFormats.Pbgra32, null);
            frame.WritePixels(new Int32Rect(0, 0, _frameWidth, _frameHeight), _pixels, _frameStride, 0);
            FramesShown++;
            FrameShown?.Invoke();
        }
        finally { Interlocked.Exchange(ref _copyPending, 0); }
    }

    /// <summary>The camera went away or another app took it (upstream's runtime error and interruption).</summary>
    private void OnCaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs args)
    {
        Log.Warning("Camera failed: {Message} (0x{Code:X8})", args.Message, args.Code);
        _dispatcher.BeginInvoke(() => Fail(sender));
    }

    private async void Fail(MediaCapture failed)
    {
        lock (_gate) { if (failed != _capture) return; }
        ++_generation;
        await ReleaseAsync();
        DeviceId = null;
        SetState(CameraPreviewState.Unavailable);
    }

    private void SetState(CameraPreviewState state)
    {
        if (State == state) return;
        State = state;
        if (state != CameraPreviewState.Running) { Frame = null; FramesShown = 0; }
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        ++_generation;
        _ = ReleaseAsync();
        DeviceId = null;
        State = CameraPreviewState.Idle;
        Frame = null;
    }
}
