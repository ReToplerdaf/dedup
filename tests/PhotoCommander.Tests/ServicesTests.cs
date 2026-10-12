using OpenCvSharp;
using PhotoCommander.Services;
using Xunit;

namespace PhotoCommander.Tests;

public class ImageMetaTests
{
    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".bmp")]
    [InlineData(".tif")]
    [InlineData(".webp")]
    public void ReadsSizeOfEveryFormat(string ext)
    {
        using var dir = new TempDir();
        using var m = TestImages.Scene(1, 640, 480);
        var path = TestImages.Save(m, dir["img" + ext]);

        var info = ImageMeta.Read(path);

        Assert.Equal(640, info.Width);
        Assert.Equal(480, info.Height);
        Assert.True(ImageMeta.IsImage(path));
    }

    [Fact]
    public void ReadsExifDateAndOrientation()
    {
        using var dir = new TempDir();
        using var m = TestImages.Scene(2, 320, 200);
        var taken = new DateTime(2024, 7, 15, 13, 45, 10);
        var path = TestImages.SaveJpegWithExif(m, dir["exif.jpg"], taken, orientation: 6);

        var info = ImageMeta.Read(path);

        Assert.Equal(taken, info.Taken);
        Assert.Equal(6, info.Orientation);
        Assert.Equal(320, info.Width);
    }

    [Fact]
    public void BrokenFileGivesEmptyInfo()
    {
        using var dir = new TempDir();
        var path = dir["broken.jpg"];
        File.WriteAllText(path, "это не картинка");

        var info = ImageMeta.Read(path);

        Assert.Equal(0, info.Width);
        Assert.Null(info.Taken);
    }
}

public class ThumbsTests
{
    [Fact]
    public void RotatesByExifOrientation()
    {
        using var dir = new TempDir();
        using var m = TestImages.Scene(3, 800, 400);
        var path = TestImages.SaveJpegWithExif(m, dir["rot.jpg"], null, orientation: 6);

        using var thumb = Thumbs.LoadMat(path, 200);

        Assert.NotNull(thumb);
        Assert.Equal(200, thumb!.Width);   // после поворота на 90° картинка «стоячая»
        Assert.Equal(400, thumb.Height);
    }

    [Fact]
    public void DoesNotUpscaleSmallImages()
    {
        using var dir = new TempDir();
        using var m = TestImages.Scene(4, 120, 90);
        var path = TestImages.Save(m, dir["small.png"]);

        using var thumb = Thumbs.LoadMat(path, 400);

        Assert.Equal(120, thumb!.Width);
    }

    [Fact]
    public void FileIsNotLockedAfterLoading()
    {
        using var dir = new TempDir();
        using var m = TestImages.Scene(5);
        var path = TestImages.Save(m, dir["a.jpg"]);

        using (Thumbs.LoadMat(path, 100)) { }
        File.Move(path, dir["b.jpg"]);

        Assert.True(File.Exists(dir["b.jpg"]));
    }
}

public class DuplicateFinderTests
{
    static readonly DupOptions Defaults = new() { TimeWindowSec = 0 };

    [Fact]
    public void FindsExactCopiesResizedAndRecodedVersions()
    {
        using var dir = new TempDir();
        using var scene = TestImages.Scene(10, 1200, 900);
        using var small = TestImages.Resized(scene, 0.5);
        using var other = TestImages.Scene(99, 1200, 900);

        var original = TestImages.Save(scene, dir["IMG_0001.jpg"]);
        File.Copy(original, dir["копия/IMG_0001.jpg"].Also(p => Directory.CreateDirectory(Path.GetDirectoryName(p)!)));
        TestImages.Save(small, dir["маленькая.jpg"], jpegQuality: 70);
        TestImages.Save(scene, dir["другой формат.png"]);
        TestImages.Save(other, dir["другое фото.jpg"]);

        var groups = DuplicateFinder.Find(dir.Path, Defaults, null, CancellationToken.None);

        var g = Assert.Single(groups);
        Assert.Equal(4, g.Members.Count);
        Assert.DoesNotContain(g.Members, x => x.Photo.FilePath.EndsWith("другое фото.jpg"));
        // лучшая — наибольшее разрешение, затем больший файл
        var best = Assert.Single(g.Members, x => x.IsBest);
        Assert.Equal(1200 * 900, best.Photo.Pixels);
        Assert.Equal(g.Members.Where(x => x.Photo.Pixels == best.Photo.Pixels).Max(x => x.Photo.Bytes), best.Photo.Bytes);
        Assert.Equal(g.Members[0], best);
        Assert.Contains(g.Members, x => x.Reason == "копия файла");
    }

    [Fact]
    public void RecursiveOptionAndDuplicatesFolderAreRespected()
    {
        using var dir = new TempDir();
        using var scene = TestImages.Scene(11);
        TestImages.Save(scene, dir["a.jpg"]);
        Directory.CreateDirectory(dir["sub"]);
        File.Copy(dir["a.jpg"], dir["sub/a.jpg"]);
        Directory.CreateDirectory(dir["_duplicates"]);
        File.Copy(dir["a.jpg"], dir["_duplicates/a.jpg"]);

        Assert.Single(DuplicateFinder.Find(dir.Path, new DupOptions { Recursive = true }, null, default));
        Assert.Empty(DuplicateFinder.Find(dir.Path, new DupOptions { Recursive = false }, null, default));
        Assert.Equal(2, DuplicateFinder.ListImages(dir.Path, true).Count); // _duplicates пропускается
    }

    [Fact]
    public void BurstShotsAreGroupedOnlyWithTimeWindow()
    {
        using var dir = new TempDir();
        using var a = TestImages.Scene(20);
        // «следующий кадр серии»: сдвиг и пара новых фигур
        using var b = new Mat();
        Cv2.WarpAffine(a, b, Cv2.GetRotationMatrix2D(new Point2f(400, 300), 3, 1.04), a.Size(), borderMode: BorderTypes.Reflect);
        Cv2.Circle(b, new Point(600, 150), 60, new Scalar(0, 0, 255), -1);
        var t = new DateTime(2024, 5, 1, 10, 0, 0);
        TestImages.SaveJpegWithExif(a, dir["burst1.jpg"], t);
        TestImages.SaveJpegWithExif(b, dir["burst2.jpg"], t.AddSeconds(2));

        var strict = new DupOptions { Threshold = 2, TimeWindowSec = 0, TimeThreshold = 24 };
        var timed = new DupOptions { Threshold = 2, TimeWindowSec = 10, TimeThreshold = 24 };

        Assert.Empty(DuplicateFinder.Find(dir.Path, strict, null, default));
        var g = Assert.Single(DuplicateFinder.Find(dir.Path, timed, null, default));
        Assert.All(g.Members, m => Assert.StartsWith("снято рядом", m.Reason));
    }

    [Fact]
    public void CanBeCancelled()
    {
        using var dir = new TempDir();
        using var scene = TestImages.Scene(30);
        for (int i = 0; i < 5; i++) TestImages.Save(scene, dir[$"{i}.jpg"]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => DuplicateFinder.Find(dir.Path, Defaults, null, cts.Token));
    }
}

public class DateSorterTests
{
    [Theory]
    [InlineData(0, "2024/2024-07")]
    [InlineData(1, "2024/07")]
    [InlineData(2, "2024/2024-07-15")]
    [InlineData(3, "2024-07-15")]
    public void SortsIntoDateFolders(int pattern, string expectedFolder)
    {
        using var dir = new TempDir();
        using var m = TestImages.Scene(40, 200, 150);
        var src = TestImages.SaveJpegWithExif(m, dir["src/фото.jpg"], new DateTime(2024, 7, 15, 9, 0, 0));
        var dest = dir["dest"];

        int n = DateSorter.Sort(new[] { src }, dest, pattern, move: false, null, default);

        Assert.Equal(1, n);
        Assert.True(File.Exists(Path.Combine(dest, expectedFolder.Replace('/', Path.DirectorySeparatorChar), "фото.jpg")));
        Assert.True(File.Exists(src)); // копирование не трогает исходник
    }

    [Fact]
    public void MoveDoesNotOverwriteExistingFiles()
    {
        using var dir = new TempDir();
        using var m = TestImages.Scene(41, 200, 150);
        var t = new DateTime(2023, 1, 2);
        var a = TestImages.SaveJpegWithExif(m, dir["a/x.jpg"], t);
        var b = TestImages.SaveJpegWithExif(m, dir["b/x.jpg"], t);

        DateSorter.Sort(new[] { a, b }, dir["out"], 3, move: true, null, default);

        Assert.True(File.Exists(dir["out/2023-01-02/x.jpg"]));
        Assert.True(File.Exists(dir["out/2023-01-02/x_1.jpg"]));
        Assert.False(File.Exists(a));
    }
}

public class FileOpsTests
{
    [Fact]
    public void UniquePathAddsSuffix()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir["a.jpg"], "");
        File.WriteAllText(dir["a_1.jpg"], "");

        Assert.Equal(dir["b.jpg"], FileOps.UniquePath(dir["b.jpg"]));
        Assert.Equal(dir["a_2.jpg"], FileOps.UniquePath(dir["a.jpg"]));
    }

    [Fact]
    public void PathHelpers()
    {
        var root = Path.Combine(Path.GetTempPath(), "x");
        Assert.True(FileOps.IsSameOrInside(Path.Combine(root, "y"), root));
        Assert.True(FileOps.IsSameOrInside(root + Path.DirectorySeparatorChar, root));
        Assert.False(FileOps.IsSameOrInside(root + "yz", root));
        Assert.True(FileOps.SamePath(root, root + Path.DirectorySeparatorChar));

        // корень диска: «C:\» или «/»
        var drive = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.True(FileOps.IsSameOrInside(root, drive));
        Assert.True(FileOps.IsSameOrInside(drive, drive));
        Assert.True(FileOps.SamePath(drive, drive));
        Assert.False(FileOps.IsSameOrInside(drive, root));
    }

    [Fact]
    public void CopyAndMoveDirectory()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir["src/a.txt"].Also(p => Directory.CreateDirectory(Path.GetDirectoryName(p)!)), "1");
        File.WriteAllText(dir["src/sub/b.txt"].Also(p => Directory.CreateDirectory(Path.GetDirectoryName(p)!)), "2");

        FileOps.CopyDirectory(dir["src"], dir["copy"]);
        FileOps.MoveDirectory(dir["src"], dir["moved"]);

        Assert.Equal("2", File.ReadAllText(dir["copy/sub/b.txt"]));
        Assert.Equal("2", File.ReadAllText(dir["moved/sub/b.txt"]));
        Assert.False(Directory.Exists(dir["src"]));
    }

    [Fact]
    public void RecycleBinOnLinuxFollowsFreedesktopSpec()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var dir = new TempDir();
        var oldXdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", dir["xdg"]);
        try
        {
            File.WriteAllText(dir["мой файл.jpg"], "x");
            Directory.CreateDirectory(dir["папка"]);
            File.WriteAllText(dir["второй/мой файл.jpg"].Also(p => Directory.CreateDirectory(Path.GetDirectoryName(p)!)), "y");

            Assert.True(FileOps.ToRecycleBin(new[] { dir["мой файл.jpg"], dir["папка"], dir["второй/мой файл.jpg"] }));

            Assert.False(File.Exists(dir["мой файл.jpg"]));
            Assert.False(Directory.Exists(dir["папка"]));
            Assert.True(File.Exists(dir["xdg/Trash/files/мой файл.jpg"]));
            Assert.True(File.Exists(dir["xdg/Trash/files/мой файл_1.jpg"]));
            Assert.True(Directory.Exists(dir["xdg/Trash/files/папка"]));
            var info = File.ReadAllText(dir["xdg/Trash/info/мой файл.jpg.trashinfo"]);
            Assert.Contains("[Trash Info]", info);
            Assert.Contains("Path=" + string.Join("/", dir["мой файл.jpg"].Split('/').Select(Uri.EscapeDataString)), info);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", oldXdg);
        }
    }
}

static class Ext
{
    public static T Also<T>(this T v, Action<T> a) { a(v); return v; }
}
