// SPDX-License-Identifier: GPL-3.0-or-later
// Windows-only: the code reading upstream's BarcodeDetector.swift asks of Vision (VNDetectBarcodesRequest), done by ZXing.Net on the
// pixels of the area: QR, Aztec, Data Matrix and PDF417 codes, several at once, each with where it sits. Nothing leaves the PC.
using ZXing;
using ZXing.Common;
using Notchly.App.Platform.Capture;
using Notchly.Core.Tools;
using Serilog;

namespace Notchly.App.Platform.Ocr;

public static class QrReader
{
    private static readonly BarcodeFormat[] Formats = [BarcodeFormat.QR_CODE, BarcodeFormat.AZTEC, BarcodeFormat.DATA_MATRIX, BarcodeFormat.PDF_417];

    /// <summary>The codes in the area with where each sits (fractions of the area, 0 = left / top). Empty when there are none.</summary>
    public static IReadOnlyList<DecodedBarcode> Read(CapturedImage area)
    {
        try
        {
            var packed = area.Stride == area.Width * 4 && area.Offset == 0 ? area : area.Crop(new Notchly.Core.Notch.RectD(0, 0, area.Width, area.Height));
            var source = new RGBLuminanceSource(packed.Pixels, packed.Width, packed.Height, RGBLuminanceSource.BitmapFormat.BGRA32);
            var reader = new BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = new DecodingOptions { TryHarder = true, TryInverted = true, PossibleFormats = Formats },
            };
            var results = reader.DecodeMultiple(source);
            if (results is null) return [];
            return results.Where(r => !string.IsNullOrEmpty(r.Text)).Select(r =>
            {
                var points = r.ResultPoints ?? [];
                var x = points.Length > 0 ? points.Min(p => p.X) : 0;
                var y = points.Length > 0 ? points.Min(p => p.Y) : 0;
                return new DecodedBarcode(r.Text, x / Math.Max(1, packed.Width), y / Math.Max(1, packed.Height));
            }).ToList();
        }
        catch (Exception e)
        {
            Log.Warning(e, "Code reading failed");
            return [];
        }
    }
}
