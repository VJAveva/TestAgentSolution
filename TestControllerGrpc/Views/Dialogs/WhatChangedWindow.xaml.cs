using System.Windows;
using TestControllerGrpc.PipelineEditor;

namespace TestControllerGrpc.Views.Dialogs;

/// <summary>Side-by-side before / after of each changed pipeline and template (Pipeline Editor, What changed).</summary>
public partial class WhatChangedWindow : Window
{
    private readonly IReadOnlyList<PipelineChange> _changes;

    public WhatChangedWindow(string title, IReadOnlyList<PipelineChange> changes)
    {
        InitializeComponent();
        Title = title;
        _changes = changes;
        Show(onlyChanges: true);
    }

    // "Only changed lines" keeps one unchanged line either side of each change for context.
    private void Show(bool onlyChanges) =>
        Sections.ItemsSource = _changes.Select(change => onlyChanges
            ? change with { Rows = change.Rows.Where((row, i) => Near(change.Rows, i)).ToList() }
            : change).ToList();

    private static bool Near(IReadOnlyList<DiffRow> rows, int index) =>
        rows[index].IsChange
        || index > 0 && rows[index - 1].IsChange
        || index < rows.Count - 1 && rows[index + 1].IsChange;

    private void OnlyChanges_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) Show(OnlyChanges.IsChecked == true);
    }
}
