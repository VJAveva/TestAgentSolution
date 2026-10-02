using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Tests.Ui;

/// <summary>
/// R1-01. Every column in the tree row template is <c>Auto</c> and the TreeView scrolls horizontally,
/// so a status indicator placed AFTER the label is pushed hundreds of pixels off-screen on deep action
/// rows - the status was being set and rendered correctly, just outside the viewport. These pin the
/// glyph to the left of the label and prove it stays inside a narrow pane.
/// </summary>
public sealed class TreeStatusGlyphLayoutTests
{
    private const double PaneWidth = 300;

    /// <summary>Generous stand-in for ~5 levels of TreeViewItem indentation.</summary>
    private const double DeepIndent = 120;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TestAgentSolution.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // ── Structural guard (file analysis - cannot flake) ──────────────

    [Fact]
    public void StatusGlyph_Should_SitBeforeTheLabel_When_RowTemplateIsDeclared()
    {
        XNamespace pres = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var doc = XDocument.Load(Path.Combine(
            RepoRoot(), "TestControllerGrpc", "Views", "Styles", "TreeViewSpec.xaml"));

        static int Column(XElement e) => int.Parse(e.Attribute("Grid.Column")!.Value);

        var statusCell = doc.Descendants(pres + "Grid")
            .Single(e => (string?)e.Attribute("ToolTip") == "{Binding StatusTooltip}");

        var labelCell = doc.Descendants(pres + "TextBlock")
            .Single(e => e.Attribute("Grid.Column") is not null
                      && e.Descendants().Any(d =>
                             d.Attributes().Any(a => a.Value.Contains("ActionLabelConv"))));

        Assert.True(Column(statusCell) < Column(labelCell),
            $"The status glyph is in column {Column(statusCell)} and the label in column " +
            $"{Column(labelCell)}. Every column is Auto and the tree scrolls horizontally, so a glyph " +
            "placed after the label is pushed off-screen on deep action rows (R1-01).");
    }

    // ── Real layout measurement ──────────────────────────────────────

    [Theory]
    [InlineData("Running")]
    [InlineData("Success")]
    [InlineData("Failed")]
    [InlineData("Skipped")]
    public void ActionRowStatus_Should_StayInsideA300pxPane_When_RowIsDeeplyIndented(string status)
    {
        var (right, labelX, visibleGlyphs) = Sta(() => MeasureStatusGlyph(status));

        Assert.True(visibleGlyphs == 1,
            $"Expected exactly one visible status glyph for '{status}', found {visibleGlyphs}.");

        Assert.True(right <= PaneWidth,
            $"'{status}' glyph right edge is {right:F0}px in a {PaneWidth}px pane " +
            $"(indent {DeepIndent}px) - it would be off-screen.");

        Assert.True(right <= labelX,
            $"'{status}' glyph (right edge {right:F0}px) must sit before the label (x {labelX:F0}px).");
    }

    private static (double Right, double LabelX, int VisibleGlyphs) MeasureStatusGlyph(string status)
    {
        var vm = new TreeNodeViewModel
        {
            NodeKind = "Action",
            // The real cause: action labels run to FormatActionLabel's 100-character cap.
            DisplayText = new string('W', 100),
            ExecutionStatus = status,
        };

        var template = (DataTemplate)Application.Current.Resources["WatchTreeSpecTemplate"];
        var presenter = new ContentPresenter { Content = vm, ContentTemplate = template };

        // Margin models TreeViewItem indentation; the outer Border is the 300px pane.
        var indented = new Border { Margin = new Thickness(DeepIndent, 0, 0, 0), Child = presenter };
        var pane = new Border { Width = PaneWidth, Child = indented };

        pane.Measure(new Size(PaneWidth, double.PositiveInfinity));
        pane.Arrange(new Rect(0, 0, PaneWidth, 200));
        pane.UpdateLayout();

        var row = FindRowGrid(presenter);
        var statusCell = row.Children.OfType<UIElement>().First(c => Grid.GetColumn(c) == 1);
        var labelCell = row.Children.OfType<UIElement>().First(c => Grid.GetColumn(c) == 3);

        var statusX = ((FrameworkElement)statusCell).TransformToAncestor(pane).Transform(default).X;
        var right = statusX + ((FrameworkElement)statusCell).ActualWidth;
        var labelX = ((FrameworkElement)labelCell).TransformToAncestor(pane).Transform(default).X;

        var visible = ((Grid)statusCell).Children.OfType<UIElement>()
            .Count(c => c.Visibility == Visibility.Visible);

        return (right, labelX, visible);
    }

    private static Grid FindRowGrid(DependencyObject root)
    {
        if (root is Grid g && g.ColumnDefinitions.Count > 1) return g;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindRowGrid(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null!;
    }

    // ── STA host ─────────────────────────────────────────────────────

    /// <remarks>
    /// One Application per process is a hard WPF rule, so every case runs on a single STA thread and
    /// reuses <see cref="Application.Current"/> if some other test got there first.
    /// </remarks>
    private static T Sta<T>(Func<T> body)
    {
        Exception? error = null;
        T result = default!;

        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application();
                if (app.Resources.MergedDictionaries.Count == 0)
                    foreach (var path in new[]
                    {
                        "Themes/DarkTheme.xaml",
                        "Views/Styles/DesignTokens.xaml",
                        "Views/Styles/Typography.xaml",
                        "Views/Styles/ControlStyles.xaml",
                        "Views/Styles/ExecutionDashboardStyles.xaml",
                        "Views/Styles/SpecTokens.xaml",
                        "Views/Styles/TreeViewSpec.xaml",
                    })
                    {
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary
                        {
                            Source = new Uri(
                                $"pack://application:,,,/TestControllerGrpc;component/{path}",
                                UriKind.Absolute),
                        });
                    }

                result = body();
            }
            catch (Exception ex) { error = ex; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
        return result;
    }
}
