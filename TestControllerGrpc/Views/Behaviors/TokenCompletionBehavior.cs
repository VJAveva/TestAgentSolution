using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Views.Behaviors;

/// <summary>
/// Token auto-complete for the Command / Parameters editors: typing <c>[</c> opens a list of known
/// tokens with their currently resolved value, and picking one inserts it at the caret.
/// </summary>
/// <remarks>
/// Attached rather than a custom control so the existing TextBoxes keep their style, binding and
/// automation name. The suggestion list is pulled from the view model on each open so a newly
/// loaded parameter file is reflected without any refresh plumbing.
/// </remarks>
public static class TokenCompletionBehavior
{
    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.RegisterAttached(
            "Source", typeof(MainViewModel), typeof(TokenCompletionBehavior),
            new PropertyMetadata(null, OnSourceChanged));

    public static void SetSource(DependencyObject d, MainViewModel? value) => d.SetValue(SourceProperty, value);
    public static MainViewModel? GetSource(DependencyObject d) => (MainViewModel?)d.GetValue(SourceProperty);

    private static readonly DependencyProperty PopupProperty =
        DependencyProperty.RegisterAttached("Popup", typeof(Popup), typeof(TokenCompletionBehavior));

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;

        box.TextChanged -= OnTextChanged;
        box.PreviewKeyDown -= OnPreviewKeyDown;
        box.LostFocus -= OnLostFocus;

        if (e.NewValue is null) return;

        box.TextChanged += OnTextChanged;
        box.PreviewKeyDown += OnPreviewKeyDown;
        box.LostFocus += OnLostFocus;
    }

    private static void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        var box = (TextBox)sender;
        if (TriggerPrefixLength(box) is 0) { Close(box); return; }
        Open(box);
    }

    /// <summary>
    /// Length of the token prefix immediately left of the caret: 2 for "[_", 1 for "[", 0 for neither.
    /// </summary>
    private static int TriggerPrefixLength(TextBox box)
    {
        var caret = box.CaretIndex;
        var text = box.Text;
        if (caret >= 2 && text[caret - 2] == '[' && text[caret - 1] == '_') return 2;
        if (caret >= 1 && text[caret - 1] == '[') return 1;
        return 0;
    }

    private static void Open(TextBox box)
    {
        var vm = GetSource(box);
        if (vm is null) return;

        var suggestions = vm.GetTokenSuggestions();
        if (suggestions.Count == 0) { Close(box); return; }

        if (box.GetValue(PopupProperty) is not Popup popup)
        {
            var list = new ListBox { MaxHeight = 220, MinWidth = 320 };
            list.SetResourceReference(Control.BackgroundProperty, "BgPanel");
            list.SetResourceReference(Control.ForegroundProperty, "TextP");
            list.DisplayMemberPath = nameof(TokenSuggestion.Display);
            list.MouseLeftButtonUp += (_, _) => Commit(box);
            list.PreviewKeyDown += (_, args) =>
            {
                if (args.Key is Key.Enter or Key.Tab) { Commit(box); args.Handled = true; }
                else if (args.Key == Key.Escape) { Close(box); args.Handled = true; }
            };

            popup = new Popup
            {
                Child = list,
                PlacementTarget = box,
                Placement = PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true,
            };
            box.SetValue(PopupProperty, popup);
        }

        ((ListBox)popup.Child).ItemsSource = suggestions;
        ((ListBox)popup.Child).SelectedIndex = 0;
        popup.IsOpen = true;
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        if (box.GetValue(PopupProperty) is not Popup { IsOpen: true } popup) return;
        var list = (ListBox)popup.Child;

        switch (e.Key)
        {
            case Key.Down:
                list.SelectedIndex = Math.Min(list.SelectedIndex + 1, list.Items.Count - 1);
                e.Handled = true;
                break;
            case Key.Up:
                list.SelectedIndex = Math.Max(list.SelectedIndex - 1, 0);
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Tab:
                Commit(box);
                e.Handled = true;
                break;
            case Key.Escape:
                Close(box);
                e.Handled = true;
                break;
        }
    }

    private static void Commit(TextBox box)
    {
        if (box.GetValue(PopupProperty) is not Popup { IsOpen: true } popup) return;
        if (((ListBox)popup.Child).SelectedItem is not TokenSuggestion pick) { Close(box); return; }

        var prefix = TriggerPrefixLength(box);
        if (prefix == 0) { Close(box); return; }

        // Replace the typed "[" / "[_" so the inserted token is not doubled up.
        var start = box.CaretIndex - prefix;
        box.Text = box.Text.Remove(start, prefix).Insert(start, pick.Token);
        box.CaretIndex = start + pick.Token.Length;
        Close(box);
    }

    private static void OnLostFocus(object sender, RoutedEventArgs e) => Close((TextBox)sender);

    private static void Close(TextBox box)
    {
        if (box.GetValue(PopupProperty) is Popup popup) popup.IsOpen = false;
    }
}
