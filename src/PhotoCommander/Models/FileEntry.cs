namespace PhotoCommander.Models;

/// <summary>Строка в файловой панели.</summary>
public sealed class FileEntry
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsParent { get; init; }
    public bool IsImage { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }

    public string DisplayName => IsParent ? ".." : Name;
    public string SizeText => IsParent ? "" : IsDirectory ? "<ПАПКА>" : FormatSize(Size);
    public string DateText => IsParent ? "" : Modified.ToString("dd.MM.yyyy HH:mm");

    /// <summary>По этому тексту работает быстрый поиск по первым буквам.</summary>
    public override string ToString() => DisplayName;

    public static string FormatSize(long b) => b switch
    {
        < 1024 => $"{b} Б",
        < 1024 * 1024 => $"{b / 1024.0:0.#} КБ",
        < 1024L * 1024 * 1024 => $"{b / 1024.0 / 1024:0.#} МБ",
        _ => $"{b / 1024.0 / 1024 / 1024:0.##} ГБ",
    };
}
