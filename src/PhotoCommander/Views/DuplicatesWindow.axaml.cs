using System.Collections.ObjectModel;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using PhotoCommander.Models;
using PhotoCommander.Services;

namespace PhotoCommander.Views;

/// <summary>Результаты поиска: группы похожих фото с превью.</summary>
public partial class DuplicatesWindow : Window
{
    readonly ObservableCollection<DupGroupVm> _groups = new();
    readonly string _root;
    readonly string _otherPane;

    /// <summary>true, если файлы перемещались/удалялись — панели нужно обновить.</summary>
    public bool FilesChanged { get; private set; }

    public DuplicatesWindow() : this(new List<DupGroup>(), "", "") { }

    public DuplicatesWindow(List<DupGroup> groups, string root, string otherPane)
    {
        InitializeComponent();
        _root = root;
        _otherPane = otherPane;
        foreach (var g in groups)
        {
            var vm = new DupGroupVm(g);
            vm.PropertyChanged += (_, _) => UpdateSummary();
            _groups.Add(vm);
        }
        GroupList.ItemsSource = _groups;
        UpdateSummary();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F1) { Help.Show(this); e.Handled = true; }
        };
    }

    IEnumerable<DupItemVm> AllItems => _groups.SelectMany(g => g.Items);

    Task<DialogResult> Message(string text, DialogButtons buttons = DialogButtons.Ok, DialogIcon icon = DialogIcon.None) =>
        Dialogs.Show(this, text, Title ?? "", buttons, icon);

    void UpdateSummary()
    {
        var marked = AllItems.Where(i => i.Marked).ToList();
        Summary.Text = $"Папка: {_root}     Групп: {_groups.Count}     Фото в группах: {AllItems.Count()}     " +
                       $"Отмечено: {marked.Count} ({FileEntry.FormatSize(marked.Sum(i => i.M.Photo.Bytes))})";
    }

    void MarkAuto_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var i in AllItems) i.Marked = !i.IsBest;
    }

    void UnmarkAll_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var i in AllItems) i.Marked = false;
    }

    void KeepThis_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not DupItemVm item) return;
        foreach (var i in item.Group.Items) i.Marked = !ReferenceEquals(i, item);
    }

    void Card_DoubleTapped(object? sender, TappedEventArgs e)
    {
        // двойной щелчок по галочке или кнопке — это не просьба открыть фото
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(true) == null && v.FindAncestorOfType<CheckBox>(true) == null
            && (sender as Control)?.Tag is DupItemVm item)
            OpenViewer(item.Group, item);
    }

    void FullScreen_Click(object? sender, RoutedEventArgs e) => OpenViewer(null, null);

    void Help_Click(object? sender, RoutedEventArgs e) => Help.Show(this);

    /// <summary>Открыть просмотр на весь экран; отметки в нём сразу видны и здесь.</summary>
    public ViewerWindow? OpenViewer(DupGroupVm? group, DupItemVm? item)
    {
        if (_groups.Count == 0) return null;
        var viewer = new ViewerWindow(_groups, group, item);
        viewer.Closed += (_, _) => Activate();
        viewer.Show(this);
        return viewer;
    }

    /// <summary>Отмеченные элементы с проверкой «не отмечена ли вся группа».</summary>
    async Task<List<DupItemVm>?> GetMarkedChecked(string verb)
    {
        var marked = AllItems.Where(i => i.Marked).ToList();
        if (marked.Count == 0)
        {
            await Message("Ничего не отмечено.");
            return null;
        }
        int fullGroups = _groups.Count(g => g.Items.All(i => i.Marked));
        if (fullGroups > 0 &&
            await Message($"В {fullGroups} групп(ах) отмечены ВСЕ фото — от них не останется ни одной копии.\nВсё равно {verb}?",
                DialogButtons.YesNo, DialogIcon.Warning) != DialogResult.Yes)
            return null;
        return marked;
    }

    async void MoveMarked_Click(object? sender, RoutedEventArgs e)
    {
        var marked = await GetMarkedChecked("переместить");
        if (marked == null) return;

        string defaultDest = Path.Combine(_root, "_duplicates");
        var answer = await Message(
            $"Переместить {marked.Count} файлов в\n{defaultDest}\n\n«Да» — туда,  «Нет» — выбрать другую папку.",
            DialogButtons.YesNoCancel, DialogIcon.Question);
        if (answer == DialogResult.Cancel) return;

        string dest = defaultDest;
        if (answer == DialogResult.No)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Куда переместить дубликаты",
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(_otherPane),
            });
            var picked = folders.FirstOrDefault()?.TryGetLocalPath();
            if (picked == null) return;
            dest = picked;
        }

        var paths = marked.Select(m => m.FullPath).ToList();
        string root = _root;
        var (ok, errors) = await Busy(() => Task.Run(() =>
        {
            var moved = new List<string>();
            var err = new StringBuilder();
            foreach (var path in paths)
            {
                try
                {
                    // сохраняем структуру подпапок относительно исходной папки
                    var rel = Path.GetRelativePath(root, path);
                    if (rel.StartsWith("..") || Path.IsPathRooted(rel)) rel = Path.GetFileName(path);
                    var target = FileOps.UniquePath(Path.Combine(dest, rel));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(path, target);
                    moved.Add(path);
                }
                catch (Exception ex)
                {
                    err.AppendLine($"{Path.GetFileName(path)}: {ex.Message}");
                }
            }
            return (moved, err);
        }));
        var movedSet = new HashSet<string>(ok, FileOps.PathComparer);
        await Finish(marked.Where(m => movedSet.Contains(m.FullPath)).ToList(), errors,
            $"Перемещено файлов: {ok.Count}\nв {dest}");
    }

    async void RecycleMarked_Click(object? sender, RoutedEventArgs e)
    {
        var marked = await GetMarkedChecked("удалить");
        if (marked == null) return;
        if (await Message($"Отправить в Корзину {marked.Count} файлов?", DialogButtons.YesNo, DialogIcon.Question) != DialogResult.Yes)
            return;

        var paths = marked.Select(m => m.FullPath).ToList();
        await Busy(() => Task.Run(() => FileOps.ToRecycleBin(paths)));
        var done = marked.Where(m => !File.Exists(m.FullPath)).ToList();
        var errors = new StringBuilder();
        if (done.Count < marked.Count) errors.AppendLine($"Не удалось удалить файлов: {marked.Count - done.Count}");
        await Finish(done, errors, $"Отправлено в Корзину: {done.Count}");
    }

    /// <summary>Выполнить долгую операцию: окно заблокировано, курсор «ожидание».</summary>
    async Task<T> Busy<T>(Func<Task<T>> work)
    {
        IsEnabled = false;
        Cursor = new Cursor(StandardCursorType.Wait);
        try
        {
            return await work();
        }
        finally
        {
            Cursor = Cursor.Default;
            IsEnabled = true;
        }
    }

    async Task Finish(List<DupItemVm> done, StringBuilder errors, string message)
    {
        if (done.Count > 0) FilesChanged = true;
        foreach (var it in done) it.Group.Items.Remove(it);
        foreach (var g in _groups.Where(g => g.Items.Count < 2).ToList()) _groups.Remove(g);
        foreach (var g in _groups) g.Refresh();
        UpdateSummary();

        bool hasErrors = errors.Length > 0;
        string text = hasErrors ? message + "\n\nОшибки:\n" + errors : message;
        await Message(text, icon: hasErrors ? DialogIcon.Warning : DialogIcon.Info);
    }

    async void SaveCsv_Click(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Сохранить отчёт",
            SuggestedFileName = "duplicates_report.csv",
            DefaultExtension = "csv",
            FileTypeChoices = new[] { new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } } },
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(_root),
        });
        var path = file?.TryGetLocalPath();
        if (path == null) return;

        try
        {
            File.WriteAllText(path, BuildCsv(), new UTF8Encoding(true));
            await Message($"Отчёт сохранён:\n{path}", icon: DialogIcon.Info);
        }
        catch (Exception ex)
        {
            await Message(ex.Message, icon: DialogIcon.Error);
        }
    }

    internal string BuildCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("группа;статус;файл;ширина;высота;байт;отличие;время съёмки;причина;отмечен");
        foreach (var g in _groups)
        {
            foreach (var i in g.Items)
            {
                var p = i.M.Photo;
                sb.AppendLine(string.Join(";",
                    g.Id.ToString(),
                    i.IsBest ? "KEEP" : "DUP",
                    Csv(i.FullPath),
                    p.Width.ToString(),
                    p.Height.ToString(),
                    p.Bytes.ToString(),
                    i.M.Distance.ToString(),
                    p.Taken?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                    Csv(i.Reason),
                    i.Marked ? "да" : ""));
            }
        }
        return sb.ToString();
    }

    static string Csv(string s) =>
        s.Contains(';') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
