// SPDX-License-Identifier: GPL-3.0-or-later
// The user's real Downloads folder, wherever it was moved (another drive, OneDrive).
using System.IO;
using System.Runtime.InteropServices;

namespace Notchly.App.Platform.Shell;

public static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string Downloads
    {
        get
        {
            if (Native.SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var pointer) == 0)
            {
                try
                {
                    if (Marshal.PtrToStringUni(pointer) is { Length: > 0 } path) return path;
                }
                finally { Marshal.FreeCoTaskMem(pointer); }
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }
}
