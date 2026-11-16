using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GameIndex.Views;

public sealed partial class StatTile : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(StatTile), new PropertyMetadata(""));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(string), typeof(StatTile), new PropertyMetadata(""));
    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(nameof(Detail), typeof(string), typeof(StatTile), new PropertyMetadata(""));
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(StatTile), new PropertyMetadata("\uE946"));
    /// <summary>0..100 shows a thin progress bar; any negative value hides it.</summary>
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(StatTile),
        new PropertyMetadata(-1.0, (d, _) => ((StatTile)d).UpdateBar()));

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Detail { get => (string)GetValue(DetailProperty); set => SetValue(DetailProperty, value); }
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    public StatTile() { InitializeComponent(); UpdateBar(); }

    private void UpdateBar()
    {
        if (Bar is null) return;
        Bar.Visibility = Progress >= 0 ? Visibility.Visible : Visibility.Collapsed;
        if (Progress >= 0) Bar.Value = Math.Clamp(Progress, 0, 100);
    }
}
