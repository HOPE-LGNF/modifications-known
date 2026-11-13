using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ModificationsKnown.App.Views;

public partial class CoverImage : UserControl
{
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<CoverImage, IImage?>(nameof(Source));

    public static readonly StyledProperty<double> BlurRadiusProperty =
        AvaloniaProperty.Register<CoverImage, double>(nameof(BlurRadius), 12);

    public CoverImage()
    {
        InitializeComponent();
        Backdrop.Effect = new BlurEffect { Radius = BlurRadius };
    }

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public double BlurRadius
    {
        get => GetValue(BlurRadiusProperty);
        set => SetValue(BlurRadiusProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (Backdrop is null) return;

        if (change.Property == SourceProperty)
        {
            Backdrop.Source = Source;
            Picture.Source = Source;
            Layers.IsVisible = Source is not null;
        }
        else if (change.Property == BlurRadiusProperty)
        {
            Backdrop.Effect = new BlurEffect { Radius = BlurRadius };
        }
    }
}
