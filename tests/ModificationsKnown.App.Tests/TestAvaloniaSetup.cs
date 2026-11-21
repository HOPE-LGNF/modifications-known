using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;

[assembly: AvaloniaTestApplication(typeof(ModificationsKnown.App.Tests.TestAvaloniaSetup))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerTest)]

namespace ModificationsKnown.App.Tests;

/// <summary>
/// Boots the real <see cref="ModificationsKnown.App.App"/> so tests see the
/// production App.axaml resources and styles, but never goes through
/// Program.Main: no single-instance service, no startup scan.
///
/// The headless session provides no classic desktop lifetime, so the production
/// OnFrameworkInitializationCompleted (settings, logging, Steam/Nexus services,
/// shell) returns before constructing anything. Do not replace this with
/// Program.BuildAvaloniaApp(): that starts a real desktop platform.
/// </summary>
public static class TestAvaloniaSetup
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<ModificationsKnown.App.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
}
