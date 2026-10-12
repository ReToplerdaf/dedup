using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PhotoCommander.Controls;
using PhotoCommander.Models;
using PhotoCommander.Services;
using PhotoCommander.Views;

namespace PhotoCommander;

public partial class MainWindow : Window
{
    FilePane _active;
    bool _activeInitialized;
    CancellationTokenSource? _cts;
    int _previewTicket;

    FilePane Other => ReferenceEquals(_active, LeftPane) ? RightPane : LeftPane;

    public MainWindow()
    {
        InitializeComponent();
        _active = LeftPane;

        PatternBox.ItemsSource = DateSorter.Patterns.Select(p => p.Title).ToList();
        PatternBox.SelectedIndex = 0;

        LeftPane.Activated += (_, _) => SetActive(LeftPane);
        RightPane.Activated += (_, _) => SetActive(RightPane);
        LeftPane.CurrentChanged += (_, _) => { if (ReferenceEquals(_active, LeftPane)) UpdatePreview(); };
        RightPane.CurrentChanged += (_, _) => { if (ReferenceEquals(_active, RightPane)) UpdatePreview(); };

        LeftPane.Navigate(StartFolder(Environment.SpecialFolder.Desktop));
        RightPane.Navigate(StartFolder(Environment.SpecialFolder.MyPictures));

        // F-клавиши и Tab перехватываем до того, как их обработают списки и поля ввода
        AddHandler(KeyDownEvent, Window_PreviewKeyDown, RoutingStrategies.Tunnel);

        Opened += (_, _) =>
        {
            SetActive(LeftPane);
            LeftPane.FocusList();
        };
    }

    /// <summary>Папка для старта: нужная системная, иначе домашняя.</summary>
    static string StartFolder(Environment.SpecialFolder folder)
    {
        var p = Environment.GetFolderPath(folder);
        if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p;
        p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Directory.Exists(p) ? p : Path.GetPathRoot(Environment.CurrentDirectory) ?? "/";
    }

    // ---------- активная панель ----------

    void SetActive(FilePane pane)
    {
        if (ReferenceEquals(_active, pane) && _activeInitialized) return;
        _activeInitialized = true;
        _active = pane;
        LeftPane.IsActive = ReferenceEquals(pane, LeftPane);
        RightPane.IsActive = ReferenceEquals(pane, RightPane);

        bool left = ReferenceEquals(pane, LeftPane);
        string arrow = left ? "→" : "←";
        ActiveLabel.Text = left ? "Источник: ЛЕВАЯ  →" : "←  Источник: ПРАВАЯ";
        BtnCopy.Content = $"F5   Копировать  {arrow}";
        BtnMove.Content = $"F6   Переместить  {arrow}";
        BtnSame.Content = $"=    Та же папка  {arrow}";
        BtnSort.Content = $"Разложить по датам  {arrow}";
        UpdatePreview();
    }

    // ---------- клавиатура ----------

    void Window_PreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_cts != null) return; // идёт операция
        bool inText = FocusManager?.GetFocusedElement() is TextBox;

        switch (e.Key)
        {
            case Key.Tab when !inText && e.KeyModifiers == KeyModifiers.None:
            {
                var other = Other;
                SetActive(other);
                other.FocusList();
                e.Handled = true;
                break;
            }
            case Key.F1:
                Help.Show(this);
                e.Handled = true;
                break;
            case Key.F5:
                _ = CopyMove(false);
                e.Handled = true;
                break;
            case Key.F6:
                _ = CopyMove(true);
                e.Handled = true;
                break;
            case Key.F7:
                _ = MkDir();
                e.Handled = true;
                break;
            case Key.F8:
            case Key.Delete when !inText:
                _ = Recycle();
                e.Handled = true;
                break;
            case Key.R when e.KeyModifiers == KeyModifiers.Control:
                RefreshBoth();
                e.Handled = true;
                break;
        }
    }

    // ---------- файловые операции ----------

    void Copy_Click(object? sender, RoutedEventArgs e) => _ = CopyMove(false);
    void Move_Click(object? sender, RoutedEventArgs e) => _ = CopyMove(true);
    void MkDir_Click(object? sender, RoutedEventArgs e) => _ = MkDir();
    void Recycle_Click(object? sender, RoutedEventArgs e) => _ = Recycle();

    Task<DialogResult> Message(string text, DialogButtons buttons = DialogButtons.Ok, DialogIcon icon = DialogIcon.None) =>
        Dialogs.Show(this, text, Title ?? "Photo Commander", buttons, icon);

    async Task CopyMove(bool move)
    {
        if (_cts != null) return;
        var sel = _active.SelectedEntries;
        if (sel.Count == 0) return;
        string src = _active.CurrentPath, dest = Other.CurrentPath;
        if (FileOps.SamePath(src, dest))
        {
            await Message("В обеих панелях открыта одна и та же папка.");
            return;
        }
        string verb = move ? "Переместить" : "Копировать";
        if (await Message($"{verb} {Describe(sel)}\nв {dest}?", DialogButtons.OkCancel, DialogIcon.Question) != DialogResult.Ok)
            return;

        await RunJob(move ? "Перемещение" : "Копирование", (progress, ct) =>
        {
            int n = 0;
            foreach (var it in sel)
            {
                ct.ThrowIfCancellationRequested();
                if (it.IsDirectory && FileOps.IsSameOrInside(dest, it.FullPath))
                    throw new InvalidOperationException($"Нельзя {verb.ToLower()} папку «{it.Name}» саму в себя.");
                var target = FileOps.UniquePath(Path.Combine(dest, it.Name));
                if (it.IsDirectory)
                {
                    if (move) FileOps.MoveDirectory(it.FullPath, target);
                    else FileOps.CopyDirectory(it.FullPath, target);
                }
                else if (move) File.Move(it.FullPath, target);
                else File.Copy(it.FullPath, target);
                progress.Report(new ScanProgress(++n, sel.Count, move ? "Перемещение" : "Копирование"));
            }
            return $"Готово: {n} из {sel.Count}";
        });
        RefreshBoth();
    }

    async Task MkDir()
    {
        if (_cts != null) return;
        var pane = _active;
        var name = await Dialogs.Ask(this, "Новая папка", $"Создать папку в\n{pane.CurrentPath}");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            Directory.CreateDirectory(Path.Combine(pane.CurrentPath, name));
            pane.Navigate(pane.CurrentPath, name);
            pane.FocusList();
        }
        catch (Exception ex)
        {
            await Message(ex.Message, icon: DialogIcon.Warning);
        }
    }

    async Task Recycle()
    {
        if (_cts != null) return;
        var sel = _active.SelectedEntries;
        if (sel.Count == 0) return;
        if (await Message($"Отправить в Корзину {Describe(sel)}?", DialogButtons.YesNo, DialogIcon.Warning) != DialogResult.Yes)
            return;
        if (!FileOps.ToRecycleBin(sel.Select(x => x.FullPath)))
            await Message("Не все объекты удалось удалить.", icon: DialogIcon.Warning);
        RefreshBoth();
        _active.FocusList();
    }

    void Help_Click(object? sender, RoutedEventArgs e) => Help.Show(this);

    void SameFolder_Click(object? sender, RoutedEventArgs e)
    {
        Other.Navigate(_active.CurrentPath, _active.Current?.Name);
    }

    // ---------- поиск похожих ----------

    async void FindSimilar_Click(object? sender, RoutedEventArgs e)
    {
        var opt = await ReadDupOptions();
        if (opt == null) return;
        string root = _active.CurrentPath;

        var groups = await RunJob("Поиск похожих", (p, ct) => DuplicateFinder.Find(root, opt, p, ct));
        if (groups == null) return;

        int extra = groups.Sum(g => g.Members.Count - 1);
        StatusText.Text = $"Групп похожих: {groups.Count}, лишних копий: {extra}";
        if (groups.Count == 0)
        {
            await Message("Похожих фото не найдено.", icon: DialogIcon.Info);
            return;
        }

        var win = new DuplicatesWindow(groups, root, Other.CurrentPath);
        await win.ShowDialog(this);
        if (win.FilesChanged) RefreshBoth();
    }

    async Task<DupOptions?> ReadDupOptions()
    {
        if (!double.TryParse((TimeWindowBox.Text ?? "").Trim().Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var window) || window < 0)
        {
            await Message("«Окно, секунд» — число, например 10 (0 — выключить).", icon: DialogIcon.Warning);
            return null;
        }
        return new DupOptions
        {
            Threshold = (int)ThrSlider.Value,
            TimeThreshold = (int)TimeThrSlider.Value,
            TimeWindowSec = window,
            Recursive = RecursiveCheck.IsChecked == true,
            UseFileTimeIfNoExif = MtimeCheck.IsChecked == true,
        };
    }

    // ---------- сортировка по датам ----------

    async void SortDates_Click(object? sender, RoutedEventArgs e)
    {
        var opt = await ReadDupOptions();
        if (opt == null) return;
        string src = _active.CurrentPath, dest = Other.CurrentPath;
        bool move = MoveRadio.IsChecked == true;
        bool recursive = SortRecursiveCheck.IsChecked == true;
        bool skipDups = SkipDupsCheck.IsChecked == true;
        int pattern = Math.Max(0, PatternBox.SelectedIndex);
        opt.Recursive = recursive;

        string verb = move ? "Переместить" : "Скопировать";
        if (await Message(
                $"{verb} фото из\n{src}\n\nв папки по датам ({DateSorter.Patterns[pattern].Title}) внутри\n{dest}?" +
                (skipDups ? "\n\nДубликаты будут пропущены (берётся лучшая копия)." : ""),
                DialogButtons.OkCancel, DialogIcon.Question) != DialogResult.Ok)
            return;

        await RunJob("Сортировка по датам", (p, ct) =>
        {
            var files = DuplicateFinder.ListImages(src, recursive);
            int skipped = 0;
            if (skipDups)
            {
                var groups = DuplicateFinder.Find(src, opt, p, ct);
                var dups = new HashSet<string>(
                    groups.SelectMany(g => g.Members.Where(m => !m.IsBest).Select(m => m.Photo.FilePath)),
                    FileOps.PathComparer);
                int before = files.Count;
                files = files.Where(f => !dups.Contains(f)).ToList();
                skipped = before - files.Count;
            }
            int n = DateSorter.Sort(files, dest, pattern, move, p, ct);
            return $"Разложено по датам: {n}" + (skipDups ? $", пропущено дубликатов: {skipped}" : "");
        });
        RefreshBoth();
    }

    // ---------- превью ----------

    async void UpdatePreview()
    {
        int ticket = ++_previewTicket;
        var cur = _active.Current;
        if (cur == null || !cur.IsImage)
        {
            SetPreview(null);
            PreviewInfo.Text = cur == null || cur.IsParent ? "" : cur.IsDirectory ? $"Папка: {cur.Name}" : cur.Name;
            return;
        }

        await Task.Delay(120); // не грузить каждую картинку при быстрой прокрутке
        if (ticket != _previewTicket) return;

        string path = cur.FullPath;
        var (bmp, info) = await Task.Run(() => (Thumbs.Load(path, 400), ImageMeta.Read(path)));
        if (ticket != _previewTicket) { bmp?.Dispose(); return; }

        SetPreview(bmp);
        string when = info.Taken is DateTime t ? $"Снято: {t:dd.MM.yyyy HH:mm:ss}" : "Дата съёмки: нет в EXIF";
        PreviewInfo.Text = $"{cur.Name}\n{info.Width}×{info.Height}, {FileEntry.FormatSize(cur.Size)}\n{when}";
    }

    void SetPreview(Avalonia.Media.Imaging.Bitmap? bmp)
    {
        var old = PreviewImage.Source as IDisposable;
        PreviewImage.Source = bmp;
        old?.Dispose();
    }

    // ---------- фоновые задачи ----------

    async Task<T?> RunJob<T>(string title, Func<IProgress<ScanProgress>, CancellationToken, T> work) where T : class
    {
        if (_cts != null) return null;
        var cts = new CancellationTokenSource();
        _cts = cts;
        var progress = new Progress<ScanProgress>(ShowProgress);

        CommandsPanel.IsEnabled = false;
        CancelBtn.IsVisible = true;
        Bar.IsIndeterminate = true;
        StatusText.Text = title + "…";
        try
        {
            var result = await Task.Run(() => work(progress, cts.Token));
            if (result is string s) StatusText.Text = s;
            return result;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = title + ": отменено";
            return null;
        }
        catch (Exception ex)
        {
            StatusText.Text = title + ": ошибка";
            await Dialogs.Show(this, ex.Message, title, DialogButtons.Ok, DialogIcon.Error);
            return null;
        }
        finally
        {
            _cts = null;
            cts.Dispose();
            CommandsPanel.IsEnabled = true;
            CancelBtn.IsVisible = false;
            Bar.IsIndeterminate = false;
            Bar.Value = 0;
        }
    }

    void ShowProgress(ScanProgress p)
    {
        if (_cts == null) return; // запоздавшее сообщение после завершения
        Bar.IsIndeterminate = p.Total <= 0;
        if (p.Total > 0)
        {
            Bar.Maximum = p.Total;
            Bar.Value = p.Done;
            StatusText.Text = $"{p.Stage}: {p.Done} / {p.Total}";
        }
        else
        {
            StatusText.Text = p.Stage;
        }
    }

    void Cancel_Click(object? sender, RoutedEventArgs e) => _cts?.Cancel();

    // ---------- помощники ----------

    void RefreshBoth()
    {
        LeftPane.Refresh();
        RightPane.Refresh();
    }

    static string Describe(List<FileEntry> sel) =>
        sel.Count == 1 ? $"«{sel[0].Name}»" : $"{sel.Count} объектов";
}
