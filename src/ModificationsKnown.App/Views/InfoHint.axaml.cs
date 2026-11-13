using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
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

    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<InfoHint, string?>(nameof(Label));

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
        ApplyLabel();
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>When set, the badge becomes an inline link-style label
    /// (e.g. “为什么？”) instead of a round “?” badge.</summary>
    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty && HintText is not null)
            HintText.Text = change.GetNewValue<string>();
        else if (change.Property == LabelProperty)
            ApplyLabel();
    }

    private void ApplyLabel()
    {
        if (BadgeText is null || Badge is null) return;

        var label = Label;
        if (string.IsNullOrWhiteSpace(label))
        {
            BadgeText.Text = "?";
            BadgeText.FontSize = 10;
            BadgeText.TextDecorations = null;
            Badge.Width = 16;
            Badge.Height = 16;
            Badge.Padding = default;
            Badge.CornerRadius = new Avalonia.CornerRadius(8);
            return;
        }

        BadgeText.Text = label;
        BadgeText.FontSize = 11;
        BadgeText.TextDecorations = TextDecorations.Underline;
        Badge.Width = double.NaN;
        Badge.Height = 18;
        Badge.Padding = new Avalonia.Thickness(2, 0);
        Badge.CornerRadius = new Avalonia.CornerRadius(4);
    }

    private void OnBadgeEnter(object? sender, PointerEventArgs e)
    {
        _closeTimer.Stop();
        HintPopup.IsOpen = true;
    }

    private void OnBadgePressed(object? sender, PointerPressedEventArgs e)
    {
        _closeTimer.Stop();
        HintPopup.IsOpen = true;
        e.Handled = true;
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
