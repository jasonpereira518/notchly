// SPDX-License-Identifier: GPL-3.0-or-later
// Placeholder replaced by the editor in the next commit.
using Notchly.App.Platform.Capture;

namespace Notchly.App.Screenshots;

internal static class ScreenshotEditorWindow
{
    public static void Open(ScreenshotService service, CapturedImage image, double scale) =>
        service.Pin(image, scale, new Notchly.App.Platform.Native.RECT { Left = 100, Top = 100, Right = 100 + image.Width, Bottom = 100 + image.Height });
}
