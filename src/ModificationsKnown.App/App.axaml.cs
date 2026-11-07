using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ModificationsKnown.App.Services;
using ModificationsKnown.App.Theming;
using ModificationsKnown.App.ViewModels;
using ModificationsKnown.App.Views;
using ModificationsKnown.Infrastructure.BepInEx;
using ModificationsKnown.Infrastructure.Diagnostics;
using ModificationsKnown.Infrastructure.Games;
using ModificationsKnown.Infrastructure.Installation;
using ModificationsKnown.Infrastructure.Network;
using ModificationsKnown.Infrastructure.Nexus;
using ModificationsKnown.Infrastructure.Storage;

namespace ModificationsKnown.App;

public class App : Application
{
    private NexusModsClient? _nexusClient;
    private HttpFileDownloader? _downloader;
    private ThumbnailService? _thumbnails;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        var logger = new FileAppLogger();
        logger.Info("Starting Modifications Known");

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            logger.Error("Unhandled exception", args.ExceptionObject as Exception);

        var settings = new JsonAppSettingsService();
        settings.LoadAsync().GetAwaiter().GetResult();
        ThemeService.LoadCustomThemes(logger);
        ThemeService.Apply(settings.Current.ThemePreset);
        logger.Info("Settings loaded; creating shell");

        _nexusClient = new NexusModsClient(() => settings.Current.NexusApiKey, logger);
        _downloader = new HttpFileDownloader();
        _thumbnails = new ThumbnailService(logger);

        var locator = new SteamGameLocator(settings, logger);
        var modManager = new BepInExModManager(logger);
        var installer = new ArchiveModInstaller(logger);
        var launcher = new GameLauncher(logger);
        var dialogs = new UserDialogService(() => desktop.MainWindow);
        var filePicker = new FilePickerService(() => desktop.MainWindow);

        var shell = new ShellViewModel(
            settings,
            logger,
            locator,
            _nexusClient,
            modManager,
            installer,
            _downloader,
            launcher,
            dialogs,
            filePicker,
            _thumbnails);

        var window = new MainWindow
        {
            DataContext = shell
        };

        desktop.MainWindow = window;
        logger.Info("Main window created");
        desktop.Exit += (_, _) =>
        {
            _nexusClient.Dispose();
            _downloader.Dispose();
            _thumbnails.Dispose();
        };

        window.Opened += async (_, _) =>
        {
            logger.Info("Main window opened");
            await shell.InitializeAsync();
        };

        base.OnFrameworkInitializationCompleted();
    }
}
