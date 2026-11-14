using System.Windows;
using System.Windows.Controls;

namespace IndustrialDataAnalyzer.App.Controls;

/// <summary>Displays one numeric summary in a consistent card.</summary>
public sealed class MetricCard : Control
{
    /// <summary>Gets or sets the metric title.</summary>
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    /// <summary>Identifies the title property.</summary>
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(MetricCard), new PropertyMetadata(string.Empty));

    /// <summary>Gets or sets the metric value.</summary>
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    /// <summary>Identifies the value property.</summary>
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(string), typeof(MetricCard), new PropertyMetadata("0"));

    /// <summary>Gets or sets the supporting note.</summary>
    public string Note { get => (string)GetValue(NoteProperty); set => SetValue(NoteProperty, value); }
    /// <summary>Identifies the note property.</summary>
    public static readonly DependencyProperty NoteProperty = DependencyProperty.Register(nameof(Note), typeof(string), typeof(MetricCard), new PropertyMetadata(string.Empty));
}
