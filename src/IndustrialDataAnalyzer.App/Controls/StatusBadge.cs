using System.Windows;
using System.Windows.Controls;

namespace IndustrialDataAnalyzer.App.Controls;

/// <summary>Displays a compact status label.</summary>
public sealed class StatusBadge : Control
{
    /// <summary>Gets or sets the status text.</summary>
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    /// <summary>Identifies the text property.</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(StatusBadge), new PropertyMetadata(string.Empty));
}
