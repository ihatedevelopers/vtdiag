// Audited exception (banned/rs0030-allowlist.txt): the read side may not open files with a FileMode. The game and the
// Riot Client keep their logs open for writing, so File.OpenRead (FileShare.Read) fails with a sharing violation.
// This is the only place that opens a file with FileMode.Open + FileAccess.Read + FileShare.ReadWrite|Delete.
// It never requests write access and never creates, truncates or appends.
#pragma warning disable RS0030

namespace VTD.Platform.IO;

/// <summary>Opens existing files for reading while other processes keep them open for writing.</summary>
public static class SharedReadFile
{
    /// <summary>Opens <paramref name="path"/> read-only with full sharing. Throws like <see cref="File.OpenRead"/>.</summary>
    /// <param name="path">Absolute path of an existing file.</param>
    public static FileStream Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
    }
}
