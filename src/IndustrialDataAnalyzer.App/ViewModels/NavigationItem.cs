using CommunityToolkit.Mvvm.ComponentModel;

namespace IndustrialDataAnalyzer.App.ViewModels;

/// <summary>Represents a selectable item in the sidebar.</summary>
public partial class NavigationItem(NavigationPage page, string title, string icon) : ObservableObject
{
    /// <summary>Gets the target page.</summary>
    public NavigationPage Page { get; } = page;
    /// <summary>Gets the display title.</summary>
    public string Title { get; } = title;
    /// <summary>Gets the Segoe MDL2 glyph.</summary>
    public string Icon { get; } = icon;

    [ObservableProperty]
    private bool isSelected;
}
