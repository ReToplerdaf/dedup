using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using PhotoCommander.Models;
using PhotoCommander.Services;

namespace PhotoCommander.Views;

public sealed class DupGroupVm : INotifyPropertyChanged
{
    public int Id { get; }
    public ObservableCollection<DupItemVm> Items { get; } = new();

    public DupGroupVm(DupGroup g)
    {
        Id = g.Id;
        foreach (var m in g.Members) Items.Add(new DupItemVm(m, this));
    }

    public string Header
    {
        get
        {
            int marked = Items.Count(i => i.Marked);
            string warn = Items.Count > 0 && marked == Items.Count ? "   ⚠ отмечены ВСЕ — не останется ни одного" : "";
            return $"Группа {Id} — фото: {Items.Count}, отмечено: {marked}{warn}";
        }
    }

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Header)));

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class DupItemVm : INotifyPropertyChanged
{
    bool _marked;
    bool _thumbRequested;
    BitmapSource? _thumb;

    public DupMember M { get; }
    public DupGroupVm Group { get; }

    public DupItemVm(DupMember m, DupGroupVm group)
    {
        M = m;
        Group = group;
        _marked = !m.IsBest; // по умолчанию отмечены все, кроме лучшей копии
    }

    public string FullPath => M.Photo.FilePath;
    public string Name => Path.GetFileName(M.Photo.FilePath);
    public string Folder => Path.GetDirectoryName(M.Photo.FilePath) ?? "";
    public string Info => $"{M.Photo.Width}×{M.Photo.Height} · {FileEntry.FormatSize(M.Photo.Bytes)}";
    public string When => M.Photo.Taken?.ToString("dd.MM.yyyy HH:mm:ss") ?? "дата неизвестна";
    public string Badge => M.IsBest ? "★ ЛУЧШЕЕ" : $"отличие {M.Distance}";
    public string Reason => M.Reason;
    public bool IsBest => M.IsBest;

    public bool Marked
    {
        get => _marked;
        set
        {
            if (_marked == value) return;
            _marked = value;
            OnChanged();
            Group.Refresh();
        }
    }

    /// <summary>Превью грузится лениво, в фоне, только когда карточка видна.</summary>
    public BitmapSource? Thumb
    {
        get
        {
            if (!_thumbRequested)
            {
                _thumbRequested = true;
                var path = FullPath;
                Task.Run(() => Thumbs.Load(path, 220)).ContinueWith(t =>
                {
                    _thumb = t.Result;
                    OnChanged(nameof(Thumb));
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            return _thumb;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
