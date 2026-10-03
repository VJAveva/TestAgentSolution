using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using TestControllerGrpc.Core.Preflight;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Views.Dialogs;

/// <summary>
/// The pre-flight report, as a themed modal. Replaces a MessageBox that could not show 30 findings,
/// could not be filtered, and could not be copied into a ticket.
/// </summary>
public partial class PreflightDialog : Window
{
    private readonly PreflightDialogViewModel _vm;

    private PreflightDialog(PreflightDialogViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();

        if (vm.CheckOnly)
        {
            // Nothing to start, so the only exit is Close - and it must be the default button.
            RunButton.Visibility = Visibility.Collapsed;
            CancelButton.Content = "Close";
            CancelButton.IsDefault = true;
        }

        RestoreSize();
        SizeChanged += (_, _) => SaveSize();
    }

    /// <summary>
    /// Shows the report. Returns true when the user chose to run; false for Cancel, Close, or
    /// any report that failed.
    /// </summary>
    public static bool Show(
        PreflightReport report,
        string checkedBy,
        bool checkOnly,
        Window? owner,
        Func<PreflightReport>? recheck = null)
    {
        var dialog = new PreflightDialog(new PreflightDialogViewModel(report, checkedBy, checkOnly, recheck));

        // Assigning a null Owner throws, and CenterOwner without an Owner parks the window in the
        // top-left corner. Both are reachable because MainWindow is null until the shell is shown.
        if (owner is not null && owner != dialog) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        return dialog.ShowDialog() == true;
    }

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_vm.ReportText);
        }
        catch (Exception ex)
        {
            // The clipboard can be locked by another process; losing a copy must not close the dialog.
            ThemedMessageBox.Show($"Could not copy the report: {ex.Message}", "Copy report",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        var safe = string.Join("_", _vm.Target.Split(Path.GetInvalidFileNameChars()));
        var dialog = new SaveFileDialog
        {
            Title = "Save pre-flight report",
            Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt",
            FileName = $"preflight_{DateTime.Now:yyyyMMdd_HHmmss}_{safe}.md",
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var isMarkdown = dialog.FileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(dialog.FileName, isMarkdown ? _vm.ToMarkdown() : _vm.ReportText);
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"Could not save the report: {ex.Message}", "Save report",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Recheck_Click(object sender, RoutedEventArgs e)
    {
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            _vm.Recheck();
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"Could not re-run the checks: {ex.Message}", "Re-check",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Cursor = null;
        }
    }

    // ── Remembered size ─────────────────────────────────────────────

    private sealed record DialogSize(double Width, double Height);

    private static string SizeFile =>
        Path.Combine(AppLogger.DefaultLogDirectory, "preflight", "dialog-size.json");

    private void RestoreSize()
    {
        try
        {
            if (!File.Exists(SizeFile)) return;
            var saved = JsonSerializer.Deserialize<DialogSize>(File.ReadAllText(SizeFile));
            if (saved is null) return;

            // Guard against a size saved on a monitor that is no longer attached.
            Width = Math.Clamp(saved.Width, MinWidth, SystemParameters.VirtualScreenWidth);
            Height = Math.Clamp(saved.Height, MinHeight, SystemParameters.VirtualScreenHeight);
        }
        catch
        {
            // A corrupt preference must never stop the dialog opening.
        }
    }

    private void SaveSize()
    {
        if (WindowState != WindowState.Normal) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SizeFile)!);
            File.WriteAllText(SizeFile, JsonSerializer.Serialize(new DialogSize(Width, Height)));
        }
        catch
        {
            // Preference only.
        }
    }
}
