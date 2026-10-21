using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Text;
using VTD.Platform.Sources;

namespace VTD.Platform.IO;

/// <summary>Live file reads. Metadata via FileInfo, text only through a <see cref="LineFilter"/>, headers capped.</summary>
public sealed class FileSource : IFileSource
{
    /// <inheritdoc />
    public FileStat Stat(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            var file = new FileInfo(path);
            if (file.Exists)
            {
                return new FileStat
                {
                    Status = ReadStatus.Ok,
                    Size = file.Length,
                    ModifiedUtc = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                    CreatedUtc = new DateTimeOffset(file.CreationTimeUtc, TimeSpan.Zero),
                };
            }

            var dir = new DirectoryInfo(path);
            if (dir.Exists)
            {
                return new FileStat
                {
                    Status = ReadStatus.Ok,
                    IsDirectory = true,
                    ModifiedUtc = new DateTimeOffset(dir.LastWriteTimeUtc, TimeSpan.Zero),
                    CreatedUtc = new DateTimeOffset(dir.CreationTimeUtc, TimeSpan.Zero),
                };
            }

            return new FileStat { Status = ReadStatus.NotFound };
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException)
        {
            return new FileStat { Status = ReadStatus.AccessDenied };
        }
        catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException)
        {
            return new FileStat { Status = ReadStatus.Failed };
        }
    }

    /// <inheritdoc />
    public DirListing List(string path, string pattern, bool recursive, int max)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(pattern);
        try
        {
            var dir = new DirectoryInfo(path);
            if (!dir.Exists)
            {
                return new DirListing { Status = ReadStatus.NotFound };
            }

            using (var probe = dir.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = false, RecurseSubdirectories = false }).GetEnumerator())
            {
                _ = probe.MoveNext();
            }

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                MatchCasing = MatchCasing.CaseInsensitive,
                ReturnSpecialDirectories = false,
            };
            var entries = new List<DirEntry>();
            var truncated = false;
            foreach (var info in dir.EnumerateFileSystemInfos(pattern, options))
            {
                if (entries.Count >= max)
                {
                    truncated = true;
                    break;
                }

                entries.Add(new DirEntry
                {
                    Name = Path.GetRelativePath(dir.FullName, info.FullName),
                    IsDirectory = info is DirectoryInfo,
                    Size = info is FileInfo f ? f.Length : 0,
                    ModifiedUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                    CreatedUtc = new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero),
                });
            }

            return new DirListing { Status = ReadStatus.Ok, Entries = entries, Truncated = truncated };
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException)
        {
            return new DirListing { Status = ReadStatus.AccessDenied };
        }
        catch (Exception e) when (e is IOException or ArgumentException)
        {
            return new DirListing { Status = ReadStatus.Failed };
        }
    }

    /// <inheritdoc />
    public LinesResult ReadLines(string path, LineFilter filter, long maxBytes, int maxLines)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(filter);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new LinesResult { Status = ReadStatus.NotFound };
            }

            using var stream = SharedReadFile.Open(path);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16);
            var kept = new List<string>();
            var read = 0;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                read++;
                if (kept.Count < maxLines)
                {
                    var mapped = filter.Map(line);
                    if (mapped is not null)
                    {
                        kept.Add(mapped);
                    }
                }

                if (stream.Position >= maxBytes)
                {
                    return Result(info, kept, read, truncated: stream.Position < stream.Length);
                }
            }

            return Result(info, kept, read, truncated: false);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException)
        {
            return new LinesResult { Status = ReadStatus.AccessDenied };
        }
        catch (Exception e) when (e is IOException or ArgumentException or DecoderFallbackException)
        {
            return new LinesResult { Status = ReadStatus.Failed };
        }
    }

    /// <inheritdoc />
    public HeaderResult ReadHeader(string path, int count)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[Math.Clamp(count, 0, 4096)];
            var n = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return new HeaderResult { Status = ReadStatus.Ok, Hex = Convert.ToHexStringLower(buffer.AsSpan(0, n)) };
        }
        catch (FileNotFoundException)
        {
            return new HeaderResult { Status = ReadStatus.NotFound };
        }
        catch (DirectoryNotFoundException)
        {
            return new HeaderResult { Status = ReadStatus.NotFound };
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException)
        {
            return new HeaderResult { Status = ReadStatus.AccessDenied };
        }
        catch (Exception e) when (e is IOException or ArgumentException)
        {
            return new HeaderResult { Status = ReadStatus.Failed };
        }
    }

    /// <inheritdoc />
    public FileVersionResult GetVersion(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            if (!File.Exists(path))
            {
                return new FileVersionResult { Status = ReadStatus.NotFound };
            }

            var v = FileVersionInfo.GetVersionInfo(path);
            var numeric = v.FileMajorPart == 0 && v.FileMinorPart == 0 && v.FileBuildPart == 0 && v.FilePrivatePart == 0
                ? null
                : string.Create(CultureInfo.InvariantCulture, $"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}.{v.FilePrivatePart}");
            return new FileVersionResult { Status = ReadStatus.Ok, FileVersion = numeric, ProductVersion = v.ProductVersion?.Trim() };
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException)
        {
            return new FileVersionResult { Status = ReadStatus.AccessDenied };
        }
        catch (Exception e) when (e is IOException or ArgumentException)
        {
            return new FileVersionResult { Status = ReadStatus.Failed };
        }
    }

    /// <inheritdoc />
    public ShortcutResult ReadShortcut(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[Math.Min(stream.Length, 1 << 16)];
            var n = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return new ShortcutResult { Status = ReadStatus.Ok, TargetPath = ShellLinkParser.TargetPath(buffer.AsSpan(0, n)) };
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new ShortcutResult { Status = ReadStatus.NotFound };
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException)
        {
            return new ShortcutResult { Status = ReadStatus.AccessDenied };
        }
        catch (Exception e) when (e is IOException or ArgumentException)
        {
            return new ShortcutResult { Status = ReadStatus.Failed };
        }
    }

    private static LinesResult Result(FileInfo info, List<string> kept, int read, bool truncated) => new()
    {
        Status = ReadStatus.Ok,
        Lines = kept,
        LinesRead = read,
        Truncated = truncated,
        Size = info.Length,
        ModifiedUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
    };
}

/// <summary>Minimal MS-SHLLINK parser: the LinkInfo local base path (+ common suffix), else null. No COM, no shell.</summary>
public static class ShellLinkParser
{
    private const int HeaderSize = 0x4C;

    /// <summary>Extracts the target path from .lnk bytes.</summary>
    /// <param name="data">File bytes.</param>
    public static string? TargetPath(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != HeaderSize)
        {
            return null;
        }

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data[0x14..]);
        var offset = HeaderSize;
        if ((flags & 0x1) != 0)
        {
            if (data.Length < offset + 2)
            {
                return null;
            }

            offset += 2 + BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        }

        if ((flags & 0x2) == 0 || data.Length < offset + 0x1C)
        {
            return null;
        }

        var info = data[offset..];
        var infoSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(info);
        var infoHeader = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[4..]);
        var infoFlags = BinaryPrimitives.ReadUInt32LittleEndian(info[8..]);
        if ((infoFlags & 0x1) == 0 || infoSize > info.Length)
        {
            return null;
        }

        info = info[..infoSize];
        var localOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[16..]);
        var suffixOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[24..]);
        string? local;
        string? suffix;
        if (infoHeader >= 0x24 && info.Length >= 0x24)
        {
            var localU = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[28..]);
            var suffixU = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[32..]);
            local = ReadUnicodeZ(info, localU);
            suffix = ReadUnicodeZ(info, suffixU);
        }
        else
        {
            local = ReadAnsiZ(info, localOffset);
            suffix = ReadAnsiZ(info, suffixOffset);
        }

        if (string.IsNullOrEmpty(local))
        {
            return null;
        }

        return string.IsNullOrEmpty(suffix) ? local : Path.Combine(local, suffix);
    }

    private static string? ReadAnsiZ(ReadOnlySpan<byte> s, int at)
    {
        if (at <= 0 || at >= s.Length)
        {
            return null;
        }

        var end = s[at..].IndexOf((byte)0);
        return end < 0 ? null : Encoding.Latin1.GetString(s.Slice(at, end));
    }

    private static string? ReadUnicodeZ(ReadOnlySpan<byte> s, int at)
    {
        if (at <= 0 || at >= s.Length - 1)
        {
            return null;
        }

        var sb = new StringBuilder();
        for (var i = at; i + 1 < s.Length; i += 2)
        {
            var c = (char)BinaryPrimitives.ReadUInt16LittleEndian(s[i..]);
            if (c == '\0')
            {
                return sb.ToString();
            }

            sb.Append(c);
        }

        return null;
    }
}
