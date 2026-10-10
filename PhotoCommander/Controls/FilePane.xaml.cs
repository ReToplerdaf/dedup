using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PhotoCommander.Models;
using PhotoCommander.Services;

namespace PhotoCommander.Controls;

/// <summary>Одна файловая панель в стиле Norton Commander.</summary>
public partial class FilePane : UserControl
{
    List<FileEntry> _items = new();
    List<string> _drives = new();
    bool _suppressDrive;
    bool _isActive;

    public string CurrentPath { get; private set; } = "";

    /// <summary>Панель получила фокус.</summary>
    public event EventHandler? Activated;
    /// <summary>Изменился текущий/выделенный элемент.</summary>
    public event EventHandler? CurrentChanged;

    public FilePane()
    {
        InitializeComponent();
        LoadDrives();
        IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if (e.NewValue is true) Activated?.Invoke(this, EventArgs.Empty);
        };
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            PaneBorder.BorderBrush = (Brush)FindResource(value ? "NcActiveBorder" : "NcInactiveBorder");
        }
    }

    public FileEntry? Current => FileList.SelectedItem as FileEntry;

    public List<FileEntry> SelectedEntries =>
        FileList.SelectedItems.Cast<FileEntry>().Where(x => !x.IsParent).ToList();

    void LoadDrives()
    {
        _suppressDrive = true;
        try
        {
            _drives = DriveInfo.GetDrives().Where(x => x.IsReady).Select(x => x.Name).ToList();
        }
        catch
        {
            _drives = new List<string>();
        }
        DriveBox.ItemsSource = _drives;
        _suppressDrive = false;
    }

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
                MessageBox.Show($"Папка не найдена:\n{path}", "Photo Commander");
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
            MessageBox.Show(ex.Message, "Photo Commander");
            PathBox.Text = CurrentPath;
            return false;
        }

        _items = list;
        CurrentPath = di.FullName;
        PathBox.Text = CurrentPath;
        FileList.ItemsSource = _items;

        _suppressDrive = true;
        var root = Path.GetPathRoot(CurrentPath);
        DriveBox.SelectedItem = _drives.FirstOrDefault(x => string.Equals(x, root, StringComparison.OrdinalIgnoreCase));
        _suppressDrive = false;

        if (_items.Count > 0)
        {
            int idx = 0;
            if (selectName != null)
                idx = Math.Max(0, _items.FindIndex(x => string.Equals(x.Name, selectName, StringComparison.OrdinalIgnoreCase)));
            FileList.SelectedIndex = idx;
            FileList.ScrollIntoView(_items[idx]);
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
        FileList.ScrollIntoView(FileList.SelectedItem);
        FileList.UpdateLayout();
        if (FileList.ItemContainerGenerator.ContainerFromIndex(FileList.SelectedIndex) is ListViewItem item)
            item.Focus();
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
        try
        {
            Process.Start(new ProcessStartInfo(cur.FullPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Photo Commander");
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

    void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(FileList, d) is ListViewItem)
            OpenCurrent();
    }

    void FileList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OpenCurrent(); e.Handled = true; }
        else if (e.Key == Key.Back) { GoUp(); e.Handled = true; }
    }

    void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateStatus();
        CurrentChanged?.Invoke(this, EventArgs.Empty);
    }

    void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (Navigate(PathBox.Text)) FocusList();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            PathBox.Text = CurrentPath;
            FocusList();
            e.Handled = true;
        }
    }

    void DriveBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDrive || DriveBox.SelectedItem is not string drive) return;
        if (!string.Equals(Path.GetPathRoot(CurrentPath), drive, StringComparison.OrdinalIgnoreCase))
        {
            if (Navigate(drive)) FocusList();
        }
    }

    void Up_Click(object sender, RoutedEventArgs e) => GoUp();

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Выберите папку", InitialDirectory = CurrentPath };
        if (dlg.ShowDialog() == true && Navigate(dlg.FolderName)) FocusList();
    }
}
