using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace PhotoCommander.Views;

public enum DialogButtons { Ok, OkCancel, YesNo, YesNoCancel }
public enum DialogResult { None, Ok, Cancel, Yes, No }
public enum DialogIcon { None, Info, Question, Warning, Error }

/// <summary>Простые модальные окна: сообщение с кнопками и ввод строки.</summary>
public static class Dialogs
{
    public static Task<DialogResult> Show(Window? owner, string text, string title,
        DialogButtons buttons = DialogButtons.Ok, DialogIcon icon = DialogIcon.None)
    {
        var choices = buttons switch
        {
            DialogButtons.OkCancel => new[] { (DialogResult.Ok, "OK"), (DialogResult.Cancel, "Отмена") },
            DialogButtons.YesNo => new[] { (DialogResult.Yes, "Да"), (DialogResult.No, "Нет") },
            DialogButtons.YesNoCancel => new[] { (DialogResult.Yes, "Да"), (DialogResult.No, "Нет"), (DialogResult.Cancel, "Отмена") },
            _ => new[] { (DialogResult.Ok, "OK") },
        };
        // Escape — «Отмена», если её нет — «Нет», иначе единственная кнопка
        var escape = choices.Any(c => c.Item1 == DialogResult.Cancel) ? DialogResult.Cancel
                   : choices.Any(c => c.Item1 == DialogResult.No) ? DialogResult.No
                   : choices[0].Item1;

        var (glyph, color) = icon switch
        {
            DialogIcon.Info => ("i", "#2060C0"),
            DialogIcon.Question => ("?", "#2060C0"),
            DialogIcon.Warning => ("!", "#C08000"),
            DialogIcon.Error => ("×", "#C02020"),
            _ => ("", ""),
        };

        var win = NewWindow(title);
        var result = DialogResult.None;

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6 };
        Button? first = null;
        foreach (var (res, label) in choices)
        {
            var b = new Button { Content = label, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            b.Click += (_, _) => { result = res; win.Close(); };
            row.Children.Add(b);
            first ??= b;
        }

        var body = new DockPanel { Margin = new Thickness(16), LastChildFill = true };
        DockPanel.SetDock(row, Dock.Bottom);
        row.Margin = new Thickness(0, 16, 0, 0);
        body.Children.Add(row);
        if (glyph != "")
        {
            var badge = new Border
            {
                Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Margin = new Thickness(0, 0, 12, 0),
                Background = Brush.Parse(color), VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock
                {
                    Text = glyph, Foreground = Brushes.White, FontWeight = FontWeight.Bold, FontSize = 18,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            DockPanel.SetDock(badge, Dock.Left);
            body.Children.Add(badge);
        }
        body.Children.Add(new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 560, VerticalAlignment = VerticalAlignment.Center });

        win.Content = body;
        win.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { result = escape; win.Close(); e.Handled = true; }
            else if (e.Key == Key.Enter && e.Source is not Button) { result = choices[0].Item1; win.Close(); e.Handled = true; }
        };
        win.Opened += (_, _) => first?.Focus();

        return ShowAsync(win, owner, () => result == DialogResult.None ? escape : result);
    }

    /// <summary>Окно ввода строки. null — пользователь отказался.</summary>
    public static Task<string?> Ask(Window? owner, string title, string prompt, string initial = "")
    {
        var win = NewWindow(title);
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 10), MinWidth = 320 };
        var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Отмена", MinWidth = 80, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        bool accepted = false;
        ok.Click += (_, _) => { accepted = true; win.Close(); };
        cancel.Click += (_, _) => win.Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6 };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        win.Content = panel;
        win.Opened += (_, _) => { box.Focus(); box.SelectAll(); };

        return ShowAsync(win, owner, () => accepted ? box.Text?.Trim() : null);
    }

    static Window NewWindow(string title) => new()
    {
        Title = title,
        SizeToContent = SizeToContent.WidthAndHeight,
        CanResize = false,
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        MinWidth = 300,
    };

    static async Task<T> ShowAsync<T>(Window win, Window? owner, Func<T> result)
    {
        if (owner != null && owner.IsVisible)
        {
            await win.ShowDialog(owner);
        }
        else
        {
            var closed = new TaskCompletionSource();
            win.Closed += (_, _) => closed.TrySetResult();
            win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            win.Show();
            await closed.Task;
        }
        return result();
    }
}
