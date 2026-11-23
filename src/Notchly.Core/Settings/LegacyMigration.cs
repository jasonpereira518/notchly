// SPDX-License-Identifier: GPL-3.0-or-later
// Windows-only: carries data over from the app's earlier working name ("Islet") after the rename
// to Notchly. Copies, never moves, so going back to an older build still finds its own files.
namespace Notchly.Core.Settings;

public static class LegacyMigration
{
    /// <summary>
    /// Copies the folder `legacy` to `current` when only the legacy one exists. Returns whether
    /// anything was copied. Once `current` exists it is never touched again.
    /// </summary>
    public static bool CopyFolderOnce(string legacy, string current)
    {
        if (Directory.Exists(current) || !Directory.Exists(legacy)) return false;
        // Copy into a sibling first, so an interrupted copy never leaves a half-filled folder
        // that would stop the next launch from trying again.
        var staging = current + ".migrating";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        CopyTree(legacy, staging);
        Directory.Move(staging, current);
        return true;
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(from))
            CopyTree(directory, Path.Combine(to, Path.GetFileName(directory)));
    }
}
