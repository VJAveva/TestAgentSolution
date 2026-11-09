using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TestControllerGrpc.Models;
using TestControllerGrpc.ViewModels;
using TestControllerGrpc.Views;

namespace TestControllerGrpc.Tests.Ui;

/// <summary>
/// The P1 right-click fix failed on JVGR22 (2026-11-09): right-clicking in the indent left of a deep row
/// showed the root menu. The indent is not part of any row's hit area, so the click reached the TreeView
/// itself and "empty space" selected the root. Rows must be matched by height, at any X.
/// Uses the real SpecTVI style and row template, so it measures what the user sees.
/// </summary>
public sealed class TreeRightClickHitTests
{
    private static WatchListConfig Config() => new()
    {
        WatchItems =
        [
            new WatchItemConfig
            {
                Tag = "P1",
                Path = @"C:\Triggers",
                Events =
                [
                    new EventConfig
                    {
                        Type = "Renamed",
                        Children =
                        [
                            new ActionGroupConfig
                            {
                                Tag = "Stage",
                                Children = [new ActionConfig { Tag = "Install", Type = ActionType.RunCommand, Command = "x.bat" }],
                            },
                        ],
                    },
                ],
            },
        ],
    };

    [Fact]
    public void RowAt_Should_ReturnTheDeepRow_When_TheClickIsInItsIndent()
    {
        var (hitRowTag, rowAtTag, belowLast, afterCollapse) = Sta(() =>
        {
            var root = TreeNodeViewModel.FromWatchList(Config());
            var tree = new TreeView
            {
                Background = Brushes.Transparent,
                ItemContainerStyle = (Style)Application.Current.Resources["SpecTVI"],
                ItemTemplate = (DataTemplate)Application.Current.Resources["WatchTreeSpecTemplate"],
                ItemsSource = new[] { root },
            };
            var host = new Border { Width = 500, Height = 400, Child = tree };
            host.Measure(new Size(500, 400));
            host.Arrange(new Rect(0, 0, 500, 400));
            host.UpdateLayout();

            var action = Containers(tree).Single(c => ((TreeNodeViewModel)c.DataContext).Tag == "Install");
            var bd = (FrameworkElement)action.Template.FindName("Bd", action);
            var rowTop = bd.TranslatePoint(new Point(0, 0), tree).Y;
            var indentPoint = new Point(2, rowTop + bd.ActualHeight / 2);

            Assert.True(bd.TranslatePoint(new Point(0, 0), tree).X > 20, "The test row must be indented.");

            var hit = tree.InputHitTest(indentPoint) as DependencyObject;
            var hitRow = TreeHitTest.ContainerAt(hit);
            var row = TreeHitTest.RowAt(tree, indentPoint);
            var below = TreeHitTest.RowAt(tree, new Point(2, 390));

            ((TreeNodeViewModel)action.DataContext).Parent!.IsExpanded = false;
            host.UpdateLayout();
            var collapsed = TreeHitTest.RowAt(tree, indentPoint);

            return (((TreeNodeViewModel?)hitRow?.DataContext)?.Tag,
                    ((TreeNodeViewModel?)row?.DataContext)?.Tag,
                    below,
                    ((TreeNodeViewModel?)collapsed?.DataContext)?.Tag);
        });

        Assert.NotEqual("Install", hitRowTag);   // the visual hit misses the row: the reported bug
        Assert.Equal("Install", rowAtTag);
        Assert.Null(belowLast);                    // real empty space still means the root menu
        Assert.NotEqual("Install", afterCollapse); // a hidden row is never the target
    }

    [Fact]
    public void RightClickHandler_Should_MatchRowsByHeight_When_SelectingTheTarget()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TestAgentSolution.sln"))) dir = dir.Parent;
        var source = File.ReadAllText(Path.Combine(dir!.FullName, "TestControllerGrpc", "Views", "MainWindow.xaml.cs"));
        var start = source.IndexOf("private void OnTreePreviewRightButtonDown(", StringComparison.Ordinal);
        Assert.True(start >= 0, "OnTreePreviewRightButtonDown was renamed; update this guard deliberately.");

        Assert.Contains("TreeHitTest.RowAt(tree, e.GetPosition(tree))", source[start..]);
    }

    private static IEnumerable<TreeViewItem> Containers(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TreeViewItem item) yield return item;
            foreach (var deeper in Containers(child)) yield return deeper;
        }
    }

    private static T Sta<T>(Func<T> body) => WpfTestHost.Run(body);
}
