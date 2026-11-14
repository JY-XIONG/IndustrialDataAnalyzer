using CommunityToolkit.Mvvm.ComponentModel;

namespace IndustrialDataAnalyzer.App.ViewModels.Pages;

/// <summary>Provides page identity and header text for the shell.</summary>
public abstract class PageViewModel(NavigationPage page, string title, string description) : ObservableObject
{
    /// <summary>Gets the page identifier.</summary>
    public NavigationPage Page { get; } = page;
    /// <summary>Gets the page title.</summary>
    public string Title { get; } = title;
    /// <summary>Gets the page description.</summary>
    public string Description { get; } = description;
}
