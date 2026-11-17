using System.Windows;

namespace TestControllerGrpc.Views.Dialogs;

/// <summary>Asks for the name of a template made from a group (Pipeline Editor, Save group as template).</summary>
public partial class TemplateNameDialog : Window
{
    public TemplateNameDialog(string groupName)
    {
        InitializeComponent();
        GroupLabel.Text = $"Save '{groupName}' as a template";
        NameBox.Text = groupName;
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    public string TemplateName => NameBox.Text.Trim();

    private void NameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        SaveButton.IsEnabled = TemplateName.Length > 0;

    private void OnSave(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
