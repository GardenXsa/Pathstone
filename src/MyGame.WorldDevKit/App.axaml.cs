using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MyGame.WorldDevKit.ViewModels;
using MyGame.WorldDevKit.Views;

namespace MyGame.WorldDevKit;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(),
                Title = "Pathstone WorldDevKit",
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
