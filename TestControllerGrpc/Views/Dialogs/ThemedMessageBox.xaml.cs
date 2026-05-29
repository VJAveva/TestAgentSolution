using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TestControllerGrpc.Views.Dialogs;

public partial class ThemedMessageBox : Window
{
    public string Caption { get; }
    public string Message { get; }

    private MessageBoxResult _result = MessageBoxResult.None;

    private ThemedMessageBox(string message, string caption, MessageBoxButton buttons, MessageBoxImage icon)
    {
        Message = message;
        Caption = caption;
        DataContext = this;
        InitializeComponent();
        SetupIcon(icon);
        SetupButtons(buttons);
        Owner = GetActiveWindow();
    }

    /// <summary>Drop-in replacement for MessageBox.Show with themed styling.</summary>
    public static MessageBoxResult Show(string message, string caption,
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None)
    {
        var dlg = new ThemedMessageBox(message, caption, buttons, icon);
        dlg.ShowDialog();
        return dlg._result;
    }

    private void SetupIcon(MessageBoxImage icon)
    {
        switch (icon)
        {
            case MessageBoxImage.Error:
                IconGlyph.Text = "\uEA39";
                IconGlyph.Foreground = FindBrush("AccRed");
                break;
            case MessageBoxImage.Warning:
                IconGlyph.Text = "\uE7BA";
                IconGlyph.Foreground = FindBrush("AccYellow");
                break;
            case MessageBoxImage.Information:
                IconGlyph.Text = "\uE946";
                IconGlyph.Foreground = FindBrush("Accent");
                break;
            case MessageBoxImage.Question:
                IconGlyph.Text = "\uE9CE";
                IconGlyph.Foreground = FindBrush("AccMauve");
                break;
            default:
                IconGlyph.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void SetupButtons(MessageBoxButton buttons)
    {
        switch (buttons)
        {
            case MessageBoxButton.OK:
                AddButton("OK", MessageBoxResult.OK, isPrimary: true);
                break;
            case MessageBoxButton.OKCancel:
                AddButton("Cancel", MessageBoxResult.Cancel, isPrimary: false);
                AddButton("OK", MessageBoxResult.OK, isPrimary: true);
                break;
            case MessageBoxButton.YesNo:
                AddButton("No", MessageBoxResult.No, isPrimary: false);
                AddButton("Yes", MessageBoxResult.Yes, isPrimary: true);
                break;
            case MessageBoxButton.YesNoCancel:
                AddButton("Cancel", MessageBoxResult.Cancel, isPrimary: false);
                AddButton("No", MessageBoxResult.No, isPrimary: false);
                AddButton("Yes", MessageBoxResult.Yes, isPrimary: true);
                break;
        }
    }

    private void AddButton(string text, MessageBoxResult result, bool isPrimary)
    {
        var btn = new Button
        {
            Content = text,
            MinWidth = 80,
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = Cursors.Hand,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
        };

        if (isPrimary)
        {
            btn.Background = FindBrush("Accent");
            btn.Foreground = FindBrush("TreeBadgeFg");
            btn.BorderBrush = FindBrush("Accent");
        }
        else
        {
            btn.Background = FindBrush("BtnBg");
            btn.Foreground = FindBrush("TextP");
            btn.BorderBrush = FindBrush("Bdr");
        }

        btn.Click += (_, _) =>
        {
            _result = result;
            Close();
        };

        ButtonPanel.Children.Add(btn);
    }

    private Brush FindBrush(string key)
        => TryFindResource(key) is Brush b ? b : Brushes.Gray;

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private static Window? GetActiveWindow()
    {
        foreach (Window w in Application.Current.Windows)
        {
            if (w.IsActive) return w;
        }
        return Application.Current.MainWindow;
    }
}
