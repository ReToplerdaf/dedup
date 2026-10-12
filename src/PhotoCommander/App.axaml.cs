using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using PhotoCommander.Views;

namespace PhotoCommander;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            args.Handled = true;
            var owner = (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            _ = Dialogs.Show(owner, args.Exception.Message, "Photo Commander — ошибка", DialogButtons.Ok, DialogIcon.Error);
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();

        base.OnFrameworkInitializationCompleted();
    }
}
