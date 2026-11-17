using System.ComponentModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    private readonly PipelineEditorSessionViewModel _session;
    private WatchItemXmlEditorViewModel _vm;
    private readonly Dictionary<WatchItemXmlEditorViewModel, TextDocument> _documents = new();
    private FoldingManager? _foldingManager;
    private XmlFoldingStrategy? _foldingStrategy;
    private CompletionWindow? _completionWindow;
    private readonly DispatcherTimer _diagnosticsTimer;
    private XmlDiagnosticBackgroundRenderer? _diagnosticRenderer;

    public RawXmlEditorWindow(PipelineEditorSessionViewModel session)
    {
        InitializeComponent();
        _session = session;
        _vm = session.SelectedTab;
        DataContext = _session;

        _session.RequestClose += () => DialogResult = _session.DialogAccepted;
        _session.Confirm = message => Dialogs.ThemedMessageBox.Show(
            message, "Pipeline Editor", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        _session.ConfirmReload = message => Dialogs.ThemedMessageBox.Show(
            message, "Changed since you opened it", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        _session.PropertyChanged += OnSessionPropertyChanged;
        Closing += OnWindowClosing;

        _diagnosticsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _diagnosticsTimer.Tick += async (_, _) =>
        {
            _diagnosticsTimer.Stop();
            _vm.RefreshStructure();
            _vm.SyncSelectionToCaret(XmlEditor.CaretOffset);
            await _vm.RefreshDiagnosticsAsync();
        };

        // Set up AvalonEdit
        ConfigureEditor();
        ApplyThemeColors();
        _diagnosticRenderer = new XmlDiagnosticBackgroundRenderer(
            () => _vm.ValidationErrors,
            severity => TryFindResource(severity == "Error" ? "LogErrorBrush" : "LogWarnBrush") as Brush);
        XmlEditor.TextArea.TextView.BackgroundRenderers.Add(_diagnosticRenderer);

        // Two-way sync: editor ? ViewModel
        XmlEditor.TextChanged += (_, _) =>
        {
            if (_vm.XmlText != XmlEditor.Text)
                _vm.XmlText = XmlEditor.Text;
            UpdateFolding();
            _diagnosticsTimer.Stop();
            _diagnosticsTimer.Start();
        };

        // Track cursor position in status bar; the structure tree follows the caret
        XmlEditor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            UpdateCursorPosition();
            _vm.SyncSelectionToCaret(XmlEditor.CaretOffset);
        };
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

        _foldingStrategy = new XmlFoldingStrategy();
        Attach(_vm);
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PipelineEditorSessionViewModel.SelectedTab)
            || _session.SelectedTab is not { } tab || ReferenceEquals(tab, _vm)) return;
        Detach(_vm);
        _vm = tab;
        Attach(tab);
    }

    // Each tab keeps its own document, so its undo history survives switching tabs.
    private void Attach(WatchItemXmlEditorViewModel tab)
    {
        if (!_documents.TryGetValue(tab, out var document))
            _documents[tab] = document = new TextDocument(tab.XmlText);
        if (_foldingManager is not null) FoldingManager.Uninstall(_foldingManager);
        XmlEditor.Document = document;
        XmlEditor.IsReadOnly = tab.IsReadOnly;
        _foldingManager = FoldingManager.Install(XmlEditor.TextArea);
        UpdateFolding();

        tab.ConfirmTagRename ??= (oldTag, newTag) => Dialogs.ThemedMessageBox.Show(
            $"Renaming pipeline '{oldTag}' to '{newTag}' will unassign every engineer assigned to '{oldTag}', " +
            $"and a pipelines['{oldTag}'] override in its parameter file will stop applying.\n\nContinue?",
            "Pipeline Tag Rename", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        tab.NavigateToError += NavigateToLine;
        tab.QuickFixRequested += ApplyQuickFix;
        tab.NavigateToOffset += NavigateToOffset;
        tab.PropertyChanged += OnViewModelPropertyChanged;
        tab.ValidationErrors.CollectionChanged += OnDiagnosticsChanged;
        XmlEditor.TextArea.TextView.InvalidateVisual();
        _diagnosticsTimer.Stop();
        _diagnosticsTimer.Start();
    }

    private void Detach(WatchItemXmlEditorViewModel tab)
    {
        _completionWindow?.Close();
        TokenPopup.IsOpen = false;
        tab.NavigateToError -= NavigateToLine;
        tab.QuickFixRequested -= ApplyQuickFix;
        tab.NavigateToOffset -= NavigateToOffset;
        tab.PropertyChanged -= OnViewModelPropertyChanged;
        tab.ValidationErrors.CollectionChanged -= OnDiagnosticsChanged;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_session.HasUnsavedChanges) return;
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
        // Committing on punctuation inserted a suggestion whenever a space or quote was typed.
        if (_completionWindow is not null && e.Text.Length > 0
            && !XmlEditorTyping.KeepsCompletionOpen(e.Text[0]))
            _completionWindow.Close();
    }

    private void OnTextEntered(object sender, TextCompositionEventArgs e)
    {
        var before = TextBeforeCaret();
        if (e.Text == "<")
        {
            ShowElementCompletion();
        }
        else if (e.Text == " ")
        {
            if (XmlEditorTyping.CurrentElementName(before) is { } tagName)
                ShowAttributeCompletion(tagName);
        }
        else if (e.Text == "\"")
        {
            if (XmlEditorTyping.AttributeOpenedByQuote(before) is { } attrName)
                ShowValueCompletion(attrName);
        }
        else if (e.Text == "_" && before.EndsWith("[_", StringComparison.Ordinal))
        {
            ShowTokenCompletion();
        }
        else if (e.Text == ">")
        {
            TryAutoCloseTag(before);
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
    private bool IsInsideTag(string tagName) =>
        string.Equals(XmlEditorTyping.CurrentElementName(TextBeforeCaret()), tagName, StringComparison.Ordinal);

    // Not just the current line: attributes are often written one per line.
    private string TextBeforeCaret()
    {
        var offset = XmlEditor.CaretOffset;
        var start = Math.Max(0, offset - XmlEditorTyping.LookBehind);
        return XmlEditor.Document.GetText(start, offset - start);
    }

    private void TryAutoCloseTag(string before)
    {
        var offset = XmlEditor.CaretOffset;
        var after = XmlEditor.Document.GetText(offset, Math.Min(200, XmlEditor.Document.TextLength - offset));
        if (XmlEditorTyping.AutoClose(before, after) is not { } close) return;

        if (close.SelfClose)
        {
            XmlEditorQuickFixApplier.ReplaceRangeAsSingleUndo(XmlEditor.Document, offset - 1, 1, " />");
            XmlEditor.CaretOffset = offset + 2;
        }
        else
        {
            XmlEditor.Document.Insert(offset, $"</{close.Name}>");
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
    }

    private void NavigateToOffset(int offset)
    {
        if (offset < 0 || offset > XmlEditor.Document.TextLength) return;
        XmlEditor.TextArea.ClearSelection();
        XmlEditor.CaretOffset = offset;
        XmlEditor.ScrollToLine(XmlEditor.Document.GetLineByOffset(offset).LineNumber);
        XmlEditor.TextArea.Caret.BringCaretToView();
    }

    private void StructureTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is PipelineStructureNode node)
            _vm.SelectedStructureNode = node;
    }

    private void BrowseField_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PipelineFieldViewModel field) return;
        string? chosen;
        if (field.Kind == TestControllerGrpc.PipelineEditor.PipelineFieldKind.Folder)
        {
            var folder = new Microsoft.Win32.OpenFolderDialog { Title = field.Label };
            if (Directory.Exists(field.Value)) folder.InitialDirectory = field.Value;
            chosen = folder.ShowDialog(this) == true ? folder.FolderName : null;
        }
        else
        {
            var parametersDirectory = Path.Combine(AppContext.BaseDirectory, "Parameters");
            var file = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Parameter Files|*.json;*.txt|All Files|*.*",
                Title = field.Label,
                InitialDirectory = Directory.Exists(parametersDirectory) ? parametersDirectory : Environment.CurrentDirectory,
            };
            chosen = file.ShowDialog(this) == true ? file.FileName : null;
        }
        if (chosen is not null) field.Value = chosen;
    }

    private TextBox? _tokenTarget;

    // Typing "[_" in a form field opens the token list (same list as the XML completion).
    private void FieldText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox { IsReadOnly: false } box) return;
        var caret = box.CaretIndex;
        if (caret < 2 || box.Text.Substring(caret - 2, 2) != "[_")
        {
            if (ReferenceEquals(_tokenTarget, box)) TokenPopup.IsOpen = false;
            return;
        }
        var tokens = _vm.GetTokenCompletions();
        if (tokens.Count == 0) return;
        TokenList.ItemsSource = tokens;
        TokenList.SelectedIndex = 0;
        _tokenTarget = box;
        TokenPopup.PlacementTarget = box;
        TokenPopup.IsOpen = true;
    }

    private void FieldText_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!TokenPopup.IsOpen || !ReferenceEquals(sender, _tokenTarget)) return;
        switch (e.Key)
        {
            case Key.Down:
                TokenList.SelectedIndex = Math.Min(TokenList.SelectedIndex + 1, TokenList.Items.Count - 1);
                TokenList.ScrollIntoView(TokenList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                TokenList.SelectedIndex = Math.Max(TokenList.SelectedIndex - 1, 0);
                TokenList.ScrollIntoView(TokenList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter or Key.Tab:
                InsertSelectedToken();
                e.Handled = true;
                break;
            case Key.Escape:
                TokenPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void TokenList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => InsertSelectedToken();

    private void InsertSelectedToken()
    {
        TokenPopup.IsOpen = false;
        if (_tokenTarget is not { } box || TokenList.SelectedItem is not XmlEditorCompletion token) return;
        var caret = box.CaretIndex;
        if (caret < 2) return;
        box.Text = box.Text[..(caret - 2)] + token.Text + box.Text[caret..];
        box.CaretIndex = caret - 2 + token.Text.Length;
        box.Focus();
    }

    // Same rules as the main tree's menu (NodeMenuProvider), limited to the editor's items; built when it opens.
    private void StructureTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = ((FrameworkElement)sender).ContextMenu;
        menu.Items.Clear();
        if (_vm.SelectedStructureNode is not { } node)
        {
            e.Handled = true;
            return;
        }

        TestControllerGrpc.Menus.NodeMenuGroup? lastGroup = null;
        foreach (var item in _vm.StructureMenuFor(node))
        {
            if (lastGroup is not null && item.Group != lastGroup)
                menu.Items.Add(new Separator());
            lastGroup = item.Group;
            menu.Items.Add(ToMenuItem(item, node));
        }
        if (menu.Items.Count == 0) e.Handled = true;
    }

    private MenuItem ToMenuItem(TestControllerGrpc.Menus.NodeMenuItem item, PipelineStructureNode node)
    {
        var menuItem = new MenuItem
        {
            Header = item.Label,
            IsEnabled = item.IsEnabled,
            IsCheckable = item.IsChecked is not null,
            IsChecked = item.IsChecked == true,
            ToolTip = item.ToolTip,
        };
        ToolTipService.SetShowOnDisabled(menuItem, true);
        if (item.Children.Count > 0)
        {
            foreach (var child in item.Children)
                menuItem.Items.Add(ToMenuItem(child, node));
            return menuItem;
        }

        menuItem.Click += (_, _) =>
        {
            string? input = null;
            if (item.Id == TestControllerGrpc.Menus.NodeMenuIds.Skip)
            {
                var dialog = new Dialogs.SkipReasonDialog(node.Name, null) { Owner = this };
                if (dialog.ShowDialog() != true) return;
                input = dialog.Reason;
            }
            else if (item.Id == TestControllerGrpc.Menus.NodeMenuIds.SaveAsTemplate)
            {
                var dialog = new Dialogs.TemplateNameDialog(node.Name) { Owner = this };
                if (dialog.ShowDialog() != true) return;
                input = dialog.TemplateName;
            }
            else if (item.Id == PipelineEditorMenuIds.CopyTemplateHere
                     && Dialogs.ThemedMessageBox.Show(
                         $"This pipeline will no longer follow changes to template '{node.Name}'.\n\nCopy its steps here?",
                         "Copy for this pipeline only", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }
            _vm.RunStructureCommand(item.Id, node, input);
            XmlEditor.Focus();
        };
        return menuItem;
    }

    // The form edits the XML document, so its undo history is the XML editor's (one step per change).
    private void FormView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.Z) e.Handled = XmlEditor.Undo();
        else if (e.Key == Key.Y) e.Handled = XmlEditor.Redo();
    }

    // Requirement 4.2: double-click or Enter on a template call (or a step under it) opens Edit template.
    private void StructureTree_MouseDoubleClick(object sender, MouseButtonEventArgs e) => EditSelectedTemplate(e);

    private void StructureTree_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) EditSelectedTemplate(e);
    }

    private void EditSelectedTemplate(RoutedEventArgs e)
    {
        if (_vm.SelectedStructureNode is not { } node || node.Kind != TestControllerGrpc.Models.NodeKinds.Ref && !node.IsFromTemplate)
            return;
        _vm.RunStructureCommand(TestControllerGrpc.Menus.NodeMenuIds.EditTemplate, node);
        e.Handled = true;
    }

    // Setting TextEditor.Text clears the undo stack and resets the caret, so Format/Auto-fix/Revert go through the document.
    private void ReplaceTextUndoable(string text) =>
        XmlEditorQuickFixApplier.ReplaceAsSingleUndo(XmlEditor.Document, text);

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
        Detach(_vm);
        _session.PropertyChanged -= OnSessionPropertyChanged;
        XmlEditor.MouseHover -= OnEditorMouseHover;
        XmlEditor.MouseHoverStopped -= OnEditorMouseHoverStopped;
        if (_diagnosticRenderer is not null)
            XmlEditor.TextArea.TextView.BackgroundRenderers.Remove(_diagnosticRenderer);
        if (_foldingManager is not null)
        {
            FoldingManager.Uninstall(_foldingManager);
            _foldingManager = null;
        }
        base.OnClosed(e);
    }
}
