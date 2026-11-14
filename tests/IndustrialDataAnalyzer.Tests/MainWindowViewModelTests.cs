using System.Linq;
using IndustrialDataAnalyzer.App.ViewModels;
using IndustrialDataAnalyzer.App.ViewModels.Pages;
using IndustrialDataAnalyzer.Core.Abstractions;

namespace IndustrialDataAnalyzer.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void StartsOnOverviewWithOnlyOverviewSelected()
    {
        var viewModel = CreateViewModel();
        var items = GetNavigationItems(viewModel);

        Assert.Equal(NavigationPage.Overview, viewModel.CurrentPage.Page);
        Assert.Equal(Enum.GetValues<NavigationPage>(), items.Select(item => item.Page));
        Assert.Equal(NavigationPage.Overview, Assert.Single(items, item => item.IsSelected).Page);
    }

    [Theory]
    [InlineData(NavigationPage.Overview)]
    [InlineData(NavigationPage.Devices)]
    [InlineData(NavigationPage.Acquisition)]
    [InlineData(NavigationPage.Monitoring)]
    [InlineData(NavigationPage.History)]
    [InlineData(NavigationPage.Alarms)]
    [InlineData(NavigationPage.Reports)]
    [InlineData(NavigationPage.Settings)]
    public void EveryNavigationItemOpensItsPageAndSelectsOnlyIt(NavigationPage page)
    {
        var viewModel = CreateViewModel();
        var items = GetNavigationItems(viewModel);
        var target = Assert.Single(items, item => item.Page == page);

        viewModel.NavigateCommand.Execute(target);

        Assert.Equal(page, viewModel.CurrentPage.Page);
        Assert.Same(target, Assert.Single(items, item => item.IsSelected));
    }

    [Fact]
    public void InvalidNavigationParameterPreservesPageAndSelection()
    {
        var viewModel = CreateViewModel();
        var items = GetNavigationItems(viewModel);
        viewModel.NavigateCommand.Execute(items.Single(item => item.Page == NavigationPage.Devices));
        var selectedPage = viewModel.CurrentPage;

        viewModel.NavigateCommand.Execute(null);
        viewModel.NavigateCommand.Execute(new NavigationItem((NavigationPage)999, "无效页面", string.Empty));

        Assert.Same(selectedPage, viewModel.CurrentPage);
        Assert.Equal(NavigationPage.Devices, Assert.Single(items, item => item.IsSelected).Page);
    }

    [Fact]
    public void SwitchingPagesClearsThePreviousSelection()
    {
        var viewModel = CreateViewModel();
        var items = GetNavigationItems(viewModel);

        foreach (var target in items.Reverse())
        {
            viewModel.NavigateCommand.Execute(target);
            Assert.Same(target, Assert.Single(items, item => item.IsSelected));
        }
    }

    private static MainWindowViewModel CreateViewModel()
    {
        var info = new TestApplicationInfo();
        PageViewModel[] pages =
        [
            new OverviewViewModel(),
            new DevicesViewModel(),
            new AcquisitionViewModel(),
            new MonitoringViewModel(),
            new HistoryViewModel(),
            new AlarmsViewModel(),
            new ReportsViewModel(),
            new SettingsViewModel(info)
        ];
        return new MainWindowViewModel(pages, info);
    }

    private static NavigationItem[] GetNavigationItems(MainWindowViewModel viewModel) =>
    [
        .. viewModel.WorkspaceItems,
        .. viewModel.DeviceItems,
        .. viewModel.DataItems,
        .. viewModel.SystemItems
    ];

    private sealed class TestApplicationInfo : IApplicationInfo
    {
        public string Name => "Test";
        public string Version => "V1.0.0";
        public string EnvironmentName => "Test";
        public string DataDirectory => string.Empty;
        public string LogsDirectory => string.Empty;
    }
}
