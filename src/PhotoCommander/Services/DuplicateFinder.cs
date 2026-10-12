using System.Numerics;
using System.Security.Cryptography;
using OpenCvSharp;

namespace PhotoCommander.Services;

public sealed class DupOptions
{
    /// <summary>Обычный порог: макс. число отличающихся бит pHash (из 64).</summary>
    public int Threshold { get; set; } = 6;
    /// <summary>Фото, снятые с разницей не больше N секунд, сравниваются мягче. 0 — выключено.</summary>
    public double TimeWindowSec { get; set; } = 10;
    /// <summary>Мягкий порог для близких по времени фото.</summary>
    public int TimeThreshold { get; set; } = 14;
    public bool Recursive { get; set; } = true;
    /// <summary>Если нет EXIF — брать время изменения файла.</summary>
    public bool UseFileTimeIfNoExif { get; set; }
}

public sealed class PhotoInfo
{
    public required string FilePath { get; init; }
    public string Md5 { get; set; } = "";
    public ulong Hash { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public long Bytes { get; set; }
    public DateTime? Taken { get; set; }
    public long Pixels => (long)Width * Height;
}

public sealed class DupMember
{
    public required PhotoInfo Photo { get; init; }
    public bool IsBest { get; init; }
    public int Distance { get; init; }
    public string Reason { get; init; } = "";
}

public sealed class DupGroup
{
    public int Id { get; init; }
    public List<DupMember> Members { get; } = new();
}

public sealed record ScanProgress(int Done, int Total, string Stage);

/// <summary>
/// Поиск одинаковых и похожих фото:
/// 1) точные копии по MD5;
/// 2) похожие по перцептивному хэшу (DCT 8×8 из OpenCV);
/// 3) снятые почти одновременно — с более мягким порогом.
/// </summary>
public static class DuplicateFinder
{
    public static List<string> ListImages(string root, bool recursive)
    {
        var opt = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true };
        string dupMarker = Path.DirectorySeparatorChar + "_duplicates" + Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(root, "*", opt)
            .Where(ImageMeta.IsImage)
            .Where(p => !p.Contains(dupMarker, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static List<DupGroup> Find(string root, DupOptions o, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new ScanProgress(0, 0, "Поиск файлов…"));
        var files = ListImages(root, o.Recursive);
        var infos = new PhotoInfo?[files.Count];
        int done = 0;

        var po = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount };
        Parallel.For(0, files.Count, po, i =>
        {
            infos[i] = Analyze(files[i], o);
            int d = Interlocked.Increment(ref done);
            if (d % 10 == 0 || d == files.Count)
                progress?.Report(new ScanProgress(d, files.Count, "Анализ изображений"));
        });

        ct.ThrowIfCancellationRequested();
        progress?.Report(new ScanProgress(0, 0, "Сравнение…"));
        var items = infos.Where(x => x != null).Select(x => x!).ToList();
        return Group(items, o, ct);
    }

    static PhotoInfo? Analyze(string path, DupOptions o)
    {
        try
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length == 0) return null;

            // уменьшенное декодирование в оттенках серого — быстро и достаточно для хэша
            using var gray = Cv2.ImDecode(data, ImreadModes.ReducedGrayscale2);
            if (gray.Empty()) return null;

            var info = new PhotoInfo
            {
                FilePath = path,
                Bytes = data.LongLength,
                Md5 = Convert.ToHexString(MD5.HashData(data)),
                Hash = PHash(gray),
            };

            var meta = ImageMeta.Read(path);
            info.Width = meta.Width > 0 ? meta.Width : gray.Width * 2;
            info.Height = meta.Height > 0 ? meta.Height : gray.Height * 2;
            info.Taken = meta.Taken ?? (o.UseFileTimeIfNoExif ? (DateTime?)File.GetLastWriteTime(path) : null);
            return info;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Перцептивный хэш: 32×32 → DCT → 8×8 низких частот → сравнение с медианой.</summary>
    static ulong PHash(Mat gray)
    {
        using var small = new Mat();
        Cv2.Resize(gray, small, new Size(32, 32), 0, 0, InterpolationFlags.Area);
        using var f = new Mat();
        small.ConvertTo(f, MatType.CV_32FC1);
        using var dct = new Mat();
        Cv2.Dct(f, dct);

        var vals = new float[64];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                vals[y * 8 + x] = dct.At<float>(y, x);

        var sorted = vals.Skip(1).OrderBy(v => v).ToArray(); // без DC-компоненты, 63 значения
        float median = sorted[31];

        ulong h = 0;
        for (int i = 0; i < 64; i++)
            if (vals[i] > median) h |= 1UL << (63 - i);
        return h;
    }

    static List<DupGroup> Group(List<PhotoInfo> items, DupOptions o, CancellationToken ct)
    {
        int n = items.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        var reason = new string?[n];

        int Root(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        void Union(int a, int b)
        {
            a = Root(a); b = Root(b);
            if (a != b) parent[b] = a;
        }
        void Mark(int i, string r) => reason[i] ??= r;

        // 1) точные копии
        var byMd5 = new Dictionary<string, int>();
        for (int i = 0; i < n; i++)
        {
            if (byMd5.TryGetValue(items[i].Md5, out int first))
            {
                Union(first, i);
                Mark(i, "копия файла");
                Mark(first, "копия файла");
            }
            else byMd5[items[i].Md5] = i;
        }

        // 2) похожие по хэшу
        var hashes = items.Select(x => x.Hash).ToArray();
        for (int i = 0; i < n; i++)
        {
            if ((i & 255) == 0) ct.ThrowIfCancellationRequested();
            ulong hi = hashes[i];
            for (int j = i + 1; j < n; j++)
            {
                if (BitOperations.PopCount(hi ^ hashes[j]) <= o.Threshold)
                {
                    Union(i, j);
                    Mark(i, "похожее изображение");
                    Mark(j, "похожее изображение");
                }
            }
        }

        // 3) снятые почти одновременно — мягкий порог
        if (o.TimeWindowSec > 0)
        {
            var timed = Enumerable.Range(0, n)
                .Where(i => items[i].Taken.HasValue)
                .OrderBy(i => items[i].Taken!.Value)
                .ToArray();
            string label = $"снято рядом (≤{o.TimeWindowSec:0.#} с)";
            for (int a = 0; a < timed.Length; a++)
            {
                int i = timed[a];
                var ti = items[i].Taken!.Value;
                for (int b = a + 1; b < timed.Length; b++)
                {
                    int j = timed[b];
                    if ((items[j].Taken!.Value - ti).TotalSeconds > o.TimeWindowSec) break;
                    if (BitOperations.PopCount(hashes[i] ^ hashes[j]) <= o.TimeThreshold)
                    {
                        Union(i, j);
                        Mark(i, label);
                        Mark(j, label);
                    }
                }
            }
        }

        // сборка групп; лучшая копия — наибольшее разрешение, затем больший файл,
        // при равенстве — более короткое имя (обычно это оригинал, а не «копия»)
        var groups = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            int r = Root(i);
            if (!groups.TryGetValue(r, out var list))
            {
                list = new List<int>();
                groups[r] = list;
            }
            list.Add(i);
        }

        var result = new List<DupGroup>();
        int id = 0;
        foreach (var members in groups.Values
                     .Where(g => g.Count > 1)
                     .OrderBy(g => items[g[0]].FilePath, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = members
                .OrderByDescending(k => items[k].Pixels)
                .ThenByDescending(k => items[k].Bytes)
                .ThenBy(k => Path.GetFileName(items[k].FilePath).Length)
                .ThenBy(k => items[k].FilePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            int best = ordered[0];
            var group = new DupGroup { Id = ++id };
            foreach (var k in ordered)
            {
                group.Members.Add(new DupMember
                {
                    Photo = items[k],
                    IsBest = k == best,
                    Distance = BitOperations.PopCount(hashes[k] ^ hashes[best]),
                    Reason = reason[k] ?? "",
                });
            }
            result.Add(group);
        }
        return result;
    }
}
