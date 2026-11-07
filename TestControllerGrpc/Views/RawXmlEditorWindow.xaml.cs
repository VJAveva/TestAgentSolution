using System.ComponentModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Highlighting;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Views;

public partial class RawXmlEditorWindow : Window
{
    private readonly WatchItemXmlEditorViewModel _vm;
    private FoldingManager? _foldingManager;
    private XmlFoldingStrategy? _foldingStrategy;
    private CompletionWindow? _completionWindow;
    private readonly DispatcherTimer _diagnosticsTimer;
    private XmlDiagnosticBackgroundRenderer? _diagnosticRenderer;

    public RawXmlEditorWindow(WatchItemXmlEditorViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = _vm;

        // Wire close request
        _vm.RequestClose += () => DialogResult = _vm.DialogAccepted;
        Closing += OnWindowClosing;
        _vm.ConfirmTagRename ??= (oldTag, newTag) => Dialogs.ThemedMessageBox.Show(
            $"Renaming pipeline '{oldTag}' to '{newTag}' will unassign every engineer assigned to '{oldTag}', " +
            $"and a pipelines['{oldTag}'] override in its parameter file will stop applying.\n\nContinue?",
            "Pipeline Tag Rename", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

        // Wire error navigation
        _vm.NavigateToError += NavigateToLine;
        _vm.QuickFixRequested += ApplyQuickFix;
        _diagnosticsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _diagnosticsTimer.Tick += async (_, _) =>
        {
            _diagnosticsTimer.Stop();
            await _vm.RefreshDiagnosticsAsync();
        };

        // Wire preview visibility changes
        _vm.PropertyChanged += OnViewModelPropertyChanged;

        // Set up AvalonEdit
        ConfigureEditor();
        ApplyThemeColors();
        _diagnosticRenderer = new XmlDiagnosticBackgroundRenderer(
            () => _vm.ValidationErrors,
            severity => TryFindResource(severity == "Error" ? "LogErrorBrush" : "LogWarnBrush") as Brush);
        XmlEditor.TextArea.TextView.BackgroundRenderers.Add(_diagnosticRenderer);
        _vm.ValidationErrors.CollectionChanged += OnDiagnosticsChanged;

        // Load initial text
        XmlEditor.Text = _vm.XmlText;

        // Two-way sync: editor ? ViewModel
        XmlEditor.TextChanged += (_, _) =>
        {
            if (_vm.XmlText != XmlEditor.Text)
                _vm.XmlText = XmlEditor.Text;
            UpdateFolding();
            _diagnosticsTimer.Stop();
            _diagnosticsTimer.Start();
        };

        // Track cursor position in status bar
        XmlEditor.TextArea.Caret.PositionChanged += (_, _) => UpdateCursorPosition();
        XmlEditor.MouseHover += OnEditorMouseHover;
        XmlEditor.MouseHoverStopped += OnEditorMouseHoverStopped;

        // IntelliSense: trigger completion on typing
        XmlEditor.TextArea.TextEntering += OnTextEntering;
        XmlEditor.TextArea.TextEntered += OnTextEntered;

        // Keyboard shortcuts for font size
        InputBindings.Add(new KeyBinding(
            new RelayCommand(() => IncreaseFontSize_Click(null, null!)),
            new KeyGesture(Key.OemPlus, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(
            new RelayCommand(() => DecreaseFontSize_Click(null, null!)),
            new KeyGesture(Key.OemMinus, ModifierKeys.Control)));

        // Initial folding
        SetupFolding();
        _diagnosticsTimer.Start();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_vm.HasUnsavedChanges) return;
        var answer = Dialogs.ThemedMessageBox.Show(
            "You have unsaved XML changes. Discard them and close?",
            "Unsaved Changes", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        e.Cancel = answer != MessageBoxResult.Yes;
    }

    private void ApplyQuickFix(XmlEditorQuickFix fix)
    {
        if (fix.Kind == XmlEditorQuickFixKind.OpenParameterFileGlobal)
        {
            try
            {
                Process.Start(new ProcessStartInfo(fix.Value) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                       or InvalidOperationException or FileNotFoundException)
            {
                MessageBox.Show(this, ex.Message, "Could not open parameter file",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return;
        }

        var replacement = _vm.GetQuickFixResult(fix);
        if (replacement is null || replacement == XmlEditor.Text) return;

        XmlEditorQuickFixApplier.ReplaceAsSingleUndo(XmlEditor.Document, replacement);
    }

    private void BrowseParameterFile_Click(object sender, RoutedEventArgs e)
    {
        var text = XmlEditor.Document.Text;
        const string attributePrefix = "ParameterFile=\"";
        if (text.Length < attributePrefix.Length)
        {
            MessageBox.Show(this, "Place the caret inside an Initialize ParameterFile value first.",
                "Parameter file", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var searchEnd = Math.Min(XmlEditor.CaretOffset, text.Length - 1);
        var attributeStart = text.LastIndexOf(attributePrefix, searchEnd, StringComparison.OrdinalIgnoreCase);
        if (attributeStart < 0 || XmlEditor.CaretOffset < attributeStart + attributePrefix.Length)
        {
            MessageBox.Show(this, "Place the caret inside an Initialize ParameterFile value first.",
                "Parameter file", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var valueStart = attributeStart + attributePrefix.Length;
        var valueEnd = text.IndexOf('"', XmlEditor.CaretOffset);
        if (valueEnd < valueStart)
        {
            MessageBox.Show(this, "Complete the ParameterFile attribute quotes before browsing.",
                "Parameter file", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var parametersDirectory = Path.Combine(AppContext.BaseDirectory, "Parameters");
        if (!Directory.Exists(parametersDirectory))
            parametersDirectory = Path.Combine(Environment.CurrentDirectory, "Parameters");
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Parameter Files|*.json;*.txt|All Files|*.*",
            Title = "Select Parameter File",
            InitialDirectory = Directory.Exists(parametersDirectory)
                ? parametersDirectory
                : Environment.CurrentDirectory,
        };
        if (dialog.ShowDialog(this) != true) return;

        var escapedPath = System.Security.SecurityElement.Escape(dialog.FileName) ?? dialog.FileName;
        XmlEditorQuickFixApplier.ReplaceRangeAsSingleUndo(
            XmlEditor.Document, valueStart, valueEnd - valueStart, escapedPath);
    }

    private void OnDiagnosticsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        XmlEditor.TextArea.TextView.InvalidateVisual();

    private void OnEditorMouseHover(object sender, MouseEventArgs e)
    {
        var textView = XmlEditor.TextArea.TextView;
        var position = textView.GetPosition(e.GetPosition(textView));
        if (position is null) return;

        var offset = XmlEditor.Document.GetOffset(position.Value.Location);
        var tooltip = _vm.GetHoverText(offset);
        if (tooltip is null) return;
        XmlEditor.ToolTip = tooltip;
        System.Windows.Controls.ToolTipService.SetShowDuration(XmlEditor, 60_000);
    }

    private void OnEditorMouseHoverStopped(object sender, MouseEventArgs e) =>
        XmlEditor.ToolTip = null;

    private void ConfigureEditor()
    {
        ApplyHighlighting(ThemeService.CurrentTheme);

        // ThemeChanged is static-rooted, so this window must unhook itself or it is leaked.
        ThemeService.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => ThemeService.ThemeChanged -= OnThemeChanged;

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
            var offset = XmlEditor.CaretOffset;
            var lineText = GetCurrentLineTextUpToCaret(offset);
            var tagName = ExtractCurrentTagName(lineText);
            if (tagName is not null)
                ShowAttributeCompletion(tagName);
        }
        else if (e.Text == "\"")
        {
            var offset = XmlEditor.CaretOffset;
            var lineText = GetCurrentLineTextUpToCaret(offset);
            var attrName = ExtractCurrentAttributeName(lineText);
            if (attrName is not null)
                ShowValueCompletion(attrName);
        }
        else if (e.Text == "_" && XmlEditor.CaretOffset >= 2
                 && XmlEditor.Document.GetText(XmlEditor.CaretOffset - 2, 2) == "[_")
        {
            ShowTokenCompletion();
        }
        else if (e.Text == ">")
        {
            TryAutoCloseTag();
        }
    }

    private void ShowElementCompletion()
    {
        var entries = _vm.GetElementCompletions();
        if (entries.Count == 0) return;
        _completionWindow = new CompletionWindow(XmlEditor.TextArea);
        var data = _completionWindow.CompletionList.CompletionData;
        foreach (var entry in entries)
            data.Add(new XmlCompletionData(entry.Text, entry.Description));
        _completionWindow.Show();
        _completionWindow.Closed += (_, _) => _completionWindow = null;
    }

    private void ShowAttributeCompletion(string tagName)
    {
        var entries = _vm.GetAttributeCompletions(tagName);
        if (entries.Count == 0) return;
        _completionWindow = new CompletionWindow(XmlEditor.TextArea);
        var data = _completionWindow.CompletionList.CompletionData;
        foreach (var entry in entries)
            data.Add(new XmlCompletionData(entry.Text, entry.Description));
        _completionWindow.Show();
        _completionWindow.Closed += (_, _) => _completionWindow = null;
    }

    private void ShowValueCompletion(string attrName)
    {
        var values = _vm.GetValueCompletions(attrName, IsInsideTag("Event"));
        if (values.Count == 0) return;

        _completionWindow = new CompletionWindow(XmlEditor.TextArea);
        var data = _completionWindow.CompletionList.CompletionData;
        foreach (var value in values)
            data.Add(new XmlCompletionData(value.Text, value.Description));

        _completionWindow.Show();
        _completionWindow.Closed += (_, _) => _completionWindow = null;
    }

    private void ShowTokenCompletion()
    {
        var values = _vm.GetTokenCompletions();
        if (values.Count == 0) return;
        _completionWindow = new CompletionWindow(XmlEditor.TextArea)
        {
            StartOffset = XmlEditor.CaretOffset - 2,
        };
        foreach (var value in values)
            _completionWindow.CompletionList.CompletionData.Add(
                new XmlCompletionData(value.Text, value.Description));
        _completionWindow.Show();
        _completionWindow.Closed += (_, _) => _completionWindow = null;
    }

    /// <summary>Checks if the caret is currently inside a specific tag name.</summary>
    private bool IsInsideTag(string tagName)
    {
        var offset = XmlEditor.CaretOffset;
        var lineText = GetCurrentLineTextUpToCaret(offset);
        var extractedTag = ExtractCurrentTagName(lineText);
        return string.Equals(extractedTag, tagName, StringComparison.Ordinal);
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
        var lastOpen = lineText.LastIndexOf('<');
        if (lastOpen < 0) return null;
        var afterOpen = lineText[(lastOpen + 1)..].TrimStart();
        if (afterOpen.StartsWith('/') || afterOpen.StartsWith('!') || afterOpen.StartsWith('?'))
            return null;

        var end = afterOpen.IndexOfAny([' ', '>', '/', '\t', '\n']);
        var tagName = end > 0 ? afterOpen[..end] : afterOpen;
        return tagName.Length > 0 && !tagName.Contains('=') ? tagName : null;
    }

    private static string? ExtractCurrentAttributeName(string lineText)
    {
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

        var textBefore = XmlEditor.Document.GetText(0, offset);
        var lastOpenBracket = textBefore.LastIndexOf('<');
        if (lastOpenBracket < 0) return;

        var tagContent = textBefore[(lastOpenBracket + 1)..^1]; // exclude the '>'
        if (tagContent.StartsWith('/') || tagContent.StartsWith('!') || tagContent.StartsWith('?'))
            return;
        if (tagContent.EndsWith('/'))
            return; // self-closing

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
            XmlEditorQuickFixApplier.ReplaceRangeAsSingleUndo(XmlEditor.Document, offset - 1, 1, " />");
            XmlEditor.CaretOffset = offset + 2;
        }
        else
        {
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
        if (e.PropertyName == nameof(WatchItemXmlEditorViewModel.XmlText))
        {
            if (XmlEditor.Text != _vm.XmlText)
                ReplaceTextUndoable(_vm.XmlText);
        }
        else if (e.PropertyName == nameof(WatchItemXmlEditorViewModel.IsPreviewVisible))
        {
            PreviewColumn.Width = _vm.IsPreviewVisible ? new GridLength(280) : new GridLength(0);
        }
    }

    // Setting TextEditor.Text clears the undo stack, so Format/Auto-fix/Revert go through the document instead.
    private void ReplaceTextUndoable(string text)
    {
        var caret = XmlEditor.CaretOffset;
        XmlEditorQuickFixApplier.ReplaceAsSingleUndo(XmlEditor.Document, text);
        XmlEditor.CaretOffset = Math.Min(caret, XmlEditor.Document.TextLength);
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

                // Caret only: a selected line is replaced by the next keystroke. The diagnostic renderer marks the error.
                XmlEditor.TextArea.ClearSelection();
                XmlEditor.CaretOffset = offset;
                XmlEditor.ScrollToLine(line);
                XmlEditor.TextArea.Caret.BringCaretToView();
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

    private void OnThemeChanged(string themeName) =>
        Dispatcher.InvokeAsync(() =>
        {
            ApplyHighlighting(themeName);
            ApplyThemeColors();
        });

    private void ApplyHighlighting(string? themeName) =>
        XmlEditor.SyntaxHighlighting = XmlHighlightingProvider.ForThemeOrDefault(themeName);

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
        _diagnosticsTimer.Stop();
        _vm.QuickFixRequested -= ApplyQuickFix;
        XmlEditor.MouseHover -= OnEditorMouseHover;
        XmlEditor.MouseHoverStopped -= OnEditorMouseHoverStopped;
        _vm.ValidationErrors.CollectionChanged -= OnDiagnosticsChanged;
        if (_diagnosticRenderer is not null)
            XmlEditor.TextArea.TextView.BackgroundRenderers.Remove(_diagnosticRenderer);
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        if (_foldingManager is not null)
        {
            FoldingManager.Uninstall(_foldingManager);
            _foldingManager = null;
        }
        base.OnClosed(e);
    }
}
