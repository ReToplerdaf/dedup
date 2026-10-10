using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

        LeftPane.Navigate(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        RightPane.Navigate(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));

        Loaded += (_, _) =>
        {
            SetActive(LeftPane);
            LeftPane.FocusList();
        };
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
        ActiveLabel.Text = left ? "Источник: ЛЕВАЯ панель  →" : "←  Источник: ПРАВАЯ панель";
        BtnCopy.Content = $"F5   Копировать  {arrow}";
        BtnMove.Content = $"F6   Переместить  {arrow}";
        BtnSame.Content = $"=    Та же папка  {arrow}";
        BtnSort.Content = $"📅   Разложить по датам  {arrow}";
        UpdatePreview();
    }

    // ---------- клавиатура ----------

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_cts != null) return; // идёт операция
        bool inText = Keyboard.FocusedElement is TextBox;

        switch (e.Key)
        {
            case Key.Tab when !inText:
            {
                var other = Other;
                SetActive(other);
                other.FocusList();
                e.Handled = true;
                break;
            }
            case Key.F5:
                Copy_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.F6:
                Move_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.F7:
                MkDir_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.F8:
            case Key.Delete when !inText:
                Recycle_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.R when Keyboard.Modifiers == ModifierKeys.Control:
                RefreshBoth();
                e.Handled = true;
                break;
        }
    }

    // ---------- файловые операции ----------

    async void Copy_Click(object sender, RoutedEventArgs e) => await CopyMove(false);
    async void Move_Click(object sender, RoutedEventArgs e) => await CopyMove(true);

    async Task CopyMove(bool move)
    {
        if (_cts != null) return;
        var sel = _active.SelectedEntries;
        if (sel.Count == 0) return;
        string src = _active.CurrentPath, dest = Other.CurrentPath;
        if (SamePath(src, dest))
        {
            MessageBox.Show(this, "В обеих панелях открыта одна и та же папка.", Title);
            return;
        }
        string verb = move ? "Переместить" : "Копировать";
        if (MessageBox.Show(this, $"{verb} {Describe(sel)}\nв {dest}?", Title,
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
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

    void MkDir_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        var name = InputDialog.Ask(this, "Новая папка", $"Создать папку в\n{_active.CurrentPath}");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            Directory.CreateDirectory(Path.Combine(_active.CurrentPath, name));
            _active.Navigate(_active.CurrentPath, name);
            _active.FocusList();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title);
        }
    }

    void Recycle_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        var sel = _active.SelectedEntries;
        if (sel.Count == 0) return;
        if (MessageBox.Show(this, $"Отправить в Корзину {Describe(sel)}?", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        if (!FileOps.ToRecycleBin(sel.Select(x => x.FullPath)))
            MessageBox.Show(this, "Не все объекты удалось удалить.", Title);
        RefreshBoth();
    }

    void SameFolder_Click(object sender, RoutedEventArgs e)
    {
        Other.Navigate(_active.CurrentPath, _active.Current?.Name);
    }

    // ---------- поиск похожих ----------

    async void FindSimilar_Click(object sender, RoutedEventArgs e)
    {
        var opt = ReadDupOptions();
        if (opt == null) return;
        string root = _active.CurrentPath;

        var groups = await RunJob("Поиск похожих", (p, ct) => DuplicateFinder.Find(root, opt, p, ct));
        if (groups == null) return;

        int extra = groups.Sum(g => g.Members.Count - 1);
        StatusText.Text = $"Групп похожих: {groups.Count}, лишних копий: {extra}";
        if (groups.Count == 0)
        {
            MessageBox.Show(this, "Похожих фото не найдено.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var win = new DuplicatesWindow(groups, root, Other.CurrentPath) { Owner = this };
        win.ShowDialog();
        if (win.FilesChanged) RefreshBoth();
    }

    DupOptions? ReadDupOptions()
    {
        if (!double.TryParse(TimeWindowBox.Text.Trim().Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var window) || window < 0)
        {
            MessageBox.Show(this, "«Окно, секунд» — число, например 10 (0 — выключить).", Title);
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

    async void SortDates_Click(object sender, RoutedEventArgs e)
    {
        var opt = ReadDupOptions();
        if (opt == null) return;
        string src = _active.CurrentPath, dest = Other.CurrentPath;
        bool move = MoveRadio.IsChecked == true;
        bool recursive = SortRecursiveCheck.IsChecked == true;
        bool skipDups = SkipDupsCheck.IsChecked == true;
        int pattern = PatternBox.SelectedIndex;
        opt.Recursive = recursive;

        string verb = move ? "Переместить" : "Скопировать";
        if (MessageBox.Show(this,
                $"{verb} фото из\n{src}\n\nв папки по датам ({DateSorter.Patterns[Math.Max(0, pattern)].Title}) внутри\n{dest}?" +
                (skipDups ? "\n\nДубликаты будут пропущены (берётся лучшая копия)." : ""),
                Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
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
                    StringComparer.OrdinalIgnoreCase);
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
            PreviewImage.Source = null;
            PreviewInfo.Text = cur == null || cur.IsParent ? "" : cur.IsDirectory ? $"Папка: {cur.Name}" : cur.Name;
            return;
        }

        await Task.Delay(120); // не грузить каждую картинку при быстрой прокрутке
        if (ticket != _previewTicket) return;

        string path = cur.FullPath;
        var (bmp, info) = await Task.Run(() => (Thumbs.Load(path, 400), ImageMeta.Read(path)));
        if (ticket != _previewTicket) return;

        PreviewImage.Source = bmp;
        string when = info.Taken is DateTime t ? $"Снято: {t:dd.MM.yyyy HH:mm:ss}" : "Дата съёмки: нет в EXIF";
        PreviewInfo.Text = $"{cur.Name}\n{info.Width}×{info.Height}, {FileEntry.FormatSize(cur.Size)}\n{when}";
    }

    // ---------- фоновые задачи ----------

    async Task<T?> RunJob<T>(string title, Func<IProgress<ScanProgress>, CancellationToken, T> work) where T : class
    {
        if (_cts != null) return null;
        var cts = new CancellationTokenSource();
        _cts = cts;
        var progress = new Progress<ScanProgress>(ShowProgress);

        CommandsPanel.IsEnabled = false;
        CancelBtn.Visibility = Visibility.Visible;
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
            MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
        finally
        {
            _cts = null;
            cts.Dispose();
            CommandsPanel.IsEnabled = true;
            CancelBtn.Visibility = Visibility.Collapsed;
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

    void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // ---------- помощники ----------

    void RefreshBoth()
    {
        LeftPane.Refresh();
        RightPane.Refresh();
    }

    static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);

    static string Describe(List<FileEntry> sel) =>
        sel.Count == 1 ? $"«{sel[0].Name}»" : $"{sel.Count} объектов";
}
