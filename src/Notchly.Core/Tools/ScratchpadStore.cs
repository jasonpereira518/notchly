// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/QuickTools/ScratchpadStore.swift: the scratchpad document on disk. Upstream's UserDefaults
// preference migration is dropped (there is no such preference on Windows); the legacy Scratchpad.txt import is kept.
using System.Text;

namespace Notchly.Core.Tools;

/// <summary>
/// Holds the folder the pad lives in. A failed read must never authorize a later autosave or migration cleanup: <see cref="Load"/>
/// throws and leaves <see cref="CanSave"/> false until a later load succeeds.
/// </summary>
public sealed class ScratchpadStore
{
    public const string FileName = "Scratchpad.json";
    public const string LegacyFileName = "Scratchpad.txt";

    public string? DirectoryPath { get; }

    /// <summary>The document last read or written successfully; saving an equal one is skipped.</summary>
    public ScratchpadDocument? LastSavedDocument { get; private set; }

    /// <summary>False until the first successful read, and again after any failed one.</summary>
    public bool CanSave { get; private set; }

    public ScratchpadStore(string? directoryPath)
    {
        DirectoryPath = directoryPath;
    }

    /// <summary>
    /// Reads the stored document (or the legacy text file, or starts empty), sanitises it, clears the pads whose text expired and
    /// saves the result. Throws when the storage is unavailable, unreadable or holds invalid data, and then nothing is changed on disk.
    /// </summary>
    public ScratchpadDocument Load(string defaultName, ScratchpadRetention retention, DateTimeOffset now)
    {
        CanSave = false;
        LastSavedDocument = null;
        if (DirectoryPath == null) throw new IOException("The scratchpad folder is unavailable.");
        string path = Path.Combine(DirectoryPath, FileName);
        string legacyPath = Path.Combine(DirectoryPath, LegacyFileName);
        byte[]? stored = ReadIfPresent(path);
        ScratchpadDocument decoded;
        bool migratedLegacyFile = false;

        if (stored != null)
        {
            decoded = ScratchpadDocument.Decode(stored);
            LastSavedDocument = decoded;
        }
        else if (ReadIfPresent(legacyPath) is { } legacy)
        {
            string text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(legacy);
            decoded = ScratchpadDocument.Initial(defaultName, text: text, modifiedAt: new DateTimeOffset(File.GetLastWriteTimeUtc(legacyPath), TimeSpan.Zero));
            migratedLegacyFile = true;
        }
        else
        {
            decoded = ScratchpadDocument.Initial(defaultName);
        }

        var loaded = decoded.Sanitized(defaultName).ApplyingRetention(retention, now);
        CanSave = true;
        if (Save(loaded) && migratedLegacyFile)
        {
            try { File.Delete(legacyPath); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return loaded;
    }

    /// <summary>
    /// Writes the document (temp file in the same folder, then replace), reads it back and compares. False when nothing may be saved
    /// yet, or the write or the verification failed; true when saved or already identical to what is on disk.
    /// </summary>
    public bool Save(ScratchpadDocument document)
    {
        if (!CanSave) return false;
        if (document.Equals(LastSavedDocument)) return true;
        if (DirectoryPath == null) return false;
        byte[] data = document.Encoded();
        string path = Path.Combine(DirectoryPath, FileName);
        string temp = Path.Combine(DirectoryPath, $"{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllBytes(temp, data);
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(data)) return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
        }
        LastSavedDocument = document;
        return true;
    }

    /// <summary>The bytes of the file, or null when it does not exist; any other failure throws.</summary>
    private static byte[]? ReadIfPresent(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}
