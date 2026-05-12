using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Windows;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Views.Dialogs;

public partial class ExecutionLogViewerDialog : Window
{
    private readonly ExecutionLogViewerVM _vm;

    public ExecutionLogViewerDialog(ExecutionLogViewerVM vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    /// <summary>
    /// Loads the merged execution log via the local API and shows the dialog.
    /// Returns null if the API call fails (a message box is displayed).
    /// </summary>
    public static ExecutionLogViewerDialog? Create(
        string buildName, string testCaseName, int? failedStepIndex, Window? owner)
    {
        try
        {
            var apiBase = ResolveApiBase();
            using var http = new HttpClient { BaseAddress = new Uri(apiBase), Timeout = TimeSpan.FromSeconds(15) };
            var url = $"api/results/builds/{Uri.EscapeDataString(buildName)}" +
                      $"/test/{Uri.EscapeDataString(testCaseName)}/log" +
                      (failedStepIndex.HasValue ? $"?stepIndex={failedStepIndex}" : "");

            var report = http.GetFromJsonAsync<ExecutionLogViewerVM.LogPayload>(url)
                .GetAwaiter().GetResult();

            if (report == null)
            {
                MessageBox.Show("No log data returned from API.", "Execution Log",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }

            var vm = new ExecutionLogViewerVM(report);
            return new ExecutionLogViewerDialog(vm) { Owner = owner };
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load execution log:\n{ex.Message}",
                "Execution Log", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    private static string ResolveApiBase()
    {
        // WPF host runs Kestrel on the standard port; allow override via env var.
        return Environment.GetEnvironmentVariable("TESTCONTROLLER_API_BASE")
               ?? "http://localhost:5000";
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var line in _vm.FilteredTimeline)
                sb.AppendLine($"[{line.TimeText}] {line.Source,-10} {line.Severity,-8} {line.Message}");
            Clipboard.SetText(sb.ToString());
        }
        catch
        {
            MessageBox.Show("Could not copy to clipboard.", "Copy",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
