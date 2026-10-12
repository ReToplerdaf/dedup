using System.Runtime.InteropServices;
using System.Text;

namespace PhotoCommander.Services;

/// <summary>Элемент списка дисков: путь и короткая подпись («C:\», «/», «~», «USB»).</summary>
public sealed record DriveRoot(string Path, string Label)
{
    public override string ToString() => Label;
}

public static class FileOps
{
    /// <summary>Сравнение путей: в Windows без учёта регистра, в Linux — с учётом.</summary>
    public static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Если путь занят — добавляет _1, _2 …</summary>
    public static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path) ?? "";
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            var p = Path.Combine(dir, $"{name}_{i}{ext}");
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
    }

    public static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(src))
            CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    public static void MoveDirectory(string src, string dst)
    {
        try
        {
            Directory.Move(src, dst);
        }
        catch (IOException) when (Directory.Exists(src) && !Directory.Exists(dst))
        {
            // другой диск/раздел — копируем и удаляем
            CopyDirectory(src, dst);
            Directory.Delete(src, true);
        }
    }

    /// <summary>Полный путь с одним разделителем в конце: «C:\Фото\», «/home/», «/».</summary>
    static string Normalize(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        // у корня («C:\», «/») разделитель в конце остаётся
        return Path.EndsInDirectorySeparator(full) ? full : full + Path.DirectorySeparatorChar;
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), PathComparison);

    /// <summary>true, если child совпадает с parent или лежит внутри него.</summary>
    public static bool IsSameOrInside(string child, string parent) =>
        Normalize(child).StartsWith(Normalize(parent), PathComparison);

    /// <summary>Диски (Windows) или корень, домашняя папка и точки монтирования (Linux/macOS).</summary>
    public static List<DriveRoot> ListRoots()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return DriveInfo.GetDrives().Where(x => x.IsReady).Select(x => new DriveRoot(x.Name, x.Name)).ToList();

            var roots = new List<DriveRoot> { new("/", "/") };
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home) && Directory.Exists(home)) roots.Add(new DriveRoot(home, "~"));
            string[] places = { "/media/", "/mnt/", "/run/media/", "/Volumes/" };
            roots.AddRange(DriveInfo.GetDrives()
                .Where(d => places.Any(p => d.Name.StartsWith(p, StringComparison.Ordinal)))
                .Where(d => { try { return d.IsReady; } catch { return false; } })
                .Select(d => new DriveRoot(d.Name, System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(d.Name)))));
            return roots.DistinctBy(r => r.Path).ToList();
        }
        catch
        {
            return new List<DriveRoot>();
        }
    }

    // ---------- Корзина ----------

    /// <summary>Отправляет файлы/папки в Корзину (их можно восстановить). false — не всё удалось.</summary>
    public static bool ToRecycleBin(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        if (list.Count == 0) return true;
        if (OperatingSystem.IsWindows()) return WindowsRecycle(list);
        bool ok = true;
        foreach (var p in list)
        {
            try { UnixTrash(p); }
            catch { ok = false; }
        }
        return ok;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    const uint FO_DELETE = 3;
    const ushort FOF_SILENT = 0x0004;
    const ushort FOF_NOCONFIRMATION = 0x0010;
    const ushort FOF_ALLOWUNDO = 0x0040;
    const ushort FOF_NOERRORUI = 0x0400;

    static bool WindowsRecycle(List<string> list)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = string.Join("\0", list) + "\0\0",
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI),
        };
        return SHFileOperation(ref op) == 0 && op.fAnyOperationsAborted == 0;
    }

    /// <summary>Корзина по спецификации freedesktop.org (Linux) или ~/.Trash (macOS).</summary>
    static void UnixTrash(string path)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        bool isDir = Directory.Exists(path);
        if (!isDir && !File.Exists(path)) throw new FileNotFoundException(path);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsMacOS())
        {
            MoveAny(path, UniquePath(Path.Combine(home, ".Trash", Path.GetFileName(path))), isDir);
            return;
        }

        string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x
            ? x : Path.Combine(home, ".local", "share");
        string trash = Path.Combine(dataHome, "Trash");
        string filesDir = Path.Combine(trash, "files"), infoDir = Path.Combine(trash, "info");
        Directory.CreateDirectory(filesDir);
        Directory.CreateDirectory(infoDir);

        // имя в корзине должно быть свободно и в files, и в info
        string name = Path.GetFileName(path);
        string target = Path.Combine(filesDir, name);
        for (int i = 1; File.Exists(target) || Directory.Exists(target) || File.Exists(Path.Combine(infoDir, Path.GetFileName(target) + ".trashinfo")); i++)
            target = Path.Combine(filesDir, $"{Path.GetFileNameWithoutExtension(name)}_{i}{Path.GetExtension(name)}");

        string infoPath = Path.Combine(infoDir, Path.GetFileName(target) + ".trashinfo");
        string encoded = string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
        File.WriteAllText(infoPath,
            $"[Trash Info]\nPath={encoded}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n", new UTF8Encoding(false));
        try
        {
            MoveAny(path, target, isDir);
        }
        catch
        {
            File.Delete(infoPath);
            throw;
        }
    }

    static void MoveAny(string src, string dst, bool isDir)
    {
        if (isDir) MoveDirectory(src, dst);
        else File.Move(src, dst);
    }
}
