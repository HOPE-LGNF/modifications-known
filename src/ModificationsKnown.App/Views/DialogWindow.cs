using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace ModificationsKnown.App.Views;

/// <summary>
/// Borderless modal that covers the owner window: a dimmed, softly blurred
/// backdrop with one rounded card in the centre. The blur is applied to the
/// owner's content instead of a system acrylic backdrop so the radius stays
/// under our control and the dialog can fade/scale in smoothly.
/// </summary>
public class DialogWindow : Window
{
    private const double OwnerBlurRadius = 6;

    private Control? _ownerRoot;
    private IEffect? _ownerEffect;

    public DialogWindow()
    {
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        FitToOwner();
        BlurOwner();
        PrepareTransitions();

        // Let the initial (invisible / slightly scaled) state paint once before
        // flipping it, otherwise the transitions have nothing to animate from.
        DispatcherTimer.RunOnce(AnimateIn, TimeSpan.FromMilliseconds(24));
    }

    protected override void OnClosed(EventArgs e)
    {
        RestoreOwner();
        base.OnClosed(e);
    }

    private void FitToOwner()
    {
        if (Owner is not { } owner) return;

        // PointToScreen returns physical pixels while Width/Height use DIPs, so
        // aligning the client origin keeps the dialog exactly over the owner at
        // any display scaling.
        var origin = owner.PointToScreen(new Point(0, 0));
        Position = new PixelPoint(origin.X, origin.Y);
        Width = owner.ClientSize.Width;
        Height = owner.ClientSize.Height;
    }

    private void BlurOwner()
    {
        if (Owner?.Content is not Control root) return;

        _ownerRoot = root;
        _ownerEffect = root.Effect;
        root.Effect = new BlurEffect { Radius = OwnerBlurRadius };
    }

    private void RestoreOwner()
    {
        if (_ownerRoot is null) return;

        _ownerRoot.Effect = _ownerEffect;
        _ownerRoot = null;
        _ownerEffect = null;
    }

    private void PrepareTransitions()
    {
        var duration = TimeSpan.FromMilliseconds(180);
        var easing = new CubicEaseOut();

        if (this.FindControl<Control>("DialogScrim") is { } scrim)
        {
            scrim.Transitions =
            [
                new DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = duration,
                    Easing = easing
                }
            ];
        }

        if (this.FindControl<Control>("DialogCard") is { } card)
        {
            card.Transitions =
            [
                new DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = duration,
                    Easing = easing
                },
                new TransformOperationsTransition
                {
                    Property = RenderTransformProperty,
                    Duration = duration,
                    Easing = easing
                }
            ];
        }
    }

    private void AnimateIn()
    {
        if (this.FindControl<Control>("DialogScrim") is { } scrim)
            scrim.Opacity = 1;

        if (this.FindControl<Control>("DialogCard") is { } card)
        {
            card.Opacity = 1;
            card.RenderTransform = TransformOperations.Parse("scale(1)");
        }
    }
}
