using System.Windows;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Views.Dialogs;

/// <summary>Picks one agent (Pipeline Editor, Change agent); each choice shows the agent's status.</summary>
public partial class ChooseAgentDialog : Window
{
    public ChooseAgentDialog(string nodeLabel, IReadOnlyList<XmlEditorAgentChoice> agents)
    {
        InitializeComponent();
        NodeLabel.Text = $"Change the agent of '{nodeLabel}'";
        AgentBox.ItemsSource = agents;
        Loaded += (_, _) => AgentBox.Focus();
    }

    public string? AgentName => (AgentBox.SelectedItem as XmlEditorAgentChoice)?.Name;

    private void AgentBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        OkButton.IsEnabled = AgentName is not null;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
