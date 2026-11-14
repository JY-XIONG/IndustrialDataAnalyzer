using System.Windows;
using System.Windows.Controls;

namespace IndustrialDataAnalyzer.App.Controls;

/// <summary>Displays the title and description of a content page.</summary>
public sealed class PageHeader : Control
{
    /// <summary>Gets or sets the page title.</summary>
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    /// <summary>Identifies the title property.</summary>
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageHeader), new PropertyMetadata(string.Empty));

    /// <summary>Gets or sets the page description.</summary>
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    /// <summary>Identifies the description property.</summary>
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(PageHeader), new PropertyMetadata(string.Empty));
}
