using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using PhotoCommander.Models;
using PhotoCommander.Services;
using PhotoCommander.Views;

namespace PhotoCommander.Controls;

/// <summary>Одна файловая панель в стиле Norton Commander.</summary>
public partial class FilePane : UserControl
{
    List<FileEntry> _items = new();
    List<DriveRoot> _drives = new();
    bool _suppressDrive;
    bool _isActive;

    public string CurrentPath { get; private set; } = "";

    /// <summary>Панель получила фокус или по ней щёлкнули.</summary>
    public event EventHandler? Activated;
    /// <summary>Изменился текущий/выделенный элемент.</summary>
    public event EventHandler? CurrentChanged;

    public FilePane()
    {
        InitializeComponent();
        LoadDrives();
        AddHandler(GotFocusEvent, (_, _) => Activated?.Invoke(this, EventArgs.Empty), RoutingStrategies.Bubble, true);
        AddHandler(PointerPressedEvent, (_, _) => Activated?.Invoke(this, EventArgs.Empty), RoutingStrategies.Tunnel, true);
    }

    Window? Owner => TopLevel.GetTopLevel(this) as Window;

    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            PaneBorder.BorderBrush = (IBrush)this.FindResource(value ? "NcActiveBorder" : "NcInactiveBorder")!;
            FileList.Classes.Set("active", value);
        }
    }

    public FileEntry? Current => FileList.SelectedItem as FileEntry;

    public List<FileEntry> SelectedEntries =>
        FileList.SelectedItems?.Cast<FileEntry>().Where(x => !x.IsParent).ToList() ?? new List<FileEntry>();

    void LoadDrives()
    {
        _suppressDrive = true;
        _drives = FileOps.ListRoots();
        DriveBox.ItemsSource = _drives;
        SelectDrive();
        _suppressDrive = false;
    }

    /// <summary>В списке дисков выделить тот, на котором лежит текущая папка.</summary>
    void SelectDrive()
    {
        if (string.IsNullOrEmpty(CurrentPath)) return;
        DriveBox.SelectedItem = _drives
            .Where(r => FileOps.IsSameOrInside(CurrentPath, r.Path))
            .OrderByDescending(r => r.Path.Length)
            .FirstOrDefault();
    }

    void ShowError(string text) => _ = Dialogs.Show(Owner, text, "Photo Commander", DialogButtons.Ok, DialogIcon.Warning);

    /// <summary>Открыть папку. selectName — какой элемент выделить.</summary>
    public bool Navigate(string path, string? selectName = null)
    {
        DirectoryInfo di;
        var list = new List<FileEntry>();
        try
        {
            di = new DirectoryInfo(path.Trim());
            if (!di.Exists)
            {
                ShowError($"Папка не найдена:\n{path}");
                PathBox.Text = CurrentPath;
                return false;
            }

            if (di.Parent != null)
                list.Add(new FileEntry { Name = "..", FullPath = di.Parent.FullName, IsDirectory = true, IsParent = true });

            var opt = new EnumerationOptions { IgnoreInaccessible = true };
            foreach (var d in di.EnumerateDirectories("*", opt).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                list.Add(new FileEntry { Name = d.Name, FullPath = d.FullName, IsDirectory = true, Modified = d.LastWriteTime });
            foreach (var f in di.EnumerateFiles("*", opt).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                list.Add(new FileEntry
                {
                    Name = f.Name, FullPath = f.FullName, Size = f.Length,
                    Modified = f.LastWriteTime, IsImage = ImageMeta.IsImage(f.Name),
                });
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            PathBox.Text = CurrentPath;
            return false;
        }

        _items = list;
        CurrentPath = di.FullName;
        PathBox.Text = CurrentPath;
        FileList.ItemsSource = _items;

        _suppressDrive = true;
        SelectDrive();
        _suppressDrive = false;

        if (_items.Count > 0)
        {
            int idx = 0;
            if (selectName != null)
                idx = Math.Max(0, _items.FindIndex(x => string.Equals(x.Name, selectName, FileOps.PathComparison)));
            FileList.SelectedIndex = idx;
            FileList.ScrollIntoView(idx);
        }
        UpdateStatus();
        return true;
    }

    public void Refresh()
    {
        if (string.IsNullOrEmpty(CurrentPath)) return;
        var name = Current?.Name;
        var path = CurrentPath;
        while (!Directory.Exists(path))
        {
            var p = Directory.GetParent(path);
            if (p == null) { LoadDrives(); return; }
            path = p.FullName;
        }
        LoadDrives();
        Navigate(path, name);
    }

    public void GoUp()
    {
        if (string.IsNullOrEmpty(CurrentPath)) return;
        var di = new DirectoryInfo(CurrentPath);
        if (di.Parent == null) return;
        if (Navigate(di.Parent.FullName, di.Name)) FocusList();
    }

    /// <summary>Поставить клавиатурный фокус на текущую строку списка.</summary>
    public void FocusList()
    {
        if (_items.Count == 0) { FileList.Focus(); return; }
        if (FileList.SelectedIndex < 0) FileList.SelectedIndex = 0;
        int idx = FileList.SelectedIndex;
        FileList.ScrollIntoView(idx);
        FileList.UpdateLayout();
        if (FileList.ContainerFromIndex(idx) is { } item)
            item.Focus(NavigationMethod.Directional);
        else
            FileList.Focus();
    }

    void OpenCurrent()
    {
        var cur = Current;
        if (cur == null) return;
        if (cur.IsParent) { GoUp(); return; }
        if (cur.IsDirectory)
        {
            if (Navigate(cur.FullPath)) FocusList();
            return;
        }
        OpenFile(Owner, cur.FullPath);
    }

    /// <summary>Открыть файл программой по умолчанию.</summary>
    public static void OpenFile(Window? owner, string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _ = Dialogs.Show(owner, ex.Message, "Photo Commander", DialogButtons.Ok, DialogIcon.Warning);
        }
    }

    void UpdateStatus()
    {
        var sel = SelectedEntries;
        if (sel.Count > 1)
        {
            long selBytes = sel.Where(x => !x.IsDirectory).Sum(x => x.Size);
            StatusLine.Text = $"Выделено: {sel.Count}  ({FileEntry.FormatSize(selBytes)})";
            return;
        }
        int dirs = _items.Count(x => x.IsDirectory && !x.IsParent);
        int files = _items.Count(x => !x.IsDirectory);
        int imgs = _items.Count(x => x.IsImage);
        long bytes = _items.Where(x => !x.IsDirectory).Sum(x => x.Size);
        StatusLine.Text = $"Папок: {dirs}   Файлов: {files} ({FileEntry.FormatSize(bytes)})   Фото: {imgs}";
    }

    // ---------- обработчики ----------

    void FileList_ContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        var entry = e.Container.DataContext as FileEntry ?? (e.Index < _items.Count ? _items[e.Index] : null);
        e.Container.Classes.Set("dir", entry?.IsDirectory == true);
        e.Container.Classes.Set("img", entry?.IsImage == true);
    }

    void FileList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual v && v.FindAncestorOfType<ListBoxItem>(includeSelf: true) != null)
            OpenCurrent();
    }

    void FileList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OpenCurrent(); e.Handled = true; }
        else if (e.Key == Key.Back) { GoUp(); e.Handled = true; }
    }

    void FileList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateStatus();
        CurrentChanged?.Invoke(this, EventArgs.Empty);
    }

    void PathBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (Navigate(PathBox.Text ?? "")) FocusList();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            PathBox.Text = CurrentPath;
            FocusList();
            e.Handled = true;
        }
    }

    void DriveBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressDrive || DriveBox.SelectedItem is not DriveRoot drive) return;
        if (!FileOps.SamePath(CurrentPath, drive.Path) && Navigate(drive.Path)) FocusList();
    }

    void Up_Click(object? sender, RoutedEventArgs e) => GoUp();

    async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (sp == null) return;
        var folders = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Выберите папку",
            SuggestedStartLocation = await sp.TryGetFolderFromPathAsync(CurrentPath),
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path != null && Navigate(path)) FocusList();
    }
}
