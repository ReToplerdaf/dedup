using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PhotoCommander.Models;
using PhotoCommander.Services;
using PhotoCommander.Views;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(PhotoCommander.Tests.TestAppBuilder))]

namespace PhotoCommander.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public class UiTests
{
    /// <summary>Куда складывать скриншоты (папка создаётся рядом с тестами).</summary>
    static string Shot(string name)
    {
        var dir = Environment.GetEnvironmentVariable("PC_SCREENSHOTS") is { Length: > 0 } d
            ? d : Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    /// <summary>Крутить очередь UI, пока условие не выполнится (фоновые задачи, превью).</summary>
    static void WaitFor(Func<bool> cond, int ms = 10000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Не дождались условия");
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(20);
        }
    }

    static void SaveShot(Window w, string name)
    {
#pragma warning disable CS0618 // простого Save(path) достаточно
        w.CaptureRenderedFrame()?.Save(Shot(name));
#pragma warning restore CS0618
    }

    static Window? Dialog(Window owner) => owner.OwnedWindows.LastOrDefault();

    static void Click(Window dialog, string text)
    {
        var btn = dialog.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == text);
        btn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    static TempDir PhotoFolder()
    {
        var dir = new TempDir();
        using var a = TestImages.Scene(1, 1200, 900);
        using var b = TestImages.Scene(2, 1200, 900);
        TestImages.SaveJpegWithExif(a, dir["IMG_0001.jpg"], new DateTime(2024, 7, 15, 10, 0, 0));
        File.Copy(dir["IMG_0001.jpg"], dir["IMG_0001 (копия).jpg"]);
        using (var small = TestImages.Resized(a, 0.5)) TestImages.Save(small, dir["IMG_0001_small.jpg"], 70);
        TestImages.SaveJpegWithExif(b, dir["IMG_0002.jpg"], new DateTime(2023, 12, 31, 23, 59, 0), orientation: 6);
        Directory.CreateDirectory(dir["Архив"]);
        File.WriteAllText(dir["заметки.txt"], "не фото");
        return dir;
    }

    [AvaloniaFact]
    public void MainWindowShowsFolderAndPreview()
    {
        using var dir = PhotoFolder();
        var win = new MainWindow();
        win.Show();
        win.LeftPane.Navigate(dir.Path, "IMG_0002.jpg");
        win.LeftPane.FocusList();
        win.RightPane.Navigate(dir["Архив"]);

        // .. + папка + 5 файлов; строка состояния считает фото
        Assert.Equal(7, win.LeftPane.FileList.ItemCount);
        Assert.Contains("Фото: 4", win.LeftPane.StatusLine.Text);
        Assert.Equal("IMG_0002.jpg", win.LeftPane.Current?.Name);
        Assert.NotNull(win.LeftPane.DriveBox.SelectedItem); // диск, на котором лежит папка

        // превью грузится в фоне; IMG_0002 повёрнута EXIF-ом на 90°
        WaitFor(() => win.PreviewImage.Source != null);
        var bmp = (Avalonia.Media.Imaging.Bitmap)win.PreviewImage.Source!;
        Assert.True(bmp.PixelSize.Height > bmp.PixelSize.Width);
        Assert.Contains("Снято: 31.12.2023 23:59:00", win.PreviewInfo.Text);
        Assert.Contains("1200×900", win.PreviewInfo.Text);

        SaveShot(win, "main-window.png");
        win.Close();
    }

    [AvaloniaFact]
    public void TabSwitchesActivePane()
    {
        using var dir = PhotoFolder();
        var win = new MainWindow();
        win.Show();
        win.LeftPane.Navigate(dir.Path);
        win.RightPane.Navigate(dir["Архив"]);
        win.LeftPane.FocusList();
        WaitFor(() => win.LeftPane.IsActive);
        Assert.StartsWith("Источник: ЛЕВАЯ", win.ActiveLabel.Text);

        win.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        WaitFor(() => win.RightPane.IsActive);

        Assert.False(win.LeftPane.IsActive);
        Assert.Contains("ПРАВАЯ", win.ActiveLabel.Text);
        Assert.Equal("F5   Копировать  ←", win.BtnCopy.Content);
        win.Close();
    }

    [AvaloniaFact]
    public void F5CopiesSelectedFileToOtherPane()
    {
        using var dir = PhotoFolder();
        var win = new MainWindow();
        win.Show();
        win.LeftPane.Navigate(dir.Path, "IMG_0001.jpg");
        win.RightPane.Navigate(dir["Архив"]);
        win.LeftPane.FocusList();
        WaitFor(() => win.LeftPane.IsActive);

        win.KeyPress(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
        WaitFor(() => Dialog(win) != null);
        Click(Dialog(win)!, "OK");

        WaitFor(() => File.Exists(dir["Архив/IMG_0001.jpg"]));
        WaitFor(() => win.RightPane.FileList.ItemCount == 2);
        Assert.True(File.Exists(dir["IMG_0001.jpg"]));
        win.Close();
    }

    [AvaloniaFact]
    public void F7CreatesFolder()
    {
        using var dir = PhotoFolder();
        var win = new MainWindow();
        win.Show();
        win.LeftPane.Navigate(dir.Path);
        win.LeftPane.FocusList();

        win.KeyPress(Key.F7, RawInputModifiers.None, PhysicalKey.F7, null);
        WaitFor(() => Dialog(win) != null);
        var dlg = Dialog(win)!;
        dlg.GetVisualDescendants().OfType<TextBox>().First().Text = "Отпуск 2024";
        SaveShot(dlg, "new-folder-dialog.png");
        Click(dlg, "OK");

        WaitFor(() => Directory.Exists(dir["Отпуск 2024"]));
        WaitFor(() => win.LeftPane.Current?.Name == "Отпуск 2024");
        win.Close();
    }

    [AvaloniaFact]
    public void FindSimilarOpensResultsWindow()
    {
        using var dir = PhotoFolder();
        var win = new MainWindow();
        win.Show();
        win.LeftPane.Navigate(dir.Path);
        win.RightPane.Navigate(dir["Архив"]);
        win.LeftPane.FocusList();

        var findButton = win.CommandsPanel.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Найти похожие");
        findButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        WaitFor(() => Dialog(win) is DuplicatesWindow);
        var dw = (DuplicatesWindow)Dialog(win)!;
        WaitFor(() => dw.GroupList.ItemCount == 1);
        Assert.Contains("Групп: 1", dw.Summary.Text);
        Assert.Contains("Отмечено: 2", dw.Summary.Text);
        Assert.Contains("Групп похожих: 1, лишних копий: 2", win.StatusText.Text);

        // ждём превью в карточках и снимаем окно
        var group = (DupGroupVm)dw.GroupList.Items[0]!;
        Assert.Equal("IMG_0001.jpg", group.Items.Single(i => i.IsBest).Name); // оригинал, а не «(копия)»
        WaitFor(() => group.Items.All(i => i.Thumb != null));
        SaveShot(dw, "duplicates-window.png");

        // «★ Оставить это» на уменьшенной копии — отмечены остальные
        var small = group.Items.Single(i => i.Name == "IMG_0001_small.jpg");
        var keep = dw.GetVisualDescendants().OfType<Button>().First(b => b.Tag == small);
        keep.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(small.Marked);
        Assert.All(group.Items.Where(i => i != small), i => Assert.True(i.Marked));

        // CSV-отчёт
        var csv = dw.BuildCsv();
        Assert.StartsWith("группа;статус;файл", csv);
        Assert.Equal(4, csv.Trim().Split('\n').Length);

        // все отмечены → предупреждение в заголовке группы
        foreach (var i in group.Items) i.Marked = true;
        Assert.Contains("отмечены ВСЕ", group.Header);

        dw.Close();
        win.Close();
    }

    [AvaloniaFact]
    public void MoveMarkedToDuplicatesFolderKeepsBestCopy()
    {
        using var dir = PhotoFolder();
        var groups = DuplicateFinder.Find(dir.Path, new DupOptions(), null, default);
        var owner = new Window();
        owner.Show();
        var dw = new DuplicatesWindow(groups, dir.Path, dir["Архив"]);
        dw.Show(owner);

        var move = dw.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Переместить отмеченные…");
        move.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitFor(() => Dialog(dw) != null);
        Click(Dialog(dw)!, "Да");                       // в _duplicates
        WaitFor(() => Dialog(dw) is { } d && d.GetVisualDescendants().OfType<SelectableTextBlock>().Any(t => t.Text!.StartsWith("Перемещено")));
        Click(Dialog(dw)!, "OK");

        var best = groups[0].Members.Single(m => m.IsBest).Photo.FilePath;
        Assert.True(File.Exists(best));
        Assert.Equal(2, Directory.GetFiles(dir["_duplicates"]).Length);
        Assert.True(dw.FilesChanged);
        Assert.Equal(0, dw.GroupList.ItemCount);
        dw.Close();
        owner.Close();
    }
}

public class ViewerTests
{
    static void WaitFor(Func<bool> cond, int ms = 10000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Не дождались условия");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(20);
        }
    }

    static void Press(Window w, Key key, PhysicalKey phys) => w.KeyPress(key, RawInputModifiers.None, phys, null);

    static void SaveShot(Window w, string name)
    {
        var dir = Environment.GetEnvironmentVariable("PC_SCREENSHOTS") is { Length: > 0 } d
            ? d : Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
#pragma warning disable CS0618
        w.CaptureRenderedFrame()?.Save(Path.Combine(dir, name));
#pragma warning restore CS0618
    }

    /// <summary>Две группы: 3 версии одной сцены и 2 версии другой.</summary>
    static (TempDir dir, DuplicatesWindow dw, Window owner) Setup()
    {
        var dir = new TempDir();
        using (var a = TestImages.Scene(1, 1600, 1200))
        {
            TestImages.SaveJpegWithExif(a, dir["a1.jpg"], new DateTime(2024, 7, 15, 10, 0, 0));
            using var half = TestImages.Resized(a, 0.5);
            TestImages.Save(half, dir["a2.jpg"], 70);
            using var q = TestImages.Resized(a, 0.25);
            TestImages.Save(q, dir["a3.jpg"], 60);
        }
        using (var b = TestImages.Scene(2, 1200, 900))
        {
            TestImages.Save(b, dir["b1.jpg"]);
            using var half = TestImages.Resized(b, 0.5);
            TestImages.Save(half, dir["b2.jpg"]);
        }
        var groups = DuplicateFinder.Find(dir.Path, new DupOptions { TimeWindowSec = 0 }, null, default);
        Assert.Equal(2, groups.Count);
        var owner = new Window();
        owner.Show();
        var dw = new DuplicatesWindow(groups, dir.Path, dir.Path);
        dw.Show(owner);
        return (dir, dw, owner);
    }

    static DupGroupVm GroupA(DuplicatesWindow dw) =>
        dw.GroupList.Items.Cast<DupGroupVm>().Single(g => g.Items.Count == 3);

    [AvaloniaFact]
    public void OpensFullScreenOnBestPhotoAndNavigates()
    {
        var (dir, dw, owner) = Setup();
        using var _ = dir;
        var g = GroupA(dw);
        var v = dw.OpenViewer(g, null)!;

        Assert.Equal(WindowState.FullScreen, v.WindowState);
        Assert.Equal("a1.jpg", v.Current!.Name);           // начинает с лучшей
        Press(v, Key.Right, PhysicalKey.ArrowRight);
        Assert.Equal("a2.jpg", v.Current!.Name);
        Press(v, Key.Right, PhysicalKey.ArrowRight);
        Press(v, Key.Right, PhysicalKey.ArrowRight);         // с конца группы — в следующую
        Assert.NotSame(g, v.Current!.Group);
        Press(v, Key.Left, PhysicalKey.ArrowLeft);           // и обратно на последнее фото группы
        Assert.Equal("a3.jpg", v.Current!.Name);
        Press(v, Key.Down, PhysicalKey.ArrowDown);
        Assert.NotSame(g, v.Current!.Group);
        Press(v, Key.Up, PhysicalKey.ArrowUp);
        Assert.Same(g, v.Current!.Group);

        Press(v, Key.Escape, PhysicalKey.Escape);
        Assert.False(v.IsVisible);
        dw.Close(); owner.Close();
    }

    [AvaloniaFact]
    public void MarksAreSharedWithResultsWindow()
    {
        var (dir, dw, owner) = Setup();
        using var _ = dir;
        var g = GroupA(dw);
        var a2 = g.Items.Single(i => i.Name == "a2.jpg");
        var v = dw.OpenViewer(g, a2)!;

        Assert.True(a2.Marked);
        Press(v, Key.Space, PhysicalKey.Space);
        Assert.False(a2.Marked);                            // снял отметку — видно и в окне групп
        Press(v, Key.K, PhysicalKey.K);                     // «★ Оставить это» на a2
        Assert.Equal(new[] { true, false, true }, g.Items.OrderBy(i => i.Name).Select(i => i.Marked));
        Assert.Contains("отмечено: 2", g.Header);
        v.Close(); dw.Close(); owner.Close();
    }

    [AvaloniaFact]
    public void CompareModeShowsTwoPhotosWithSharedZoom()
    {
        var (dir, dw, owner) = Setup();
        using var _ = dir;
        var g = GroupA(dw);
        var v = dw.OpenViewer(g, null)!;
        v.WindowState = WindowState.Normal;                 // для скриншота фиксированного размера
        v.Width = 1400; v.Height = 820;

        Press(v, Key.C, PhysicalKey.C);
        Assert.True(v.IsCompare);
        Assert.Equal("a1.jpg", v.LeftItem!.Name);
        Assert.Equal("a2.jpg", v.RightItem!.Name);
        Assert.Same(v.RightItem, v.Current);                // активна правая половина

        Press(v, Key.Right, PhysicalKey.ArrowRight);        // листается только правая, левая — эталон
        Assert.Equal("a1.jpg", v.LeftItem!.Name);
        Assert.Equal("a3.jpg", v.RightItem!.Name);
        Press(v, Key.Right, PhysicalKey.ArrowRight);        // a1 уже слева — пропускаем
        Assert.Equal("a2.jpg", v.RightItem!.Name);

        Press(v, Key.Tab, PhysicalKey.Tab);
        Assert.Same(v.LeftItem, v.Current);

        Press(v, Key.OemPlus, PhysicalKey.Equal);
        Press(v, Key.OemPlus, PhysicalKey.Equal);
        Assert.True(v.View.Zoom > 1.5);                     // масштаб общий для обеих половин
        Press(v, Key.D0, PhysicalKey.Digit0);
        Assert.Equal(1, v.View.Zoom);

        WaitFor(() => v.GetVisualDescendants().OfType<PhotoCommander.Controls.ZoomImage>().All(z => z.Source != null));
        SaveShot(v, "viewer-compare.png");

        Press(v, Key.F1, PhysicalKey.F1);
        Assert.True(v.IsHelpVisible);
        SaveShot(v, "viewer-help.png");
        Press(v, Key.Escape, PhysicalKey.Escape);           // первый Esc закрывает только подсказку
        Assert.False(v.IsHelpVisible);
        Assert.True(v.IsVisible);

        Press(v, Key.C, PhysicalKey.C);
        Assert.False(v.IsCompare);
        v.Close(); dw.Close(); owner.Close();
    }

    [AvaloniaFact]
    public void CompareWithTwoPhotosSwapsSides()
    {
        var (dir, dw, owner) = Setup();
        using var _ = dir;
        var g = dw.GroupList.Items.Cast<DupGroupVm>().Single(x => x.Items.Count == 2);
        var v = dw.OpenViewer(g, null)!;
        Press(v, Key.C, PhysicalKey.C);
        var (l, r) = (v.LeftItem, v.RightItem);

        Press(v, Key.Right, PhysicalKey.ArrowRight);

        Assert.Same(r, v.LeftItem);
        Assert.Same(l, v.RightItem);
        v.Close(); dw.Close(); owner.Close();
    }

    [AvaloniaFact]
    public void ZoomKeepsPointUnderCursorAndClamps()
    {
        var img = new PhotoCommander.Controls.ZoomImage();
        var host = new Window { Width = 800, Height = 600, Content = img };
        host.Show();
        using (var m = TestImages.Scene(5, 1600, 1200))
        using (var dir = new TempDir())
        {
            var path = TestImages.Save(m, dir["z.png"]);
            img.Source = Thumbs.Load(path, 2000);
        }
        WaitFor(() => img.Bounds.Width > 0);

        img.ZoomAt(new Avalonia.Point(0, 0), 4);            // увеличение в левом верхнем углу
        Assert.Equal(4, img.State.Zoom, 3);
        Assert.True(img.State.Cx < 0.2 && img.State.Cy < 0.2);
        img.ZoomAt(new Avalonia.Point(400, 300), 1000);     // больше 8 пикселей экрана на пиксель — нельзя
        Assert.True(img.State.Zoom <= 8 / Math.Min(800.0 / 1600, 600.0 / 1200) + 1e-6);
        img.State.Reset();
        Assert.Equal(0.5, img.State.Cx);
        host.Close();
    }

    [AvaloniaFact]
    public void F1OpensHelpInMainWindow()
    {
        var win = new MainWindow();
        win.Show();
        win.KeyPress(Key.F1, RawInputModifiers.None, PhysicalKey.F1, null);
        WaitFor(() => win.OwnedWindows.Any());
        var help = win.OwnedWindows.Last();
        Assert.Contains("справка", help.Title);
        Assert.Contains(help.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Как пользоваться — коротко");
        help.Width = 860; help.Height = 640;
        SaveShot(help, "help-window.png");
        help.Close();
        win.Close();
    }
}
