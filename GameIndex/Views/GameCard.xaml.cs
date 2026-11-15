using System.ComponentModel;
using GameIndex.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace GameIndex.Views;

public sealed partial class GameCard : UserControl
{
    public static readonly DependencyProperty GameProperty = DependencyProperty.Register(nameof(Game), typeof(GameViewModel), typeof(GameCard),
        new PropertyMetadata(null, (d, e) => ((GameCard)d).OnGameChanged((GameViewModel?)e.OldValue, (GameViewModel?)e.NewValue)));

    public GameViewModel? Game { get => (GameViewModel?)GetValue(GameProperty); set => SetValue(GameProperty, value); }

    /// <summary>Carousel cards slide down when selected; grid cards only get the highlight ring.</summary>
    public static readonly DependencyProperty AnimateDownProperty = DependencyProperty.Register(nameof(AnimateDown), typeof(bool), typeof(GameCard), new PropertyMetadata(true));
    public bool AnimateDown { get => (bool)GetValue(AnimateDownProperty); set => SetValue(AnimateDownProperty, value); }

    private const double SelectedOffsetY = 38, SelectedScale = 1.12;
    private Storyboard? _sb;
    private readonly PropertyChangedEventHandler _handler;

    public GameCard()
    {
        InitializeComponent();
        _handler = OnGameProperty;
        Tapped += (_, _) => Game?.RequestSelect();
        DoubleTapped += (_, _) => Game?.RequestLaunch();
        Loaded += (_, _) => { if (Game is not null) { Game.PropertyChanged -= _handler; Game.PropertyChanged += _handler; Refresh(); Apply(false); } };
        Unloaded += (_, _) => { if (Game is not null) Game.PropertyChanged -= _handler; _sb?.Stop(); };
    }

    private void OnGameChanged(GameViewModel? old, GameViewModel? nw)
    {
        if (old is not null) old.PropertyChanged -= _handler;
        if (nw is not null) nw.PropertyChanged += _handler;
        Refresh();
        Apply(false);   // recycled/new cards snap to their state; only real selection changes animate
    }

    private void OnGameProperty(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameViewModel.IsSelected)) Apply(true);
        else if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is nameof(GameViewModel.CoverImage) or nameof(GameViewModel.IsFavorite) or nameof(GameViewModel.IsRunning) or nameof(GameViewModel.StatusText))
        { Refresh(); }
    }

    private void Refresh()
    {
        var g = Game;
        if (g is null) return;
        FavIcon.Visibility = g.IsFavorite ? Visibility.Visible : Visibility.Collapsed;
        RunDot.Visibility = g.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        Card.Opacity = g.Model.IsInstalled ? 1 : 0.5;
        if (g.CoverImage is { } src)
        {
            CoverBorder.Background = new ImageBrush { ImageSource = src, Stretch = Stretch.UniformToFill };
            FallbackTitle.Visibility = Visibility.Collapsed; IconImg.Visibility = Visibility.Collapsed; Monogram.Visibility = Visibility.Collapsed;
        }
        else
        {
            // Generated fallback layout: gradient + title (+ the EXE icon when we could extract one)
            var grad = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1) };
            grad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xB0, 0x8B, 0x7C, 0xFF), Offset = 0 });
            grad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xFF, 0x11, 0x15, 0x1D), Offset = 1 });
            CoverBorder.Background = grad;
            FallbackTitle.Text = g.Title; FallbackTitle.Visibility = Visibility.Visible;
            if (g.IconImage is { } icon) { IconImg.Source = icon; IconImg.Visibility = Visibility.Visible; Monogram.Visibility = Visibility.Collapsed; }
            else { IconImg.Visibility = Visibility.Collapsed; Monogram.Text = g.Monogram; Monogram.Visibility = Visibility.Visible; }
        }
    }

    private void Apply(bool animate)
    {
        var selected = Game?.IsSelected == true;
        var ty = selected && AnimateDown ? SelectedOffsetY : 0;
        var sc = selected ? (AnimateDown ? SelectedScale : 1.04) : 1.0;
        var s = AppHost.Instance.Settings.Current;

        // capture the in-flight value so an interrupted animation never jumps
        var cy = Xf.TranslateY; var cs = Xf.ScaleX; var co = Ring.Opacity;
        _sb?.Stop();
        Xf.TranslateY = cy; Xf.ScaleX = cs; Xf.ScaleY = cs; Ring.Opacity = co;

        if (!animate || !s.AnimationsEnabled)
        {
            Xf.TranslateY = ty; Xf.ScaleX = sc; Xf.ScaleY = sc; Ring.Opacity = selected ? 1 : 0;
            return;
        }
        var ms = 320.0 * 100 / Math.Clamp(s.AnimationSpeedPercent, 25, 400);
        var sb = new Storyboard();
        void Add(DependencyObject target, string prop, double to)
        {
            var a = new DoubleAnimation { To = to, Duration = new Duration(TimeSpan.FromMilliseconds(ms)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(a, target); Storyboard.SetTargetProperty(a, prop); sb.Children.Add(a);
        }
        Add(Xf, "TranslateY", ty); Add(Xf, "ScaleX", sc); Add(Xf, "ScaleY", sc); Add(Ring, "Opacity", selected ? 1 : 0);
        sb.Completed += (_, _) => { Xf.TranslateY = ty; Xf.ScaleX = sc; Xf.ScaleY = sc; Ring.Opacity = selected ? 1 : 0; sb.Stop(); };
        _sb = sb;
        sb.Begin();
    }
}
