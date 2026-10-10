using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Input;
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
    }

    IEnumerable<DupItemVm> AllItems => _groups.SelectMany(g => g.Items);

    void UpdateSummary()
    {
        var marked = AllItems.Where(i => i.Marked).ToList();
        Summary.Text = $"Папка: {_root}     Групп: {_groups.Count}     Фото в группах: {AllItems.Count()}     " +
                       $"Отмечено: {marked.Count} ({FileEntry.FormatSize(marked.Sum(i => i.M.Photo.Bytes))})";
    }

    void MarkAuto_Click(object sender, RoutedEventArgs e)
    {
        foreach (var i in AllItems) i.Marked = !i.IsBest;
    }

    void UnmarkAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var i in AllItems) i.Marked = false;
    }

    void KeepThis_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DupItemVm item) return;
        foreach (var i in item.Group.Items) i.Marked = !ReferenceEquals(i, item);
    }

    void Card_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || (sender as FrameworkElement)?.Tag is not DupItemVm item) return;
        try
        {
            Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title);
        }
    }

    /// <summary>Отмеченные элементы с проверкой «не отмечена ли вся группа».</summary>
    List<DupItemVm>? GetMarkedChecked(string verb)
    {
        var marked = AllItems.Where(i => i.Marked).ToList();
        if (marked.Count == 0)
        {
            MessageBox.Show(this, "Ничего не отмечено.", Title);
            return null;
        }
        int fullGroups = _groups.Count(g => g.Items.All(i => i.Marked));
        if (fullGroups > 0 &&
            MessageBox.Show(this,
                $"В {fullGroups} групп(ах) отмечены ВСЕ фото — от них не останется ни одной копии.\nВсё равно {verb}?",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return null;
        return marked;
    }

    void MoveMarked_Click(object sender, RoutedEventArgs e)
    {
        var marked = GetMarkedChecked("переместить");
        if (marked == null) return;

        string defaultDest = Path.Combine(_root, "_duplicates");
        var answer = MessageBox.Show(this,
            $"Переместить {marked.Count} файлов в\n{defaultDest}\n\n«Да» — туда,  «Нет» — выбрать другую папку.",
            Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return;

        string dest = defaultDest;
        if (answer == MessageBoxResult.No)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Куда переместить дубликаты", InitialDirectory = _otherPane };
            if (dlg.ShowDialog() != true) return;
            dest = dlg.FolderName;
        }

        var done = new List<DupItemVm>();
        var errors = new StringBuilder();
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            foreach (var it in marked)
            {
                try
                {
                    // сохраняем структуру подпапок относительно исходной папки
                    var rel = Path.GetRelativePath(_root, it.FullPath);
                    if (rel.StartsWith("..") || Path.IsPathRooted(rel)) rel = Path.GetFileName(it.FullPath);
                    var target = FileOps.UniquePath(Path.Combine(dest, rel));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(it.FullPath, target);
                    done.Add(it);
                }
                catch (Exception ex)
                {
                    errors.AppendLine($"{it.Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
        Finish(done, errors, $"Перемещено файлов: {done.Count}\nв {dest}");
    }

    void RecycleMarked_Click(object sender, RoutedEventArgs e)
    {
        var marked = GetMarkedChecked("удалить");
        if (marked == null) return;
        if (MessageBox.Show(this, $"Отправить в Корзину {marked.Count} файлов?", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            FileOps.ToRecycleBin(marked.Select(m => m.FullPath));
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
        var done = marked.Where(m => !File.Exists(m.FullPath)).ToList();
        var errors = new StringBuilder();
        if (done.Count < marked.Count) errors.AppendLine($"Не удалось удалить файлов: {marked.Count - done.Count}");
        Finish(done, errors, $"Отправлено в Корзину: {done.Count}");
    }

    void Finish(List<DupItemVm> done, StringBuilder errors, string message)
    {
        if (done.Count > 0) FilesChanged = true;
        foreach (var it in done) it.Group.Items.Remove(it);
        foreach (var g in _groups.Where(g => g.Items.Count < 2).ToList()) _groups.Remove(g);
        foreach (var g in _groups) g.Refresh();
        UpdateSummary();

        bool hasErrors = errors.Length > 0;
        string text = hasErrors ? message + "\n\nОшибки:\n" + errors.ToString() : message;
        MessageBox.Show(this, text, Title, MessageBoxButton.OK,
            hasErrors ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    void SaveCsv_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = "duplicates_report.csv",
            InitialDirectory = _root,
        };
        if (dlg.ShowDialog(this) != true) return;

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
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
        MessageBox.Show(this, $"Отчёт сохранён:\n{dlg.FileName}", Title);
    }

    static string Csv(string s) =>
        s.Contains(';') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
