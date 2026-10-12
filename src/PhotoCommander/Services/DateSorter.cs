using System.Globalization;

namespace PhotoCommander.Services;

/// <summary>Раскладка фото по папкам с датой съёмки.</summary>
public static class DateSorter
{
    static string F(DateTime d, string fmt) => d.ToString(fmt, CultureInfo.InvariantCulture);
    static readonly char S = Path.DirectorySeparatorChar;

    public static readonly (string Title, Func<DateTime, string> Folder)[] Patterns =
    {
        ($"ГГГГ{S}ГГГГ-ММ", d => Path.Combine(F(d, "yyyy"), F(d, "yyyy-MM"))),
        ($"ГГГГ{S}ММ", d => Path.Combine(F(d, "yyyy"), F(d, "MM"))),
        ($"ГГГГ{S}ГГГГ-ММ-ДД", d => Path.Combine(F(d, "yyyy"), F(d, "yyyy-MM-dd"))),
        ("ГГГГ-ММ-ДД", d => F(d, "yyyy-MM-dd")),
    };

    public static int Sort(IReadOnlyList<string> files, string destRoot, int patternIndex, bool move,
                           IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var pattern = Patterns[Math.Clamp(patternIndex, 0, Patterns.Length - 1)];
        string stage = move ? "Перемещение по датам" : "Копирование по датам";
        int done = 0;
        foreach (var src in files)
        {
            ct.ThrowIfCancellationRequested();
            var taken = ImageMeta.Read(src).Taken ?? File.GetLastWriteTime(src);
            var dir = Path.Combine(destRoot, pattern.Folder(taken));
            Directory.CreateDirectory(dir);
            var dst = FileOps.UniquePath(Path.Combine(dir, Path.GetFileName(src)));
            if (move) File.Move(src, dst);
            else File.Copy(src, dst);
            done++;
            progress?.Report(new ScanProgress(done, files.Count, stage));
        }
        return done;
    }
}
