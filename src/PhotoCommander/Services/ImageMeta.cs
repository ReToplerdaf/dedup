using MetadataExtractor;
using MetadataExtractor.Formats.Bmp;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.Formats.Png;
using MetadataExtractor.Formats.WebP;

namespace PhotoCommander.Services;

public sealed record ImageInfo(int Width, int Height, DateTime? Taken, int Orientation);

/// <summary>Размеры, дата съёмки и ориентация из EXIF (без декодирования пикселей).</summary>
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
            IReadOnlyList<MetadataExtractor.Directory> dirs;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                dirs = ImageMetadataReader.ReadMetadata(fs);

            var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
            var sub = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();

            DateTime? taken = null;
            if (sub != null && sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dto)) taken = dto;
            else if (ifd0 != null && ifd0.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var dt)) taken = dt;

            int orientation = 1;
            if (ifd0 != null && ifd0.TryGetInt32(ExifDirectoryBase.TagOrientation, out var o) && o is >= 1 and <= 8)
                orientation = o;

            var (w, h) = Size(dirs, ifd0, sub);
            return new ImageInfo(w, h, taken, orientation);
        }
        catch
        {
            return new ImageInfo(0, 0, null, 1);
        }
    }

    static (int, int) Size(IReadOnlyList<MetadataExtractor.Directory> dirs, ExifIfd0Directory? ifd0, ExifSubIfdDirectory? sub)
    {
        (int, int)? Pair(MetadataExtractor.Directory? d, int wTag, int hTag) =>
            d != null && d.TryGetInt32(wTag, out var w) && d.TryGetInt32(hTag, out var h) && w > 0 && h != 0
                ? (w, Math.Abs(h)) // у BMP высота бывает отрицательной
                : null;

        return Pair(dirs.OfType<JpegDirectory>().FirstOrDefault(), JpegDirectory.TagImageWidth, JpegDirectory.TagImageHeight)
            ?? Pair(dirs.OfType<PngDirectory>().FirstOrDefault(), PngDirectory.TagImageWidth, PngDirectory.TagImageHeight)
            ?? Pair(dirs.OfType<BmpHeaderDirectory>().FirstOrDefault(), BmpHeaderDirectory.TagImageWidth, BmpHeaderDirectory.TagImageHeight)
            ?? Pair(dirs.OfType<WebPDirectory>().FirstOrDefault(), WebPDirectory.TagImageWidth, WebPDirectory.TagImageHeight)
            ?? Pair(ifd0, ExifDirectoryBase.TagImageWidth, ExifDirectoryBase.TagImageHeight)        // TIFF
            ?? Pair(sub, ExifDirectoryBase.TagExifImageWidth, ExifDirectoryBase.TagExifImageHeight)
            ?? (0, 0);
    }
}
