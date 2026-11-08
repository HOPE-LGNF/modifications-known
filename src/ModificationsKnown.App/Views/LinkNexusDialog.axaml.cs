using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ModificationsKnown.App.Services;
using ModificationsKnown.Core.Nexus;

namespace ModificationsKnown.App.Views;

public partial class LinkNexusDialog : Window
{
    private readonly INexusModsClient _client;
    private readonly string _gameDomain;

    // Avalonia's XAML loader requires a public parameterless constructor for
    // the compiled resource; normal code uses the DI-style constructor below.
    public LinkNexusDialog()
        : this(null!, "scavprototype", string.Empty)
    {
    }

    public LinkNexusDialog(INexusModsClient client, string gameDomain, string suggestedName)
    {
        _client = client;
        _gameDomain = gameDomain;
        InitializeComponent();

        SearchBox.Text = suggestedName;
        Opened += async (_, _) =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            await SearchAsync();
        };
        KeyDown += OnWindowKeyDown;
    }

    private async void OnSearchClick(object? sender, RoutedEventArgs e) => await SearchAsync();

    private async void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await SearchAsync();
    }

    private async Task SearchAsync()
    {
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        StatusText.Text = "正在搜索…";
        try
        {
            var result = await _client.SearchModsAsync(
                _gameDomain,
                new NexusSearchQuery(query, 1, 30, NexusModSort.Endorsements, IncludeAdult: false));
            Results.ItemsSource = result.Items;
            StatusText.Text = result.Items.Count == 0
                ? "没有找到匹配的模组。"
                : $"找到 {result.TotalCount} 个结果；请选择最接近的一个。";
        }
        catch (Exception exception)
        {
            Results.ItemsSource = null;
            StatusText.Text = $"搜索失败：{exception.Message}";
        }
    }

    private void OnResultDoubleTapped(object? sender, TappedEventArgs e) => AcceptSelected();

    private void OnLinkClick(object? sender, RoutedEventArgs e) => AcceptSelected();

    private void AcceptSelected()
    {
        if (Results.SelectedItem is not NexusModSummary selected)
        {
            StatusText.Text = "请先选择一个模组。";
            return;
        }

        Close(new NexusMatch(selected.Name, selected.GameScopedId, selected.Id));
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close(null);
    }
}
