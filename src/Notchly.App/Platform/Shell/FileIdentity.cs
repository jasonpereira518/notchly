// SPDX-License-Identifier: GPL-3.0-or-later
// Windows' counterpart of upstream's lstat dev:ino: the volume serial and file index of an entry, read
// with one short-lived handle together with its live size (the folder listing's size lags while a
// browser still has the file open). The handle asks only for attributes and shares read, write and
// delete, so a browser's rename is never blocked; links are read as themselves, never followed.
using Notchly.Core.Downloads;

namespace Notchly.App.Platform.Shell;

public static class FileIdentity
{
    public static FileFacts? Read(string path)
    {
        using var handle = Native.CreateFile(path,Native.FILE_READ_ATTRIBUTES, Native.FILE_SHARE_ALL, IntPtr.Zero,
            Native.OPEN_EXISTING, Native.FILE_FLAG_BACKUP_SEMANTICS | Native.FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (handle.IsInvalid || !Native.GetFileInformationByHandle(handle, out var info)) return null;
        var identity = $"{info.dwVolumeSerialNumber:x8}:{info.nFileIndexHigh:x8}{info.nFileIndexLow:x8}";
        var bytes = ((long)info.nFileSizeHigh << 32) | info.nFileSizeLow;
        return new FileFacts(identity, bytes, (info.dwFileAttributes & Native.FILE_ATTRIBUTE_DIRECTORY) != 0);
    }
}
