using Avalonia.Media.Imaging;
using OpenCvSharp;

namespace PhotoCommander.Services;

/// <summary>Загрузка уменьшенных превью с учётом поворота из EXIF. Файл не блокируется.</summary>
public static class Thumbs
{
    public static Bitmap? Load(string path, int width)
    {
        try
        {
            using var mat = LoadMat(path, width);
            if (mat == null) return null;
            Cv2.ImEncode(".bmp", mat, out var bytes);
            using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Уменьшенная картинка (BGR), уже повёрнутая по EXIF; ширина не больше width.</summary>
    public static Mat? LoadMat(string path, int width)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length == 0) return null;
        var info = ImageMeta.Read(path);

        // JPEG умеет декодироваться сразу в 1/2, 1/4, 1/8 — так намного быстрее
        bool sideways = info.Orientation is 5 or 6 or 7 or 8;
        int along = sideways ? info.Height : info.Width;
        var mode = ImreadModes.Color;
        if (along >= width * 8) mode = ImreadModes.ReducedColor8;
        else if (along >= width * 4) mode = ImreadModes.ReducedColor4;
        else if (along >= width * 2) mode = ImreadModes.ReducedColor2;

        // поворот делаем сами — одинаково для всех форматов
        var mat = Cv2.ImDecode(data, mode | ImreadModes.IgnoreOrientation);
        if (mat.Empty()) { mat.Dispose(); return null; }

        ApplyOrientation(mat, info.Orientation);

        if (mat.Width > width)
        {
            var small = new Mat();
            Cv2.Resize(mat, small, new Size(width, Math.Max(1, (int)Math.Round(mat.Height * (double)width / mat.Width))),
                0, 0, InterpolationFlags.Area);
            mat.Dispose();
            mat = small;
        }
        return mat;
    }

    /// <summary>EXIF Orientation 1–8 → поворот/отражение на месте.</summary>
    public static void ApplyOrientation(Mat m, int orientation)
    {
        switch (orientation)
        {
            case 2: Cv2.Flip(m, m, FlipMode.Y); break;
            case 3: Cv2.Rotate(m, m, RotateFlags.Rotate180); break;
            case 4: Cv2.Flip(m, m, FlipMode.X); break;
            case 5: Cv2.Transpose(m, m); break;
            case 6: Cv2.Rotate(m, m, RotateFlags.Rotate90Clockwise); break;
            case 7: Cv2.Rotate(m, m, RotateFlags.Rotate90Counterclockwise); Cv2.Flip(m, m, FlipMode.Y); break;
            case 8: Cv2.Rotate(m, m, RotateFlags.Rotate90Counterclockwise); break;
        }
    }
}
