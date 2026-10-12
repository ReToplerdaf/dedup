using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PhotoCommander.Controls;
using PhotoCommander.Services;

namespace PhotoCommander.Views;

/// <summary>
/// Просмотр похожих фото на весь экран: по одному или два рядом для сравнения,
/// с синхронным масштабом. Отметки сразу видны в окне со списком групп.
/// </summary>
public sealed class ViewerWindow : Window
{
    static readonly IBrush MarkedBrush = Brush.Parse("#FF5555");
    static readonly IBrush KeptBrush = Brush.Parse("#55FF55");
    static readonly IBrush BarBrush = Brush.Parse("#000080");
    static readonly IBrush TextBrush = Brush.Parse("#00FFFF");
    static readonly IBrush YellowBrush = Brush.Parse("#FFFF55");

    /// <summary>Сколько фото держать загруженными (текущие и соседние).</summary>
    const int CacheSize = 8;
    /// <summary>Больше этого по ширине не грузим — экономия памяти, деталей хватает.</summary>
    const int MaxLoadWidth = 6000;

    readonly IList<DupGroupVm> _groups;
    readonly ViewState _view = new();
    readonly Pane _left, _right;
    readonly Grid _panes;
    readonly TextBlock _groupInfo;
    readonly Border _helpOverlay;
    readonly Dictionary<string, Task<Bitmap?>> _cache = new();
    readonly LinkedList<string> _lru = new();

    int _groupIndex;
    bool _compare;
    bool _rightActive;

    public ViewerWindow() : this(new List<DupGroupVm>(), null, null) { }

    public ViewerWindow(IList<DupGroupVm> groups, DupGroupVm? group, DupItemVm? item)
    {
        _groups = groups;
        Title = "Похожие фото — просмотр";
        Background = Brushes.Black;
        WindowState = WindowState.FullScreen;
        Width = 1280;
        Height = 800;

        _left = new Pane(this, _view);
        _right = new Pane(this, _view);
        _panes = new Grid();

        _groupInfo = new TextBlock { Foreground = YellowBrush, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        var top = new DockPanel { Background = BarBrush };
        var hintTop = new TextBlock
        {
            Text = "F1 — справка    Esc — выход", Foreground = TextBrush, Margin = new Thickness(8, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(hintTop, Dock.Right);
        top.Children.Add(hintTop);
        _groupInfo.Margin = new Thickness(8, 4);
        top.Children.Add(_groupInfo);

        var bottom = new TextBlock
        {
            Background = Brushes.Black, Foreground = TextBrush, Padding = new Thickness(8, 4),
            FontFamily = (FontFamily)Application.Current!.FindResource("NcMono")!,
            Text = "← → Фото   ↑ ↓ Группа   Пробел В дубликаты/оставить   K Оставить это   C Сравнить   Колесо Масштаб   0 Целиком   1 1:1   O Открыть",
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _helpOverlay = new Border
        {
            IsVisible = false,
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0, 0, 0x50)),
            BorderBrush = TextBrush, BorderThickness = new Thickness(1),
            Padding = new Thickness(24), Margin = new Thickness(60),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 1000,
            Child = Help.Build(new[] { Help.ViewerKeys }, YellowBrush, Brushes.White, 16),
        };

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(top, 0);
        Grid.SetRow(_panes, 1);
        Grid.SetRow(bottom, 2);
        Grid.SetRow(_helpOverlay, 1);
        root.Children.Add(top);
        root.Children.Add(_panes);
        root.Children.Add(bottom);
        root.Children.Add(_helpOverlay);
        Content = root;

        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        Closed += (_, _) => { _left.Show(null); _right.Show(null); DisposeCache(); };

        _groupIndex = group != null ? Math.Max(0, _groups.IndexOf(group)) : 0;
        ShowGroup(item);
    }

    // ---------- что показываем ----------

    DupGroupVm? Group => _groupIndex >= 0 && _groupIndex < _groups.Count ? _groups[_groupIndex] : null;

    /// <summary>Сейчас активное фото (на нём работают Пробел, K, O, ← →).</summary>
    public DupItemVm? Current => _compare && _rightActive ? _right.Item : _left.Item;
    public DupItemVm? LeftItem => _left.Item;
    public DupItemVm? RightItem => _compare ? _right.Item : null;
    public bool IsCompare => _compare;
    public bool IsHelpVisible => _helpOverlay.IsVisible;
    public ViewState View => _view;

    void ShowGroup(DupItemVm? first = null)
    {
        var g = Group;
        if (g == null || g.Items.Count == 0) return;
        first ??= g.Items.FirstOrDefault(i => i.IsBest) ?? g.Items[0];
        if (g.Items.Count < 2) _compare = false;

        _view.Reset();
        _left.Show(first);
        _right.Show(_compare ? g.Items.FirstOrDefault(i => i != first) : null);
        _rightActive = _compare;
        Layout();
        Preload();
    }

    void Layout()
    {
        _panes.Children.Clear();
        _panes.ColumnDefinitions = new ColumnDefinitions(_compare ? "*,*" : "*");
        Grid.SetColumn(_left, 0);
        _panes.Children.Add(_left);
        if (_compare)
        {
            Grid.SetColumn(_right, 1);
            _panes.Children.Add(_right);
        }
        UpdateInfo();
    }

    internal void UpdateInfo()
    {
        var g = Group;
        if (g == null) return;
        int pos = Current == null ? 0 : g.Items.IndexOf(Current) + 1;
        int marked = g.Items.Count(i => i.Marked);
        string warn = marked == g.Items.Count ? "   ⚠ отмечены ВСЕ — не останется ни одного" : "";
        _groupInfo.Text = $"Группа {_groupIndex + 1} из {_groups.Count}   ·   фото {pos} из {g.Items.Count}   ·   в дубликаты: {marked}" +
                          (_compare ? "   ·   СРАВНЕНИЕ" : "") + warn;
        _left.SetActive(!_compare || !_rightActive);
        _right.SetActive(_compare && _rightActive);
    }

    // ---------- навигация ----------

    /// <summary>Соседнее фото на активной половине. В одиночном режиме на краю группы — в соседнюю группу.</summary>
    public void Step(int dir)
    {
        var g = Group;
        var cur = Current;
        if (g == null || cur == null) return;
        int i = g.Items.IndexOf(cur) + dir;

        if (!_compare)
        {
            if (i < 0 || i >= g.Items.Count)
            {
                int gi = _groupIndex + dir;
                if (gi < 0 || gi >= _groups.Count) return;
                _groupIndex = gi;
                var ng = _groups[gi];
                ShowGroup(dir > 0 ? ng.Items[0] : ng.Items[^1]);
                return;
            }
            _left.Show(g.Items[i]);
        }
        else
        {
            var activePane = _rightActive ? _right : _left;
            var other = _rightActive ? _left : _right;
            i = (i + g.Items.Count) % g.Items.Count;
            if (g.Items[i] == other.Item)
            {
                if (g.Items.Count == 2) { Swap(); return; }
                i = (i + dir + g.Items.Count) % g.Items.Count;
            }
            activePane.Show(g.Items[i]);
        }
        UpdateInfo();
        Preload();
    }

    void Swap()
    {
        var l = _left.Item; var r = _right.Item;
        _left.Show(r);
        _right.Show(l);
        UpdateInfo();
    }

    public void StepGroup(int dir)
    {
        int gi = _groupIndex + dir;
        if (gi < 0 || gi >= _groups.Count) return;
        _groupIndex = gi;
        ShowGroup();
    }

    public void ToggleCompare()
    {
        var g = Group;
        if (g == null) return;
        if (!_compare && g.Items.Count < 2) return;
        var cur = Current;
        _compare = !_compare;
        if (_compare)
        {
            // слева — то, что смотрели, справа — следующее в группе
            int i = cur == null ? 0 : g.Items.IndexOf(cur);
            _left.Show(cur);
            _right.Show(g.Items[(i + 1) % g.Items.Count]);
            _rightActive = true;
        }
        else
        {
            _left.Show(cur);
            _right.Show(null);
        }
        Layout();
        Preload();
    }

    public void SetActiveSide(bool right)
    {
        if (!_compare) return;
        _rightActive = right;
        UpdateInfo();
    }

    // ---------- отметки ----------

    public void ToggleMark()
    {
        if (Current is { } c) c.Marked = !c.Marked;
    }

    public void KeepCurrent()
    {
        if (Current is not { } c) return;
        foreach (var i in c.Group.Items) i.Marked = !ReferenceEquals(i, c);
    }

    // ---------- клавиатура ----------

    void OnKey(object? sender, KeyEventArgs e)
    {
        if (_helpOverlay.IsVisible && e.Key is Key.Escape or Key.F1 or Key.H)
        {
            _helpOverlay.IsVisible = false;
            e.Handled = true;
            return;
        }

        var activeImage = _compare && _rightActive ? _right.Image : _left.Image;
        e.Handled = true;
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.F1: case Key.H: _helpOverlay.IsVisible = true; break;
            case Key.Right: Step(+1); break;
            case Key.Left: Step(-1); break;
            case Key.Down: case Key.PageDown: StepGroup(+1); break;
            case Key.Up: case Key.PageUp: StepGroup(-1); break;
            case Key.Space: ToggleMark(); break;
            case Key.K: case Key.Enter: KeepCurrent(); break;
            case Key.C: ToggleCompare(); break;
            case Key.Tab: SetActiveSide(!_rightActive); break;
            case Key.OemPlus: case Key.Add: activeImage.ZoomBy(1.25); break;
            case Key.OemMinus: case Key.Subtract: activeImage.ZoomBy(0.8); break;
            case Key.D0: case Key.NumPad0: _view.Reset(); break;
            case Key.D1: case Key.NumPad1: activeImage.ZoomAt(new Point(activeImage.Bounds.Width / 2, activeImage.Bounds.Height / 2), activeImage.ActualSizeZoom); break;
            case Key.O: if (Current is { } c) FilePane.OpenFile(this, c.FullPath); break;
            case Key.F11: WindowState = WindowState == WindowState.FullScreen ? WindowState.Maximized : WindowState.FullScreen; break;
            default: e.Handled = false; break;
        }
    }

    // ---------- загрузка картинок ----------

    internal Task<Bitmap?> Load(string path)
    {
        if (_cache.TryGetValue(path, out var t))
        {
            _lru.Remove(path);
            _lru.AddFirst(path);
            return t;
        }
        t = Task.Run(() => Thumbs.Load(path, MaxLoadWidth));
        _cache[path] = t;
        _lru.AddFirst(path);
        while (_lru.Count > CacheSize)
        {
            var old = _lru.Last!.Value;
            _lru.RemoveLast();
            if (old == _left.Item?.FullPath || old == _right.Item?.FullPath) { _lru.AddFirst(old); break; }
            if (_cache.Remove(old, out var gone)) gone.ContinueWith(x => { if (x.IsCompletedSuccessfully) x.Result?.Dispose(); });
        }
        return t;
    }

    /// <summary>Заранее подгрузить соседние фото, чтобы листалось без задержки.</summary>
    void Preload()
    {
        var g = Group;
        if (g == null || Current is not { } cur) return;
        int i = g.Items.IndexOf(cur);
        foreach (var k in new[] { i + 1, i - 1 })
            if (k >= 0 && k < g.Items.Count) _ = Load(g.Items[k].FullPath);
    }

    void DisposeCache()
    {
        foreach (var t in _cache.Values)
            t.ContinueWith(x => { if (x.IsCompletedSuccessfully) x.Result?.Dispose(); });
        _cache.Clear();
        _lru.Clear();
    }

    // ---------- половина экрана с одним фото ----------

    sealed class Pane : Border
    {
        readonly ViewerWindow _owner;
        readonly TextBlock _title = new() { FontWeight = FontWeight.Bold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly TextBlock _details = new() { Foreground = TextBrush, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly TextBlock _badge = new() { FontWeight = FontWeight.Bold, Padding = new Thickness(6, 1), VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock _loading = new()
        {
            Text = "Загрузка…", Foreground = Brushes.Gray, FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        readonly Border _header;
        int _ticket;

        public ZoomImage Image { get; } = new();
        public DupItemVm? Item { get; set; }

        public Pane(ViewerWindow owner, ViewState view)
        {
            _owner = owner;
            Image.State = view;
            BorderThickness = new Thickness(3);
            Margin = new Thickness(2);

            var badgeRow = new DockPanel();
            DockPanel.SetDock(_badge, Dock.Right);
            badgeRow.Children.Add(_badge);
            badgeRow.Children.Add(_title);
            var head = new StackPanel { Spacing = 1 };
            head.Children.Add(badgeRow);
            head.Children.Add(_details);
            _header = new Border { Padding = new Thickness(8, 4), Child = head, Background = Brush.Parse("#101060") };

            var dock = new DockPanel();
            DockPanel.SetDock(_header, Dock.Top);
            dock.Children.Add(_header);
            var imgHost = new Grid();
            imgHost.Children.Add(_loading);
            imgHost.Children.Add(Image);
            dock.Children.Add(imgHost);
            Child = dock;

            // щелчок по половине делает её активной
            AddHandler(PointerPressedEvent, (_, _) => _owner.SetActiveSide(ReferenceEquals(this, _owner._right)), RoutingStrategies.Tunnel);
        }

        public async void Show(DupItemVm? item)
        {
            if (Item != null) Item.PropertyChanged -= OnItemChanged;
            Item = item;
            int ticket = ++_ticket;
            Image.Source = null;
            if (item == null) return;
            item.PropertyChanged += OnItemChanged;
            Refresh();

            _loading.Text = "Загрузка…";
            _loading.IsVisible = true;
            var bmp = await _owner.Load(item.FullPath);
            if (ticket != _ticket) return;
            _loading.IsVisible = bmp == null;
            _loading.Text = bmp == null ? "Не удалось открыть файл" : "";
            Image.Source = bmp;
        }

        void OnItemChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DupItemVm.Marked)) { Refresh(); _owner.UpdateInfo(); }
        }

        void Refresh()
        {
            if (Item is not { } it) return;
            _title.Text = (_active ? "▶ " : "") + it.Name;
            _details.Text = $"{it.Info}   ·   {it.When}   ·   {it.Reason}   ·   {it.Folder}";
            _badge.Text = (it.IsBest ? "★ ЛУЧШЕЕ   " : "") + (it.Marked ? "В ДУБЛИКАТЫ" : "ОСТАЁТСЯ");
            _badge.Background = it.Marked ? MarkedBrush : KeptBrush;
            _badge.Foreground = Brushes.Black;
            BorderBrush = it.Marked ? MarkedBrush : KeptBrush;
        }

        bool _active;

        public void SetActive(bool active)
        {
            _active = active && _owner._compare;
            Refresh();
            _header.Background = Brush.Parse(active ? "#2020A0" : "#101060");
            Opacity = active || !_owner._compare ? 1 : 0.85;
        }
    }
}
