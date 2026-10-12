using OpenCvSharp;

namespace PhotoCommander.Tests;

/// <summary>Генерация тестовых картинок и временных папок.</summary>
static class TestImages
{
    /// <summary>Картинка-«сцена»: градиент и фигуры, зависящие от seed.</summary>
    public static Mat Scene(int seed, int width = 800, int height = 600)
    {
        var m = new Mat(height, width, MatType.CV_8UC3);
        var rnd = new Random(seed);
        for (int y = 0; y < height; y++)
            Cv2.Line(m, new Point(0, y), new Point(width - 1, y),
                new Scalar(y * 255 / height, 128 + seed * 37 % 128, 255 - y * 255 / height));
        for (int i = 0; i < 12; i++)
        {
            var c = new Scalar(rnd.Next(256), rnd.Next(256), rnd.Next(256));
            var p = new Point(rnd.Next(width), rnd.Next(height));
            if (i % 2 == 0) Cv2.Circle(m, p, rnd.Next(20, width / 4), c, -1);
            else Cv2.Rectangle(m, new Rect(p.X, p.Y, rnd.Next(20, width / 3), rnd.Next(20, height / 3)), c, -1);
        }
        return m;
    }

    public static string Save(Mat m, string path, int jpegQuality = 92)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var ext = Path.GetExtension(path);
        var prms = ext is ".jpg" or ".jpeg" ? new[] { (int)ImwriteFlags.JpegQuality, jpegQuality } : Array.Empty<int>();
        Cv2.ImEncode(ext, m, out var bytes, prms);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public static Mat Resized(Mat m, double scale)
    {
        var r = new Mat();
        Cv2.Resize(m, r, new Size((int)(m.Width * scale), (int)(m.Height * scale)), 0, 0, InterpolationFlags.Area);
        return r;
    }

    /// <summary>JPEG с EXIF: дата съёмки (DateTimeOriginal) и ориентация.</summary>
    public static string SaveJpegWithExif(Mat m, string path, DateTime? taken, int orientation = 1)
    {
        Cv2.ImEncode(".jpg", m, out var jpg, new[] { (int)ImwriteFlags.JpegQuality, 92 });
        var app1 = BuildExif(taken, orientation);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        fs.Write(jpg, 0, 2);                 // SOI
        fs.Write(app1);                      // APP1 Exif
        fs.Write(jpg, 2, jpg.Length - 2);    // остальное
        return path;
    }

    /// <summary>Минимальный блок APP1/Exif (little-endian): IFD0 { Orientation, [ExifIFD] } → { DateTimeOriginal }.</summary>
    static byte[] BuildExif(DateTime? taken, int orientation)
    {
        var tiff = new List<byte>();
        void U16(int v) { tiff.Add((byte)v); tiff.Add((byte)(v >> 8)); }
        void U32(int v) { U16(v & 0xFFFF); U16(v >> 16); }

        int entries = taken == null ? 1 : 2;
        tiff.AddRange("II"u8.ToArray()); U16(42); U32(8);           // заголовок TIFF, IFD0 по смещению 8
        U16(entries);
        U16(0x0112); U16(3); U32(1); U16(orientation); U16(0);       // Orientation (SHORT)
        int exifIfdOffset = 8 + 2 + entries * 12 + 4;
        if (taken != null)
        {
            U16(0x8769); U16(4); U32(1); U32(exifIfdOffset);         // указатель на Exif IFD
        }
        U32(0);                                                      // следующего IFD нет
        if (taken != null)
        {
            int dateOffset = exifIfdOffset + 2 + 12 + 4;
            U16(1);
            U16(0x9003); U16(2); U32(20); U32(dateOffset);           // DateTimeOriginal (ASCII, 20 байт)
            U32(0);
            tiff.AddRange(System.Text.Encoding.ASCII.GetBytes(taken.Value.ToString("yyyy:MM:dd HH:mm:ss")));
            tiff.Add(0);
        }

        var body = new List<byte>();
        body.AddRange("Exif\0\0"u8.ToArray());
        body.AddRange(tiff);
        int len = body.Count + 2;
        var seg = new List<byte> { 0xFF, 0xE1, (byte)(len >> 8), (byte)len };
        seg.AddRange(body);
        return seg.ToArray();
    }
}

/// <summary>Временная папка, удаляется после теста.</summary>
sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pc-tests-" + Guid.NewGuid().ToString("N")[..8], "Фото");

    public TempDir() => Directory.CreateDirectory(Path);

    public string this[string rel] => System.IO.Path.Combine(Path, rel);

    public void Dispose()
    {
        try { Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, true); } catch { }
    }
}
