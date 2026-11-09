using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ModificationsKnown.App.Views;

/// <summary>
/// Full-window image viewer. The image is laid out at its natural size and
/// scaled with min(availableW / w, availableH / h, 1) so square and panoramic
/// images are always complete and letterboxed, never cropped or stretched.
/// Zoom is anchored at the cursor and panning is clamped to the viewport.
/// </summary>
public partial class ImageLightboxWindow : Window
{
    private const double Padding = 24;
    private readonly Bitmap? _bitmap;
    private readonly ScaleTransform _scaleTransform = new();
    private readonly TranslateTransform _translateTransform = new();
    private double _scale = 1;
    private double _fitScale = 1;
    private double _offsetX;
    private double _offsetY;
    private bool _dragging;
    private bool _moved;
    private Point _dragStart;
    private double _dragOffsetX;
    private double _dragOffsetY;

    public ImageLightboxWindow()
        : this(null, string.Empty)
    {
    }

    public ImageLightboxWindow(Bitmap? bitmap, string caption)
    {
        _bitmap = bitmap;
        InitializeComponent();

        CaptionText.Text = caption;
        if (bitmap is null) return;

        LightboxImage.Source = bitmap;
        LightboxImage.Width = bitmap.Size.Width;
        LightboxImage.Height = bitmap.Size.Height;
        LightboxImage.RenderTransform = new TransformGroup
        {
            Children = { _scaleTransform, _translateTransform }
        };

        KeyDown += OnWindowKeyDown;
        Opened += (_, _) =>
        {
            FitToOwner();
            Root.Transitions =
            [
                new DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(150),
                    Easing = new CubicEaseOut()
                }
            ];
            DispatcherTimer.RunOnce(() => Root.Opacity = 1, TimeSpan.FromMilliseconds(16));
        };
    }

    private void FitToOwner()
    {
        if (Owner is not { } owner) return;

        var origin = owner.PointToScreen(new Point(0, 0));
        Position = new PixelPoint(origin.X, origin.Y);
        Width = owner.ClientSize.Width;
        Height = owner.ClientSize.Height;
        Fit();
    }

    private void Fit()
    {
        if (_bitmap is null) return;

        var width = _bitmap.Size.Width;
        var height = _bitmap.Size.Height;
        if (width <= 0 || height <= 0) return;

        var availableWidth = Math.Max(1, Viewport.Bounds.Width - Padding * 2);
        var availableHeight = Math.Max(1, Viewport.Bounds.Height - Padding * 2);
        _fitScale = Math.Min(1, Math.Min(availableWidth / width, availableHeight / height));
        if (_fitScale <= 0) _fitScale = 0.01;

        _scale = _fitScale;
        _offsetX = (Viewport.Bounds.Width - width * _scale) / 2;
        _offsetY = (Viewport.Bounds.Height - height * _scale) / 2;
        Apply();
    }

    private void Apply()
    {
        if (_bitmap is null) return;

        _scaleTransform.ScaleX = _scale;
        _scaleTransform.ScaleY = _scale;
        _translateTransform.X = _offsetX;
        _translateTransform.Y = _offsetY;
        ZoomText.Text = $"{_scale * 100:0}%";
    }

    private void Clamp()
    {
        if (_bitmap is null) return;

        var scaledWidth = _bitmap.Size.Width * _scale;
        var scaledHeight = _bitmap.Size.Height * _scale;
        var viewWidth = Viewport.Bounds.Width;
        var viewHeight = Viewport.Bounds.Height;

        _offsetX = scaledWidth <= viewWidth
            ? (viewWidth - scaledWidth) / 2
            : Math.Min(0, Math.Max(viewWidth - scaledWidth, _offsetX));

        _offsetY = scaledHeight <= viewHeight
            ? (viewHeight - scaledHeight) / 2
            : Math.Min(0, Math.Max(viewHeight - scaledHeight, _offsetY));
    }

    private void OnViewportSizeChanged(object? sender, SizeChangedEventArgs e) => Fit();

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_bitmap is null) return;

        var target = Math.Clamp(_scale * Math.Pow(1.15, e.Delta.Y), _fitScale, 1.0);
        if (Math.Abs(target - _scale) < 0.0001) return;

        var position = e.GetPosition(Viewport);
        var imageX = (position.X - _offsetX) / _scale;
        var imageY = (position.Y - _offsetY) / _scale;
        _scale = target;
        _offsetX = position.X - imageX * _scale;
        _offsetY = position.Y - imageY * _scale;
        Clamp();
        Apply();
        e.Handled = true;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Viewport).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        _moved = false;
        _dragStart = e.GetPosition(Viewport);
        _dragOffsetX = _offsetX;
        _dragOffsetY = _offsetY;
        e.Pointer.Capture(Viewport);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return;

        var position = e.GetPosition(Viewport);
        var deltaX = position.X - _dragStart.X;
        var deltaY = position.Y - _dragStart.Y;
        if (Math.Abs(deltaX) + Math.Abs(deltaY) > 2) _moved = true;

        _offsetX = _dragOffsetX + deltaX;
        _offsetY = _dragOffsetY + deltaY;
        Clamp();
        Apply();
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        e.Pointer.Capture(null);

        // A click on the dark backdrop closes; a click on the image does not.
        if (!_moved && e.Source is not Image)
            Close();
    }

    private void OnResetClick(object? sender, RoutedEventArgs e) => Fit();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
