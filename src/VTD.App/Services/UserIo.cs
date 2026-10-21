using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace VTD.App.Services;

/// <summary>Clipboard, file dialogs and links: everything the UI does outside its window (faked in tests).</summary>
public interface IUserIo
{
    /// <summary>Copies text to the clipboard.</summary>
    /// <param name="text">Text.</param>
    void SetClipboard(string text);

    /// <summary>Asks where to save and writes the bytes through the dialog's stream (the UI never writes a path itself).</summary>
    /// <param name="suggestedName">File name.</param>
    /// <param name="filter">Dialog filter (<c>Report|*.html</c>).</param>
    /// <param name="content">Content.</param>
    /// <returns>True when saved.</returns>
    bool Save(string suggestedName, string filter, byte[] content);

    /// <summary>Asks for a file and reads it (read-only).</summary>
    /// <param name="filter">Dialog filter.</param>
    /// <returns>Name and bytes, or null when cancelled.</returns>
    (string Name, byte[] Content)? Open(string filter);

    /// <summary>Opens an official https link (KB allowlist only; never a helper-supplied link).</summary>
    /// <param name="url">URL.</param>
    /// <returns>True when opened.</returns>
    bool OpenLink(string url);
}

/// <summary>WPF implementation.</summary>
public sealed class WpfUserIo : IUserIo
{
    /// <inheritdoc />
    public void SetClipboard(string text) => Clipboard.SetText(text ?? string.Empty);

    /// <inheritdoc />
    public bool Save(string suggestedName, string filter, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var dialog = new SaveFileDialog { FileName = suggestedName, Filter = filter, AddExtension = true, OverwritePrompt = true };
        if (dialog.ShowDialog() != true)
        {
            return false;
        }

        using var stream = dialog.OpenFile();
        stream.SetLength(0);
        stream.Write(content);
        return true;
    }

    /// <inheritdoc />
    public (string Name, byte[] Content)? Open(string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        using var stream = File.OpenRead(dialog.FileName);
        if (stream.Length > 64L * 1024 * 1024)
        {
            throw new InvalidDataException("file.too_large");
        }

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return (Path.GetFileName(dialog.FileName), memory.ToArray());
    }

    /// <inheritdoc />
    public bool OpenLink(string url) => LinkOpener.Open(url);
}
