using System.Globalization;
using System.Windows.Media.Imaging;

namespace PhotoCommander.Services;

public sealed record ImageInfo(int Width, int Height, DateTime? Taken, int Orientation);

/// <summary>Размеры, дата съёмки и ориентация из EXIF (через WIC, без полного декодирования).</summary>
public static class ImageMeta
{
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".webp",
    };

    public static bool IsImage(string path) => Extensions.Contains(Path.GetExtension(path));

    public static ImageInfo Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var decoder = BitmapDecoder.Create(fs,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            int w = frame.PixelWidth, h = frame.PixelHeight;
            DateTime? taken = null;
            int orientation = 1;
            try
            {
                if (frame.Metadata is BitmapMetadata md)
                {
                    taken = ParseExif(Query(md, "/app1/ifd/exif/{ushort=36867}"))   // DateTimeOriginal (JPEG)
                         ?? ParseExif(Query(md, "/ifd/exif/{ushort=36867}"))        // DateTimeOriginal (TIFF)
                         ?? ParseExif(Query(md, "/app1/ifd/{ushort=306}"));         // DateTime
                    if (taken == null && !string.IsNullOrEmpty(md.DateTaken)
                        && DateTime.TryParse(md.DateTaken, out var dt))
                        taken = dt;

                    var o = Query(md, "/app1/ifd/{ushort=274}") ?? Query(md, "/ifd/{ushort=274}");
                    if (o != null) orientation = Convert.ToInt32(o);
                }
            }
            catch
            {
                // у PNG/BMP может не быть метаданных — это нормально
            }
            return new ImageInfo(w, h, taken, orientation);
        }
        catch
        {
            return new ImageInfo(0, 0, null, 1);
        }
    }

    static object? Query(BitmapMetadata md, string query)
    {
        try { return md.ContainsQuery(query) ? md.GetQuery(query) : null; }
        catch { return null; }
    }

    static DateTime? ParseExif(object? value)
    {
        if (value is not string s) return null;
        s = s.Trim().TrimEnd('\0');
        if (s.Length >= 19 && DateTime.TryParseExact(s.Substring(0, 19), "yyyy:MM:dd HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        return null;
    }
}
