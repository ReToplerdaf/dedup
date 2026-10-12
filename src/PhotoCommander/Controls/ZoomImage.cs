using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PhotoCommander.Controls;

/// <summary>
/// Масштаб и положение просмотра. Один объект на несколько <see cref="ZoomImage"/> —
/// и они увеличиваются и двигаются вместе (сравнение двух фото).
/// </summary>
public sealed class ViewState
{
    /// <summary>Во сколько раз крупнее, чем «вписать в экран». 1 — целиком.</summary>
    public double Zoom { get; private set; } = 1;
    /// <summary>Точка картинки в центре экрана, в долях ширины/высоты (0…1).</summary>
    public double Cx { get; private set; } = 0.5;
    public double Cy { get; private set; } = 0.5;

    public event Action? Changed;

    public void Set(double zoom, double cx, double cy)
    {
        Zoom = Math.Clamp(zoom, 1, 64);
        Cx = Zoom <= 1 ? 0.5 : Math.Clamp(cx, 0, 1);
        Cy = Zoom <= 1 ? 0.5 : Math.Clamp(cy, 0, 1);
        Changed?.Invoke();
    }

    public void Reset() => Set(1, 0.5, 0.5);
}

/// <summary>Картинка с масштабом колесом мыши и перетаскиванием.</summary>
public sealed class ZoomImage : Control
{
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<ZoomImage, Bitmap?>(nameof(Source));

    ViewState _state = new();
    Point? _dragFrom;

    static ZoomImage()
    {
        AffectsRender<ZoomImage>(SourceProperty);
    }

    public ZoomImage()
    {
        ClipToBounds = true;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
        _state.Changed += InvalidateVisual;
    }

    public Bitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public ViewState State
    {
        get => _state;
        set
        {
            _state.Changed -= InvalidateVisual;
            _state = value;
            _state.Changed += InvalidateVisual;
            InvalidateVisual();
        }
    }

    /// <summary>Масштаб «вписать»: пикселей экрана на пиксель картинки.</summary>
    double FitScale(Size img) =>
        img.Width <= 0 || img.Height <= 0 ? 1 : Math.Min(Bounds.Width / img.Width, Bounds.Height / img.Height);

    /// <summary>Прямоугольник картинки на экране для текущего масштаба и центра.</summary>
    Rect ImageRect(Size img, double zoom, double cx, double cy)
    {
        double s = FitScale(img) * zoom;
        double w = img.Width * s, h = img.Height * s;
        // картинка меньше окна по какой-то стороне — по ней держим по центру
        double x = w <= Bounds.Width ? (Bounds.Width - w) / 2 : Math.Clamp(Bounds.Width / 2 - cx * w, Bounds.Width - w, 0);
        double y = h <= Bounds.Height ? (Bounds.Height - h) / 2 : Math.Clamp(Bounds.Height / 2 - cy * h, Bounds.Height - h, 0);
        return new Rect(x, y, w, h);
    }

    public override void Render(DrawingContext ctx)
    {
        // прозрачный фон — чтобы мышь «попадала» и мимо картинки
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (Source is not { } bmp) return;
        ctx.DrawImage(bmp, new Rect(bmp.Size), ImageRect(bmp.Size, _state.Zoom, _state.Cx, _state.Cy));
    }

    /// <summary>Изменить масштаб так, чтобы точка под курсором осталась на месте.</summary>
    public void ZoomAt(Point p, double newZoom)
    {
        if (Source is not { } bmp) { _state.Set(newZoom, _state.Cx, _state.Cy); return; }
        var r = ImageRect(bmp.Size, _state.Zoom, _state.Cx, _state.Cy);
        double px = (p.X - r.X) / r.Width, py = (p.Y - r.Y) / r.Height;   // точка картинки под курсором, 0…1

        newZoom = Math.Clamp(newZoom, 1, MaxZoom(bmp.Size));
        double s = FitScale(bmp.Size) * newZoom;
        double w = bmp.Size.Width * s, h = bmp.Size.Height * s;
        double cx = (Bounds.Width / 2 - (p.X - px * w)) / w;
        double cy = (Bounds.Height / 2 - (p.Y - py * h)) / h;
        _state.Set(newZoom, cx, cy);
    }

    public void ZoomBy(double factor) => ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), _state.Zoom * factor);

    /// <summary>Масштаб 1:1 — пиксель картинки на пиксель экрана.</summary>
    public double ActualSizeZoom
    {
        get
        {
            if (Source is not { } bmp) return 1;
            double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            return Math.Max(1, 1 / (FitScale(bmp.Size) * scaling));
        }
    }

    /// <summary>Больше чем 8 экранных пикселей на пиксель фото увеличивать незачем.</summary>
    double MaxZoom(Size img) => Math.Max(1, 8 / FitScale(img));

    // ---------- мышь ----------

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ZoomAt(e.GetPosition(this), _state.Zoom * Math.Pow(1.25, e.Delta.Y));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pt = e.GetCurrentPoint(this);
        if (!pt.Properties.IsLeftButtonPressed) return;

        if (e.ClickCount == 2)
        {
            // двойной щелчок: целиком ↔ 1:1 в этой точке
            if (_state.Zoom > 1.01) _state.Reset();
            else ZoomAt(pt.Position, ActualSizeZoom > 1.01 ? ActualSizeZoom : 3);
            return;
        }
        _dragFrom = pt.Position;
        e.Pointer.Capture(this);
        Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragFrom is not { } from || Source is not { } bmp) return;
        var p = e.GetPosition(this);
        // считаем от того, что реально видно (у края картинка упирается), — без «мёртвой зоны»
        var r = ImageRect(bmp.Size, _state.Zoom, _state.Cx, _state.Cy);
        double cx = (Bounds.Width / 2 - r.X) / r.Width, cy = (Bounds.Height / 2 - r.Y) / r.Height;
        _state.Set(_state.Zoom, cx - (p.X - from.X) / r.Width, cy - (p.Y - from.Y) / r.Height);
        _dragFrom = p;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragFrom == null) return;
        _dragFrom = null;
        e.Pointer.Capture(null);
        Cursor = Cursor.Default;
    }
}
