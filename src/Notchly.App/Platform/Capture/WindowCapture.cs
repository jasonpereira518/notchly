// SPDX-License-Identifier: GPL-3.0-or-later
// Windows-only: what upstream Services/Notch/NotchWatchService.swift reads through the window server and
// ScreenCaptureKit. One window is read with Windows.Graphics.Capture, which sees it wherever it is and even
// covered; a part of the display with nothing but the desktop under it is a plain GDI copy, which leaves out
// layered windows such as the island and the area picker.
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Security.Authorization.AppCapabilityAccess;
using Serilog;
using Notchly.Core.Notch;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Platform.Capture;

/// <summary>Top-down BGRA pixels, kept in memory only.</summary>
public sealed record CapturedImage(byte[] Pixels, int Width, int Height, int Stride, int Offset = 0)
{
    /// <summary>The part <paramref name="rect"/> covers, as an image of its own (whole pixels, inside the image).</summary>
    public CapturedImage Crop(RectD rect)
    {
        var x = (int)rect.X;
        var y = (int)rect.Y;
        var width = Math.Min((int)rect.Width, Width - x);
        var height = Math.Min((int)rect.Height, Height - y);
        var pixels = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
            Buffer.BlockCopy(Pixels, Offset + (y + row) * Stride + x * 4, pixels, row * width * 4, width * 4);
        return new CapturedImage(pixels, width, height, width * 4);
    }

    /// <summary>The image at <paramref name="scale"/>, bilinear: text recognition reads small text better a little larger.</summary>
    public CapturedImage Scaled(double scale)
    {
        if (Math.Abs(scale - 1) < 0.01) return this;
        var width = Math.Max(1, (int)Math.Round(Width * scale));
        var height = Math.Max(1, (int)Math.Round(Height * scale));
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var sy = Math.Clamp((y + 0.5) / scale - 0.5, 0, Height - 1);
            var y0 = (int)sy;
            var y1 = Math.Min(y0 + 1, Height - 1);
            var fy = sy - y0;
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Clamp((x + 0.5) / scale - 0.5, 0, Width - 1);
                var x0 = (int)sx;
                var x1 = Math.Min(x0 + 1, Width - 1);
                var fx = sx - x0;
                for (var c = 0; c < 4; c++)
                {
                    double P(int px, int py) => Pixels[Offset + py * Stride + px * 4 + c];
                    var top = P(x0, y0) + (P(x1, y0) - P(x0, y0)) * fx;
                    var bottom = P(x0, y1) + (P(x1, y1) - P(x0, y1)) * fx;
                    pixels[(y * width + x) * 4 + c] = (byte)Math.Round(top + (bottom - top) * fy);
                }
            }
        }
        return new CapturedImage(pixels, width, height, width * 4);
    }
}

/// <summary>
/// One window, read with Windows.Graphics.Capture for as long as it is watched. The session keeps only the newest
/// frame; a read copies it once, and with no new frame since the last read the window looks as it did.
/// </summary>
public sealed class WindowCapture : IDisposable
{
    private static readonly Guid InteropIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid ItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid DxgiDeviceIid = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private static bool? _borderless;

    private readonly object _gate = new();
    private readonly IDirect3DDevice _device;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private Windows.Graphics.SizeInt32 _poolSize;
    private Direct3D11CaptureFrame? _newest;
    private CapturedImage? _last;
    private bool _closed, _disposed;

    private WindowCapture(IDirect3DDevice device, GraphicsCaptureItem item, bool borderless)
    {
        _device = device;
        _item = item;
        _poolSize = item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _pool.FrameArrived += OnFrameArrived;
        item.Closed += (_, _) => _closed = true;
        _session = _pool.CreateCaptureSession(item);
        try { _session.IsCursorCaptureEnabled = false; } catch (Exception e) { Log.Debug(e, "Watch: cursor capture switch unavailable"); }
        if (borderless)
            try { _session.IsBorderRequired = false; } catch (Exception e) { Log.Debug(e, "Watch: the capture border stays"); }
        _session.StartCapture();
    }

    public static bool IsSupported
    {
        get
        {
            try { return GraphicsCaptureSession.IsSupported(); }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Asks Windows once whether the yellow capture border may be left out; it stays where it may not.</summary>
    public static async Task<bool> AllowBorderlessAsync()
    {
        if (_borderless is { } known) return known;
        try
        {
            var status = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            _borderless = status == AppCapabilityAccessStatus.Allowed;
        }
        catch (Exception e)
        {
            Log.Debug(e, "Watch: borderless capture unavailable");
            _borderless = false;
        }
        return _borderless.Value;
    }

    /// <summary>Starts reading <paramref name="window"/>; null when Windows cannot capture it.</summary>
    public static WindowCapture? Start(IntPtr window, bool borderless)
    {
        try
        {
            var device = CreateDevice();
            var item = CreateItem(window);
            return item is null || device is null ? null : new WindowCapture(device, item, borderless);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Watch: could not capture the window");
            return null;
        }
    }

    /// <summary>The capture item went away: its window closed.</summary>
    public bool Closed => _closed;

    /// <summary>
    /// The window as it looks now, or as it looked at the last read when nothing new arrived; null before its first
    /// frame. Runs off the UI thread.
    /// </summary>
    public async Task<CapturedImage?> ReadAsync()
    {
        Direct3D11CaptureFrame? frame;
        lock (_gate)
        {
            frame = _newest;
            _newest = null;
        }
        if (frame is null) return _last;
        try
        {
            var content = frame.ContentSize;
            using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied);
            var image = Pixels(bitmap);
            if (image is null) return _last;
            // The surface is the pool's size; the window's pixels are its top-left part.
            var width = Math.Min(image.Width, content.Width);
            var height = Math.Min(image.Height, content.Height);
            _last = width == image.Width && height == image.Height ? image : image with { Width = width, Height = height };
            return _last;
        }
        catch (Exception e)
        {
            Log.Debug(e, "Watch: a frame could not be read");
            return _last;
        }
        finally { frame.Dispose(); }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool pool, object _)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var frame = pool.TryGetNextFrame();
            if (frame is null) return;
            _newest?.Dispose();
            _newest = frame;
            // A resized window needs buffers of its new size, or its frames are cut short.
            if (frame.ContentSize.Width != _poolSize.Width || frame.ContentSize.Height != _poolSize.Height)
            {
                _poolSize = frame.ContentSize;
                pool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
            }
        }
    }

    private static CapturedImage? Pixels(SoftwareBitmap bitmap)
    {
        if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8) return null;
        int stride, offset;
        using (var locked = bitmap.LockBuffer(BitmapBufferAccessMode.Read))
        {
            var plane = locked.GetPlaneDescription(0);
            stride = plane.Stride;
            offset = plane.StartIndex;
        }
        var capacity = (uint)(offset + stride * bitmap.PixelHeight);
        var buffer = new Windows.Storage.Streams.Buffer(capacity);
        bitmap.CopyToBuffer(buffer);
        var bytes = buffer.ToArray();
        // A packed copy has no row padding.
        if (bytes.Length < offset + stride * (bitmap.PixelHeight - 1) + bitmap.PixelWidth * 4)
        {
            stride = bitmap.PixelWidth * 4;
            offset = 0;
        }
        return new CapturedImage(bytes, bitmap.PixelWidth, bitmap.PixelHeight, stride, offset);
    }

    private static IDirect3DDevice? CreateDevice()
    {
        var result = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                                       IntPtr.Zero, 0, D3D11_SDK_VERSION, out var d3d, out _, out var context);
        if (result < 0) return null;
        try
        {
            var iid = DxgiDeviceIid;
            if (Marshal.QueryInterface(d3d, in iid, out var dxgi) < 0) return null;
            try
            {
                if (CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable) < 0) return null;
                try { return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
                finally { Marshal.Release(inspectable); }
            }
            finally { Marshal.Release(dxgi); }
        }
        finally
        {
            if (context != IntPtr.Zero) Marshal.Release(context);
            Marshal.Release(d3d);
        }
    }

    /// <summary>IGraphicsCaptureItemInterop.CreateForWindow, called through its vtable.</summary>
    private static unsafe GraphicsCaptureItem? CreateItem(IntPtr window)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        if (WindowsCreateString(name, name.Length, out var className) < 0) return null;
        IntPtr factory;
        try
        {
            if (RoGetActivationFactory(className, InteropIid, out factory) < 0) return null;
        }
        finally { WindowsDeleteString(className); }
        try
        {
            var vtable = *(IntPtr**)factory;
            var createForWindow = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtable[3];
            var iid = ItemIid;
            IntPtr item;
            if (createForWindow(factory, window, &iid, &item) < 0 || item == IntPtr.Zero) return null;
            try { return GraphicsCaptureItem.FromAbi(item); }
            finally { Marshal.Release(item); }
        }
        finally { Marshal.Release(factory); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _newest?.Dispose();
            _newest = null;
        }
        _pool.FrameArrived -= OnFrameArrived;
        try { _session.Dispose(); } catch (Exception) { }
        try { _pool.Dispose(); } catch (Exception) { }
        _last = null;
    }
}

/// <summary>A fixed part of the display, read as a plain GDI copy (layered windows such as the island stay out).</summary>
public static class ScreenRegion
{
    public static CapturedImage? Read(RECT area)
    {
        var width = area.Width;
        var height = area.Height;
        if (width < 4 || height < 4) return null;
        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        var memory = CreateCompatibleDC(screen);
        var info = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, // top-down
            biPlanes = 1, biBitCount = 32,
        };
        var bitmap = CreateDIBSection(screen, ref info, 0, out var bits, IntPtr.Zero, 0);
        try
        {
            if (memory == IntPtr.Zero || bitmap == IntPtr.Zero) return null;
            var previous = SelectObject(memory, bitmap);
            var copied = BitBlt(memory, 0, 0, width, height, screen, area.Left, area.Top, SRCCOPY);
            SelectObject(memory, previous);
            if (!copied) return null;
            var pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            // GDI leaves alpha at zero.
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return new CapturedImage(pixels, width, height, width * 4);
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
