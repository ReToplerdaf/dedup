using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoCommander.Services;

/// <summary>Загрузка уменьшенных превью с учётом поворота из EXIF. Файл не блокируется.</summary>
public static class Thumbs
{
    public static BitmapSource? Load(string path, int width)
    {
        try
        {
            var info = ImageMeta.Read(path);
            bool sideways = info.Orientation is 6 or 8;

            var bi = new BitmapImage();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (sideways) bi.DecodePixelHeight = width; else bi.DecodePixelWidth = width;
                bi.StreamSource = fs;
                bi.EndInit();
            }

            BitmapSource result = bi;
            int angle = info.Orientation switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };
            if (angle != 0) result = new TransformedBitmap(bi, new RotateTransform(angle));
            result.Freeze();
            return result;
        }
        catch
        {
            return null;
        }
    }
}
