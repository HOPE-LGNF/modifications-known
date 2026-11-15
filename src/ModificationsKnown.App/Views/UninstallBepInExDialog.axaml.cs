using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ModificationsKnown.Core.Games;

namespace ModificationsKnown.App.Views;

public partial class UninstallBepInExDialog : DialogWindow
{
    private readonly GameInstallation _installation;
    private DispatcherTimer? _timer;
    private int _seconds;

    public UninstallBepInExDialog()
        : this(new GameInstallation(
            new GameProfile("placeholder", "版本", "CU", GameEdition.Full, "0", "scavprototype", "CasualtiesUnknown_Data"),
            string.Empty, null, false, false))
    {
    }

    public UninstallBepInExDialog(GameInstallation installation)
    {
        _installation = installation;
        InitializeComponent();
        InstanceLabel.Text = installation.DisplayLabel;
        InstancePath.Text = installation.GameDirectory;
    }

    private void OnAcknowledgeChanged(object? sender, RoutedEventArgs e) =>
        ContinueButton.IsEnabled = AcknowledgeCheck.IsChecked == true;

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnContinueClick(object? sender, RoutedEventArgs e)
    {
        SummaryText.Text =
            $"实例：{_installation.DisplayLabel}\n目录：{_installation.GameDirectory}\n\n" +
            "将永久删除 BepInEx/、winhttp.dll、doorstop_config.ini、.doorstop_version，以及此实例下的所有模组、插件和配置。";
        StepOnePanel.IsVisible = false;
        StepTwoPanel.IsVisible = true;
        StartCountdown();
    }

    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        StopCountdown();
        StepTwoPanel.IsVisible = false;
        StepOnePanel.IsVisible = true;
    }

    private void StartCountdown()
    {
        StopCountdown();
        _seconds = 5;
        ConfirmButton.IsEnabled = false;
        ConfirmButton.Content = $"确认卸载（{_seconds}）";
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _seconds--;
            if (_seconds > 0)
            {
                ConfirmButton.Content = $"确认卸载（{_seconds}）";
                return;
            }

            StopCountdown();
            ConfirmButton.IsEnabled = true;
            ConfirmButton.Content = "确认卸载";
        };
        _timer.Start();
    }

    private void StopCountdown()
    {
        _timer?.Stop();
        _timer = null;
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        StopCountdown();
        Close(true);
    }
}
