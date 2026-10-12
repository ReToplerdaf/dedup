using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace PhotoCommander.Views;

/// <summary>Краткая справка: окно по F1 и подсказка в полноэкранном просмотре.</summary>
public static class Help
{
    public sealed record Section(string Title, string[] Lines);

    public static readonly Section QuickStart = new("Как пользоваться — коротко", new[]
    {
        "1. В ЛЕВОЙ панели откройте папку с фото. Панель с яркой рамкой — источник, вторая — куда.",
        "2. Нажмите «Найти похожие». Будут найдены копии, уменьшенные/пересжатые версии и серии кадров.",
        "3. В окне результатов: красная рамка — фото уйдёт в дубликаты, зелёная — останется. Лучшая копия выбрана сама.",
        "4. Двойной щелчок по фото — просмотр на весь экран: можно увеличить и сравнить два кадра рядом.",
        "5. «Переместить отмеченные…» → «Да» — дубликаты переедут в папку _duplicates. Ничего не удаляется насовсем.",
        "6. Разложить по датам: во второй панели откройте папку назначения, выберите шаблон и нажмите «Разложить по датам».",
    });

    public static readonly Section PaneKeys = new("Панели", new[]
    {
        "Tab — другая панель      Enter — открыть      Backspace — вверх",
        "F5 — копировать      F6 — переместить      F7 — новая папка      F8 / Del — в Корзину",
        "Ctrl / Shift + щелчок — выделить несколько      буквы — быстрый поиск по имени      Ctrl+R — обновить",
    });

    public static readonly Section ViewerKeys = new("Просмотр на весь экран", new[]
    {
        "← →  — предыдущее / следующее фото          ↑ ↓ или PgUp PgDn — предыдущая / следующая группа",
        "Пробел — в дубликаты / оставить          K или Enter — «★ Оставить это» (остальные — в дубликаты)",
        "C — сравнить два фото рядом          Tab или щелчок — какая половина активна",
        "Колесо мыши, + / − — масштаб          перетаскивание — сдвиг          0 — целиком          1 — 1:1",
        "Двойной щелчок — целиком ↔ 1:1          O — открыть в другой программе          F11 — окно / весь экран",
        "Esc — выход",
    });

    public static readonly Section Tips = new("Советы", new[]
    {
        "Порог сходства 4–8 подходит почти всегда. При 10 и больше начинают попадаться просто похожие сюжеты.",
        "Серии кадров: окно 10 секунд и мягкий порог 14 находят дубли, снятые подряд.",
        "Сначала переместите дубликаты в _duplicates, проверьте, и только потом удаляйте эту папку.",
    });

    public static readonly Section[] All = { QuickStart, PaneKeys, ViewerKeys, Tips };

    /// <summary>Текстовый блок с разделами справки.</summary>
    public static Control Build(IEnumerable<Section> sections, IBrush header, IBrush text, double fontSize = 14)
    {
        var panel = new StackPanel { Spacing = 4 };
        foreach (var s in sections)
        {
            panel.Children.Add(new TextBlock
            {
                Text = s.Title, FontWeight = FontWeight.Bold, FontSize = fontSize + 2, Foreground = header,
                Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 12, 0, 2),
            });
            foreach (var line in s.Lines)
                panel.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, Foreground = text, FontSize = fontSize });
        }
        return panel;
    }

    /// <summary>Окно справки (F1).</summary>
    public static Window Show(Window owner)
    {
        var close = new Button { Content = "Понятно", MinWidth = 100, HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true, IsCancel = true };
        var root = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(close, Dock.Bottom);
        close.Margin = new Thickness(0, 12, 0, 0);
        root.Children.Add(close);
        root.Children.Add(new ScrollViewer
        {
            Content = Build(All, Brush.Parse("#000080"), Brushes.Black),
        });

        var win = new Window
        {
            Title = "Photo Commander — справка",
            Width = 860, Height = 640, MinWidth = 500, MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Content = root,
        };
        close.Click += (_, _) => win.Close();
        win.KeyDown += (_, e) => { if (e.Key is Key.Escape or Key.F1) { win.Close(); e.Handled = true; } };
        win.Show(owner);
        return win;
    }
}
