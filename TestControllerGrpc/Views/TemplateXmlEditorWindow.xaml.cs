using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Views;

public partial class TemplateXmlEditorWindow : Window
{
    private readonly TemplateXmlEditorViewModel _vm;
    private FoldingManager? _foldingManager;
    private XmlFoldingStrategy? _foldingStrategy;
    private CompletionWindow? _completionWindow;

    public TemplateXmlEditorWindow(TemplateXmlEditorViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = _vm;

        // Wire close request
        _vm.RequestClose += () => DialogResult = _vm.DialogAccepted;

        // Wire error navigation
        _vm.NavigateToError += NavigateToLine;

        // Wire preview visibility changes
        _vm.PropertyChanged += OnViewModelPropertyChanged;

        // Set up AvalonEdit
        ConfigureEditor();
        ApplyThemeColors();

        // Load initial text
        XmlEditor.Text = _vm.XmlText;

        // Two-way sync: editor -> ViewModel
        XmlEditor.TextChanged += (_, _) =>
        {
            if (_vm.XmlText != XmlEditor.Text)
                _vm.XmlText = XmlEditor.Text;
            UpdateFolding();
        };

        // Track cursor position in status bar
        XmlEditor.TextArea.Caret.PositionChanged += (_, _) => UpdateCursorPosition();

        // IntelliSense: trigger completion on typing
        XmlEditor.TextArea.TextEntering += OnTextEntering;
        XmlEditor.TextArea.TextEntered += OnTextEntered;

        // Error row click navigation
        // DataGrid selection change will be handled via ViewModel command binding

        // Keyboard shortcuts for font size
        InputBindings.Add(new KeyBinding(
            new RelayCommand(() => IncreaseFontSize_Click(null, null!)),
            new KeyGesture(Key.OemPlus, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(
            new RelayCommand(() => DecreaseFontSize_Click(null, null!)),
            new KeyGesture(Key.OemMinus, ModifierKeys.Control)));

        // Initial folding
        SetupFolding();
    }

    private void ConfigureEditor()
    {
        // Load custom XML syntax highlighting from embedded resource
        var highlighting = LoadXmlHighlighting();
        if (highlighting is not null)
            XmlEditor.SyntaxHighlighting = highlighting;
        else
            XmlEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("XML");

        // Editor settings
        XmlEditor.Options.EnableHyperlinks = false;
        XmlEditor.Options.EnableEmailHyperlinks = false;
        XmlEditor.Options.ConvertTabsToSpaces = true;
        XmlEditor.Options.IndentationSize = 2;
        XmlEditor.Options.HighlightCurrentLine = true;
        XmlEditor.Options.ShowColumnRuler = false;
        XmlEditor.ShowLineNumbers = true;
    }

    private void ApplyThemeColors()
    {
        var bgBrush = TryFindResource("BgPanel") as SolidColorBrush;
        var fgBrush = TryFindResource("TextP") as SolidColorBrush;
        var lineNumFg = TryFindResource("TextS") as SolidColorBrush;

        if (bgBrush is not null)
            XmlEditor.Background = bgBrush;
        if (fgBrush is not null)
            XmlEditor.Foreground = fgBrush;

        if (lineNumFg is not null)
            XmlEditor.LineNumbersForeground = lineNumFg;

        var highlightBrush = TryFindResource("SelectedItemBg") as SolidColorBrush;
        if (highlightBrush is not null)
        {
            XmlEditor.TextArea.TextView.CurrentLineBackground = highlightBrush;
            XmlEditor.TextArea.TextView.CurrentLineBorder =
                new Pen(new SolidColorBrush(Color.FromArgb(0x33, 0x89, 0xB4, 0xFA)), 1);
        }
    }

    // ???????????????????????????????????????????????????????????????
    // CODE FOLDING
    // ???????????????????????????????????????????????????????????????

    private void SetupFolding()
    {
        _foldingManager = FoldingManager.Install(XmlEditor.TextArea);
        _foldingStrategy = new XmlFoldingStrategy();
        UpdateFolding();
    }

    private void UpdateFolding()
    {
        if (_foldingManager is not null && _foldingStrategy is not null)
        {
            try
            {
                _foldingStrategy.UpdateFoldings(_foldingManager, XmlEditor.Document);
            }
            catch
            {
                // Ignore folding errors for malformed XML
            }
        }
    }

    // ???????????????????????????????????????????????????????????????
    // INTELLISENSE / COMPLETION
    // ???????????????????????????????????????????????????????????????

    private void OnTextEntering(object sender, TextCompositionEventArgs e)
    {
        if (_completionWindow is not null && e.Text.Length > 0)
        {
            if (!char.IsLetterOrDigit(e.Text[0]))
                _completionWindow.CompletionList.RequestInsertion(e);
        }
    }

    private void OnTextEntered(object sender, TextCompositionEventArgs e)
    {
        if (e.Text == "<")
        {
            ShowElementCompletion();
        }
        else if (e.Text == " ")
        {
            // Detect if we're inside an opening tag to suggest attributes
            var offset = XmlEditor.CaretOffset;
            var lineText = GetCurrentLineTextUpToCaret(offset);
            var tagName = ExtractCurrentTagName(lineText);
            if (tagName is not null)
                ShowAttributeCompletion(tagName);
        }
        else if (e.Text == "\"")
        {
            // Detect attribute name to suggest values
            var offset = XmlEditor.CaretOffset;
            var lineText = GetCurrentLineTextUpToCaret(offset);
            var attrName = ExtractCurrentAttributeName(lineText);
            if (attrName is not null)
                ShowValueCompletion(attrName);
        }
        else if (e.Text == ">")
        {
            // Auto-close tags
            TryAutoCloseTag();
        }
    }

    private void ShowElementCompletion()
    {
        _completionWindow = new CompletionWindow(XmlEditor.TextArea);
        var data = _completionWindow.CompletionList.CompletionData;
        data.Add(new XmlCompletionData("Templates", "Root element for template definitions"));
        data.Add(new XmlCompletionData("Template", "Template definition with ID"));
        data.Add(new XmlCompletionData("ActionGroup", "Group of actions with execution type"));
        data.Add(new XmlCompletionData("Action", "Single action (RunCommand/RunRemoteCommand/SendMail)"));
        data.Add(new XmlCompletionData("Initialize", "Parameter file initialization"));
        data.Add(new XmlCompletionData("Ref", "Reference to another template"));
        _completionWindow.Show();
        _completionWindow.Closed += (_, _) => _completionWindow = null;
    }

    private void ShowAttributeCompletion(string tagName)
    {
        _completionWindow = new CompletionWindow(XmlEditor.TextArea);
        var data = _completionWindow.CompletionList.CompletionData;

        switch (tagName)
        {
            case "Template":
                data.Add(new XmlCompletionData("ID=\"\"", "Template identifier"));
                break;
            case "ActionGroup":
                data.Add(new XmlCompletionData("Tag=\"\"", "Group tag/label"));
                data.Add(new XmlCompletionData("ExecutionType=\"\"", "Sequential or Parallel"));
                data.Add(new XmlCompletionData("FailAndContinue=\"true\"", "Continue on failure"));
                break;
            case "Action":
                data.Add(new XmlCompletionData("Type=\"\"", "RunCommand, RunRemoteCommand, or SendMail"));
                data.Add(new XmlCompletionData("AgentName=\"\"", "Remote agent name"));
                data.Add(new XmlCompletionData("Command=\"\"", "Command to execute"));
                data.Add(new XmlCompletionData("Parameters=\"\"", "Command parameters"));
                data.Add(new XmlCompletionData("Timeout=\"6000\"", "Timeout in seconds"));
                data.Add(new XmlCompletionData("PollInterval=\"30\"", "Poll interval in seconds"));
                data.Add(new XmlCompletionData("FailAndContinue=\"true\"", "Continue on failure"));
                data.Add(new XmlCompletionData("From=\"\"", "Mail sender"));
                data.Add(new XmlCompletionData("To=\"\"", "Mail recipients"));
                data.Add(new XmlCompletionData("Title=\"\"", "Mail subject"));
                data.Add(new XmlCompletionData("Body=\"\"", "Mail body"));
                data.Add(new XmlCompletionData("Embed=\"\"", "Embedded content"));
                break;
            case "Initialize":
                data.Add(new XmlCompletionData("Tag=\"\"", "Initialization tag"));
                data.Add(new XmlCompletionData("ParameterFile=\"\"", "Path to parameter file"));
                break;
            case "Ref":
                data.Add(new XmlCompletionData("TemplateID=\"\"", "Referenced template ID"));
                break;
        }

        if (data.Count > 0)
        {
            _completionWindow.Show();
            _completionWindow.Closed += (_, _) => _completionWindow = null;
        }
        else
        {
            _completionWindow = null;
        }
    }

    private void ShowValueCompletion(string attrName)
    {
        var values = attrName switch
        {
            "ExecutionType" => new[] { "Sequential", "Parallel" },
            "Type" => new[] { "RunCommand", "RunRemoteCommand", "SendMail" },
            "FailAndContinue" => new[] { "true", "false" },
            "IsReboot" => new[] { "true", "false" },
            _ => Array.Empty<string>()
        };

        if (values.Length == 0) return;

        _completionWindow = new CompletionWindow(XmlEditor.TextArea);
        var data = _completionWindow.CompletionList.CompletionData;
        foreach (var v in values)
            data.Add(new XmlCompletionData(v, $"{attrName} value"));

        _completionWindow.Show();
        _completionWindow.Closed += (_, _) => _completionWindow = null;
    }

    private string GetCurrentLineTextUpToCaret(int caretOffset)
    {
        var line = XmlEditor.Document.GetLineByOffset(caretOffset);
        var start = line.Offset;
        var length = caretOffset - start;
        if (length <= 0) return "";
        return XmlEditor.Document.GetText(start, length);
    }

    private static string? ExtractCurrentTagName(string lineText)
    {
        // Find the last '<' that opens a tag (not a closing tag)
        var lastOpen = lineText.LastIndexOf('<');
        if (lastOpen < 0) return null;
        var afterOpen = lineText[(lastOpen + 1)..].TrimStart();
        if (afterOpen.StartsWith('/') || afterOpen.StartsWith('!') || afterOpen.StartsWith('?'))
            return null;

        // Extract the tag name (up to first space or '>')
        var end = afterOpen.IndexOfAny([' ', '>', '/', '\t', '\n']);
        var tagName = end > 0 ? afterOpen[..end] : afterOpen;
        return tagName.Length > 0 && !tagName.Contains('=') ? tagName : null;
    }

    private static string? ExtractCurrentAttributeName(string lineText)
    {
        // Look for pattern: attributeName="
        var eqIdx = lineText.LastIndexOf("=\"");
        if (eqIdx < 1) return null;
        var beforeEq = lineText[..eqIdx].TrimEnd();
        var spaceIdx = beforeEq.LastIndexOfAny([' ', '\t']);
        var attrName = spaceIdx >= 0 ? beforeEq[(spaceIdx + 1)..] : beforeEq;
        return attrName.Length > 0 && attrName.All(c => char.IsLetterOrDigit(c) || c == '_') ? attrName : null;
    }

    private void TryAutoCloseTag()
    {
        var offset = XmlEditor.CaretOffset;
        if (offset < 2) return;

        // Check if we just typed '>' for an opening tag (not self-closing or closing)
        var textBefore = XmlEditor.Document.GetText(0, offset);
        var lastOpenBracket = textBefore.LastIndexOf('<');
        if (lastOpenBracket < 0) return;

        var tagContent = textBefore[(lastOpenBracket + 1)..^1]; // exclude the '>'
        if (tagContent.StartsWith('/') || tagContent.StartsWith('!') || tagContent.StartsWith('?'))
            return;
        if (tagContent.EndsWith('/'))
            return; // self-closing

        // Extract tag name
        var spaceIdx = tagContent.IndexOfAny([' ', '\t', '\n']);
        var tagName = spaceIdx > 0 ? tagContent[..spaceIdx] : tagContent;
        if (string.IsNullOrWhiteSpace(tagName)) return;

        // Self-closing elements that typically don't need closing tags
        var selfClosing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Action", "Initialize", "Ref"
        };

        if (selfClosing.Contains(tagName))
        {
            // Convert to self-closing: remove '>' and add ' />'
            XmlEditor.Document.Remove(offset - 1, 1);
            XmlEditor.Document.Insert(offset - 1, " />");
            XmlEditor.CaretOffset = offset + 2;
        }
        else
        {
            // Insert closing tag
            var closingTag = $"</{tagName}>";
            XmlEditor.Document.Insert(offset, closingTag);
            XmlEditor.CaretOffset = offset; // Keep caret between tags
        }
    }

    // ???????????????????????????????????????????????????????????????
    // NAVIGATION & SYNC
    // ???????????????????????????????????????????????????????????????

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TemplateXmlEditorViewModel.XmlText))
        {
            if (XmlEditor.Text != _vm.XmlText)
                XmlEditor.Text = _vm.XmlText;
        }
        else if (e.PropertyName == nameof(TemplateXmlEditorViewModel.IsPreviewVisible))
        {
            PreviewColumn.Width = _vm.IsPreviewVisible ? new GridLength(280) : new GridLength(0);
        }
    }

    private void NavigateToLine(int line, int position)
    {
        if (line < 1) return;

        Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var docLine = XmlEditor.Document.GetLineByNumber(
                    Math.Min(line, XmlEditor.Document.LineCount));

                var offset = docLine.Offset + Math.Max(0, position - 1);
                offset = Math.Min(offset, docLine.EndOffset);

                XmlEditor.CaretOffset = offset;
                XmlEditor.ScrollToLine(line);
                XmlEditor.TextArea.Caret.BringCaretToView();

                // Select the line to highlight the error
                XmlEditor.Select(docLine.Offset, docLine.Length);
                XmlEditor.TextArea.Focus();
            }
            catch
            {
                // Gracefully handle out-of-range navigation
            }
        });
    }

    private void UpdateCursorPosition()
    {
        var caret = XmlEditor.TextArea.Caret;
        CursorPositionText.Text = $"Ln {caret.Line}, Col {caret.Column}";
    }

    private static IHighlightingDefinition? LoadXmlHighlighting()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "TestControllerGrpc.Resources.XmlSyntaxHighlighting.xshd";

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null) return null;

            using var reader = new System.Xml.XmlTextReader(stream);
            return HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
        catch
        {
            return null;
        }
    }

    private void IncreaseFontSize_Click(object? sender, RoutedEventArgs e)
    {
        var sizes = _vm.AvailableFontSizes;
        var current = _vm.EditorFontSize;
        var next = sizes.FirstOrDefault(s => s > current);
        if (next > 0) _vm.EditorFontSize = next;
    }

    private void DecreaseFontSize_Click(object? sender, RoutedEventArgs e)
    {
        var sizes = _vm.AvailableFontSizes;
        var current = _vm.EditorFontSize;
        var prev = sizes.LastOrDefault(s => s < current);
        if (prev > 0) _vm.EditorFontSize = prev;
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        if (_foldingManager is not null)
        {
            FoldingManager.Uninstall(_foldingManager);
            _foldingManager = null;
        }
        base.OnClosed(e);
    }
}

/// <summary>
/// Simple completion data for XML IntelliSense.
/// </summary>
internal sealed class XmlCompletionData : ICompletionData
{
    public XmlCompletionData(string text, string description)
    {
        Text = text;
        Description = description;
    }

    public System.Windows.Media.ImageSource? Image => null;
    public string Text { get; }
    public object Content => Text;
    public object Description { get; }
    public double Priority => 0;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        textArea.Document.Replace(completionSegment, Text);
    }
}
