// SPDX-License-Identifier: GPL-3.0-or-later
// Windows-only: a plain text copy for the tools that put a result on the clipboard. Another app may hold the clipboard for a moment,
// so a busy clipboard is tried again a few times. These are ordinary copies: the clipboard history records them like any other.
using System.Runtime.InteropServices;
using System.Windows;

namespace Notchly.App.Tools;

public static class ClipboardWrite
{
    public static bool TrySetText(string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                return true;
            }
            catch (COMException) { Thread.Sleep(20); }
        }
        return false;
    }
}
