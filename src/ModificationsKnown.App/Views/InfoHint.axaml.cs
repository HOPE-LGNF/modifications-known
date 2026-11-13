using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace ModificationsKnown.App.Views;

/// <summary>
/// Small “?” badge with a styled hover card instead of the platform tooltip.
/// The card stays open for a short grace period after the pointer leaves, and a
/// move onto the card itself keeps it open.
/// </summary>
public partial class InfoHint : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<InfoHint, string>(nameof(Text), string.Empty);

    private readonly DispatcherTimer _closeTimer;
    private bool _pointerOnPopup;

    public InfoHint()
    {
        InitializeComponent();
        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            if (!_pointerOnPopup) HintPopup.IsOpen = false;
        };
        HintText.Text = Text;
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty && HintText is not null)
            HintText.Text = change.GetNewValue<string>();
    }

    private void OnBadgeEnter(object? sender, PointerEventArgs e)
    {
        _closeTimer.Stop();
        HintPopup.IsOpen = true;
    }

    private void OnBadgeLeave(object? sender, PointerEventArgs e) => ScheduleClose();

    private void OnPopupEnter(object? sender, PointerEventArgs e)
    {
        _pointerOnPopup = true;
        _closeTimer.Stop();
    }

    private void OnPopupLeave(object? sender, PointerEventArgs e)
    {
        _pointerOnPopup = false;
        ScheduleClose();
    }

    private void ScheduleClose()
    {
        _closeTimer.Stop();
        _closeTimer.Start();
    }
}
