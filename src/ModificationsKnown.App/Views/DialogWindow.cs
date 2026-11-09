using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ModificationsKnown.App.Views;

/// <summary>
/// Borderless modal that covers the owner window: a dimmed, blurred backdrop
/// with one rounded card in the centre. It stays a real Window so the existing
/// ShowDialog&lt;T&gt; call sites and their return values keep working.
/// </summary>
public class DialogWindow : Window
{
    public DialogWindow()
    {
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent;
        TransparencyLevelHint =
        [
            WindowTransparencyLevel.AcrylicBlur,
            WindowTransparencyLevel.Transparent
        ];

        Opened += (_, _) => FitToOwner();
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
}
