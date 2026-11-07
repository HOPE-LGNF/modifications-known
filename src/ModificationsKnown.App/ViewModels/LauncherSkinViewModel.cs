using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModificationsKnown.App.Services;
using ModificationsKnown.App.Theming;
using ModificationsKnown.Core.Settings;

namespace ModificationsKnown.App.ViewModels;

public partial class ThemeCardViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => OnPropertyChanged(nameof(IsNotSelected));

    public ThemeCardViewModel(ThemePalette palette, Func<ThemeCardViewModel, Task> apply)
    {
        Palette = palette;
        ApplyCommand = new AsyncRelayCommand(() => apply(this));
        BackgroundBrush = new SolidColorBrush(Color.Parse(palette.Background));
        SurfaceBrush = new SolidColorBrush(Color.Parse(palette.Surface));
        AccentBrush = new SolidColorBrush(Color.Parse(palette.Accent));
        TextBrush = new SolidColorBrush(Color.Parse(palette.TextPrimary));
    }

    public ThemePalette Palette { get; }

    public string Name => Palette.Name;

    public string Description => Palette.Description;

    public string Id => Palette.Id;

    public bool IsNotSelected => !IsSelected;

    public IAsyncRelayCommand ApplyCommand { get; }

    public IBrush BackgroundBrush { get; }

    public IBrush SurfaceBrush { get; }

    public IBrush AccentBrush { get; }

    public IBrush TextBrush { get; }
}

public partial class LauncherSkinViewModel : ObservableObject, IAsyncPage
{
    private readonly IAppShell _shell;
    private readonly IAppSettingsService _settings;

    [ObservableProperty]
    private string _currentThemeName = string.Empty;

    public LauncherSkinViewModel(IAppShell shell, IAppSettingsService settings)
    {
        _shell = shell;
        _settings = settings;

        foreach (var palette in ThemeService.Palettes)
            Themes.Add(new ThemeCardViewModel(palette, ApplyAsync));

        RefreshSelection();
    }

    public ObservableCollection<ThemeCardViewModel> Themes { get; } = [];

    public string Explanation => "主题只改变启动器自身的颜色变量，不会修改游戏、BepInEx 或第三方模组文件。";

    public Task OnActivatedAsync()
    {
        RefreshSelection();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ApplyAsync(ThemeCardViewModel? card)
    {
        if (card is null) return;

        _settings.Current.ThemePreset = card.Id;
        await _settings.SaveAsync();
        ThemeService.Apply(card.Palette);
        RefreshSelection();
        _shell.SetStatus($"已切换到「{card.Name}」主题。", StatusKind.Success);
    }

    private void RefreshSelection()
    {
        var currentId = _settings.Current.ThemePreset;
        foreach (var theme in Themes)
            theme.IsSelected = string.Equals(theme.Id, currentId, StringComparison.OrdinalIgnoreCase);

        CurrentThemeName = Themes.FirstOrDefault(theme => theme.IsSelected)?.Name ?? "苔原";
    }
}

public sealed class GameSkinViewModel
{
    public string Title => "游戏皮肤";

    public string Status => "开发中";

    public string Description =>
        "这里将用于管理游戏内的 Body / Head / Wings 皮肤包，并与 SkinSync 协作。当前版本先把入口留好，不会修改游戏目录。";

    public IReadOnlyList<string> PlannedFeatures { get; } =
    [
        "识别完整皮肤目录结构，而不是只复制 Body",
        "安装前预览缩略图与文件清单",
        "启用/停用并把冲突明确显示出来",
        "与 BepInEx 插件状态分开管理"
    ];
}
