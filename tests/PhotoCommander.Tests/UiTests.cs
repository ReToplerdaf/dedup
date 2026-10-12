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
