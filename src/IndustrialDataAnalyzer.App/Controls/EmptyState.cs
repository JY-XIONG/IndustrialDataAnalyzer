using System.Windows;
using System.Windows.Controls;

namespace IndustrialDataAnalyzer.App.Controls;

/// <summary>Explains why a content region has no records yet.</summary>
public sealed class EmptyState : Control
{
    /// <summary>Gets or sets the empty state heading.</summary>
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    /// <summary>Identifies the title property.</summary>
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(EmptyState), new PropertyMetadata("暂无数据"));

    /// <summary>Gets or sets the explanatory text.</summary>
    public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    /// <summary>Identifies the message property.</summary>
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(string), typeof(EmptyState), new PropertyMetadata(string.Empty));
}
