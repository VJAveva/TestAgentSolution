using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Core.Preflight;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels;

/// <summary>
/// ViewModel for the Pipeline Editor window (PipelineEditor_Requirement.md).
/// Provides structure-aware XML editing with validation, auto-format, auto-fix,
/// error panel, IntelliSense, and structure preview for WatchItem XML files.
///
/// WatchItem XML structure:
/// <![CDATA[
/// <WatchItem Tag="..." Path="..." Filter="...">
///     <Event Type="Renamed|Created|Changed" ExecutionType="Sequential|Parallel">
///         <ActionGroup Tag="..." ExecutionType="Sequential" FailAndContinue="true">
///             <Initialize Tag="..." ParameterFile="..." />
///             <Action Type="RunCommand|RunRemoteCommand|SendMail" ... />
///             <Ref TemplateID="..." />
///         </ActionGroup>
///     </Event>
/// </WatchItem>
/// ]]>
/// </summary>
public sealed partial class WatchItemXmlEditorViewModel : ObservableObject
{
    private readonly string _originalXml;
    private readonly WatchListConfig? _validationContext;
    private readonly IReadOnlyCollection<string>? _knownAgents;
    private readonly IPreflightFileSystem? _fileSystem;
    private readonly IReadOnlyCollection<XmlEditorAgent>? _agentStatuses;
    private readonly XmlEditorDocumentKind _documentKind;
    private int _analysisVersion;

    [ObservableProperty] private string _xmlText = "";
    [ObservableProperty] private string _windowTitle = "Pipeline Editor";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _statusSeverity = "Info"; // Info, Success, Error
    [ObservableProperty] private bool _isXmlValid = true;
    [ObservableProperty] private int _errorLine = -1;
    [ObservableProperty] private int _errorPosition = -1;
    [ObservableProperty] private double _editorFontSize = 13.0;

    public double[] AvailableFontSizes { get; } =
        [9, 10, 11, 12, 13, 14, 16, 18, 20, 24, 28, 32];

    // Summary panel
    [ObservableProperty] private string _watchItemSummary = "";
    [ObservableProperty] private string _watchItemTag = "";
    [ObservableProperty] private int _eventCount;
    [ObservableProperty] private int _totalActionCount;

    // Error panel
    [ObservableProperty] private bool _isErrorPanelVisible;
    public ObservableCollection<ValidationErrorViewModel> ValidationErrors { get; } = new();

    private ValidationErrorViewModel? _selectedValidationError;
    public ValidationErrorViewModel? SelectedValidationError
    {
        get => _selectedValidationError;
        set
        {
            if (SetProperty(ref _selectedValidationError, value) && value is not null)
                NavigateToError?.Invoke(value.Line, value.Column);
        }
    }

    // Structure preview
    /// <summary>Set to true when the user clicks Save and validation passes.</summary>
    public bool DialogAccepted { get; private set; }

    /// <summary>The final XML text to return to the caller after a successful save.</summary>
    public string ResultXml { get; private set; } = "";

    public bool HasUnsavedChanges => !DialogAccepted && XmlText != _originalXml;

    /// <summary>Asked (oldTag, newTag) before saving a renamed WatchItem; returning false keeps the editor open.</summary>
    public Func<string, string, bool>? ConfirmTagRename { get; set; }

    private readonly string _originalTag;

    /// <summary>Raised to request the owning Window to close.</summary>
    public event Action? RequestClose;

    /// <summary>Raised when the editor should navigate to a specific error line.</summary>
    public event Action<int, int>? NavigateToError;
    public event Action<XmlEditorQuickFix>? QuickFixRequested;

    public WatchItemXmlEditorViewModel(
        string initialXml,
        WatchListConfig? validationContext = null,
        IReadOnlyCollection<string>? knownAgents = null,
        IPreflightFileSystem? fileSystem = null,
        IReadOnlyCollection<XmlEditorAgent>? agentStatuses = null)
    {
        _originalXml = initialXml;
        _validationContext = validationContext;
        _knownAgents = knownAgents;
        _fileSystem = fileSystem;
        _agentStatuses = agentStatuses;
        _documentKind = DetectDocumentKind(initialXml);
        _originalTag = RootTag(initialXml) ?? "";
        XmlText = initialXml;
        StatusMessage = "Ready";
        StatusSeverity = "Info";
        UpdateSummary();
        RebuildStructure();
    }

    public async Task RefreshDiagnosticsAsync()
    {
        var version = Interlocked.Increment(ref _analysisVersion);
        var xml = XmlText;
        var analysis = await Task.Run(() => WatchListXmlEditorAnalyzer.Analyze(
            xml, _documentKind, _validationContext, _knownAgents, _fileSystem));
        if (version != Volatile.Read(ref _analysisVersion)) return;
        ShowErrors(ToValidationErrors(analysis));
    }

    [RelayCommand]
    private void ApplyQuickFix(XmlEditorQuickFix? fix)
    {
        if (fix is not null)
            QuickFixRequested?.Invoke(fix);
    }

    public string? GetQuickFixResult(XmlEditorQuickFix fix) =>
        WatchListXmlEditorAnalyzer.ApplyQuickFix(
            XmlText, _documentKind, fix, _validationContext);

    public IReadOnlyList<XmlEditorCompletion> GetValueCompletions(string attributeName, bool insideEvent) =>
        WatchListXmlEditorAnalyzer.GetValueCompletions(
            XmlText, _documentKind, attributeName, insideEvent,
            _validationContext, _agentStatuses);

    public IReadOnlyList<XmlEditorCompletion> GetTokenCompletions() =>
        WatchListXmlEditorAnalyzer.GetTokenCompletions(
            XmlText, _documentKind, _validationContext);

    public IReadOnlyList<XmlEditorCompletion> GetElementCompletions() =>
        WatchListXmlEditorAnalyzer.GetElementCompletions(_documentKind);

    public IReadOnlyList<XmlEditorCompletion> GetAttributeCompletions(string elementName) =>
        WatchListXmlEditorAnalyzer.GetAttributeCompletions(elementName);

    public string? GetHoverText(int offset) =>
        WatchListXmlEditorAnalyzer.GetHoverText(
            XmlText, _documentKind, offset, _validationContext, _agentStatuses);

    private static string? RootTag(string xml)
    {
        try { return XDocument.Parse(xml).Root?.Attribute("Tag")?.Value; }
        catch (XmlException) { return null; }
    }

    private static XmlEditorDocumentKind DetectDocumentKind(string xml)
    {
        try
        {
            return XDocument.Parse(xml).Root?.Name.LocalName == "WatchList"
                ? XmlEditorDocumentKind.WatchList
                : XmlEditorDocumentKind.WatchItem;
        }
        catch (XmlException)
        {
            return XmlEditorDocumentKind.WatchItem;
        }
    }

    private static List<ValidationErrorViewModel> ToValidationErrors(XmlEditorAnalysis analysis) =>
        analysis.Diagnostics.Select(d => new ValidationErrorViewModel
        {
            Line = d.Line,
            Column = d.Column,
            Length = d.Length,
            ErrorType = d.Code,
            Message = d.Message,
            Severity = d.Severity.ToString(),
            QuickFixes = d.QuickFixes,
        }).ToList();

    partial void OnXmlTextChanged(string value)
    {
        UpdateSummary();
        _structureDirty = true;
    }

    private void UpdateSummary()
    {
        try
        {
            var doc = XElement.Parse(XmlText);
            WatchItemTag = doc.Attribute("Tag")?.Value ?? "";
            var events = doc.Elements("Event").ToList();
            EventCount = events.Count;
            TotalActionCount = CountActions(doc);

            var path = doc.Attribute("Path")?.Value ?? "";
            var filter = doc.Attribute("Filter")?.Value ?? "";
            WatchItemSummary = $"{EventCount} event(s), {TotalActionCount} action(s) - {path}{filter}";
        }
        catch
        {
            WatchItemSummary = "Invalid XML";
        }
    }

    private static int CountActions(XElement el)
    {
        var count = el.Name.LocalName == "Action" ? 1 : 0;
        foreach (var child in el.Elements())
            count += CountActions(child);
        return count;
    }

    // ???????????????????????????????????????????????????????????????
    // COMMANDS
    // ???????????????????????????????????????????????????????????????

    [RelayCommand]
    private void Save()
    {
        var errors = RunFullValidation(XmlText);
        var blockingErrors = errors.Count(error => error.Severity == "Error");
        ShowErrors(errors, navigate: blockingErrors > 0);
        if (blockingErrors > 0)
        {
            StatusMessage = $"Cannot save {blockingErrors} validation error(s)";
            StatusSeverity = "Error";
            IsXmlValid = false;
            return;
        }

        if (_documentKind == XmlEditorDocumentKind.WatchItem
            && _originalTag.Length > 0
            && RootTag(XmlText) is { } newTag
            && !string.Equals(_originalTag, newTag, StringComparison.OrdinalIgnoreCase)
            && ConfirmTagRename?.Invoke(_originalTag, newTag) == false)
        {
            StatusMessage = "Save cancelled - Tag rename not confirmed";
            StatusSeverity = "Info";
            return;
        }

        DialogAccepted = true;
        ResultXml = XmlText;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        DialogAccepted = false;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Validate()
    {
        var errors = RunFullValidation(XmlText);
        ShowErrors(errors, navigate: true);
        var blockingErrors = errors.Count(error => error.Severity == "Error");
        if (blockingErrors == 0)
        {
            IsXmlValid = true;
            StatusMessage = errors.Count == 0
                ? "XML is valid - all structural rules passed"
                : $"XML is valid with {errors.Count} warning(s)";
            StatusSeverity = errors.Count == 0 ? "Success" : "Info";
        }
        else
        {
            StatusMessage = $"{blockingErrors} validation error(s) found";
            StatusSeverity = "Error";
            IsXmlValid = false;
        }
    }

    [RelayCommand]
    private void AutoFormat()
    {
        try
        {
            var doc = XDocument.Parse(XmlText);
            var settings = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                NewLineChars = "\n",
                NewLineHandling = NewLineHandling.Replace,
                OmitXmlDeclaration = true,
                Encoding = Encoding.UTF8,
            };

            using var sw = new StringWriter();
            using (var xw = XmlWriter.Create(sw, settings))
            {
                doc.WriteTo(xw);
            }

            XmlText = sw.ToString();
            IsXmlValid = true;
            ErrorLine = -1;
            ErrorPosition = -1;
            StatusMessage = "? XML formatted successfully";
            StatusSeverity = "Success";
        }
        catch (XmlException ex)
        {
            IsXmlValid = false;
            ErrorLine = ex.LineNumber;
            ErrorPosition = ex.LinePosition;
            StatusMessage = $"? Cannot format � Line {ex.LineNumber}, Pos {ex.LinePosition}: {ex.Message}";
            StatusSeverity = "Error";
            NavigateToError?.Invoke(ex.LineNumber, ex.LinePosition);
        }
        catch (Exception ex)
        {
            StatusMessage = $"? Cannot format � {ex.Message}";
            StatusSeverity = "Error";
        }
    }

    [RelayCommand]
    private void AutoFix()
    {
        try
        {
            var doc = XDocument.Parse(XmlText);
            var root = doc.Root;
            var watchItems = root?.Name.LocalName switch
            {
                "WatchItem" => new[] { root },
                "WatchList" => root.Elements("WatchItem").ToArray(),
                _ => null,
            };
            if (root is null || watchItems is null)
            {
                StatusMessage = "Cannot auto-fix - Root must be <WatchItem> or <WatchList>";
                StatusSeverity = "Error";
                return;
            }

            var fixCount = 0;

            foreach (var watchItem in watchItems)
            {
                if (watchItem.Attribute("Filter") is null)
                {
                    watchItem.SetAttributeValue("Filter", "*.*");
                    fixCount++;
                }

                foreach (var ev in watchItem.Elements("Event"))
                {
                    if (ev.Attribute("Type") is null)
                    {
                        ev.SetAttributeValue("Type", "Renamed");
                        fixCount++;
                    }
                    if (ev.Attribute("ExecutionType") is null)
                    {
                        ev.SetAttributeValue("ExecutionType", "Sequential");
                        fixCount++;
                    }
                    fixCount += FixActionNodes(ev.Descendants());
                }
            }

            if (root.Element("Templates") is { } templates)
                fixCount += FixActionNodes(templates.Descendants());

            // Re-serialize with formatting
            var settings = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                NewLineChars = "\n",
                NewLineHandling = NewLineHandling.Replace,
                OmitXmlDeclaration = true,
                Encoding = Encoding.UTF8,
            };

            using var sw = new StringWriter();
            using (var xw = XmlWriter.Create(sw, settings))
            {
                doc.WriteTo(xw);
            }

            XmlText = sw.ToString();

            if (fixCount > 0)
            {
                StatusMessage = $"? Auto-fixed {fixCount} issue(s) and reformatted";
                StatusSeverity = "Success";
            }
            else
            {
                StatusMessage = "No issues to fix � XML reformatted";
                StatusSeverity = "Info";
            }

            // Re-validate after fix
            var remaining = RunFullValidation(XmlText);
            if (remaining.Count > 0)
            {
                ShowErrors(remaining);
                var remainingErrors = remaining.Count(error => error.Severity == "Error");
                var withQuickFix = remaining.Count(error => error.QuickFixes.Count > 0);
                StatusMessage += $" ({remainingErrors} error(s), {remaining.Count - remainingErrors} warning(s) need manual edits"
                    + (withQuickFix > 0 ? $"; {withQuickFix} have a one-click fix in the error panel)" : ")");
                if (remainingErrors > 0)
                {
                    IsXmlValid = false;
                    StatusSeverity = "Error";
                }
            }
            else
            {
                IsXmlValid = true;
                ErrorLine = -1;
                ErrorPosition = -1;
                ValidationErrors.Clear();
                IsErrorPanelVisible = false;
            }
        }
        catch (XmlException ex)
        {
            StatusMessage = $"? Cannot auto-fix � XML is malformed: Line {ex.LineNumber}: {ex.Message}";
            StatusSeverity = "Error";
            NavigateToError?.Invoke(ex.LineNumber, ex.LinePosition);
        }
        catch (Exception ex)
        {
            StatusMessage = $"? Cannot auto-fix � {ex.Message}";
            StatusSeverity = "Error";
        }
    }

    private static int FixActionNodes(IEnumerable<XElement> nodes)
    {
        var fixCount = 0;
        foreach (var el in nodes)
        {
            switch (el.Name.LocalName)
            {
                case "ActionGroup":
                    if (el.Attribute("ExecutionType") is null)
                    {
                        el.SetAttributeValue("ExecutionType", "Sequential");
                        fixCount++;
                    }
                    fixCount += NormalizeBoolAttr(el, "FailAndContinue");
                    break;

                case "Action":
                    fixCount += NormalizeBoolAttr(el, "FailAndContinue");
                    fixCount += NormalizeBoolAttr(el, "IsReboot");
                    break;
            }
        }
        return fixCount;
    }

    /// <summary>Normalize True/False ? true/false for a boolean attribute. Returns 1 if fixed, 0 otherwise.</summary>
    private static int NormalizeBoolAttr(XElement el, string attrName)
    {
        if (el.Attribute(attrName) is not { } attr) return 0;
        if (string.Equals(attr.Value, "True", StringComparison.Ordinal))
        {
            attr.Value = "true";
            return 1;
        }
        if (string.Equals(attr.Value, "False", StringComparison.Ordinal))
        {
            attr.Value = "false";
            return 1;
        }
        return 0;
    }

    [RelayCommand]
    private void Revert()
    {
        XmlText = _originalXml;
        IsXmlValid = true;
        ErrorLine = -1;
        ErrorPosition = -1;
        ValidationErrors.Clear();
        IsErrorPanelVisible = false;
        StatusMessage = "Reverted to original";
        StatusSeverity = "Info";
    }

    [RelayCommand]
    private void NavigateToValidationError(ValidationErrorViewModel? error)
    {
        if (error is null) return;
        NavigateToError?.Invoke(error.Line, error.Column);
    }

    // ???????????????????????????????????????????????????????????????
    // VALIDATION
    // ???????????????????????????????????????????????????????????????

    private List<ValidationErrorViewModel> RunFullValidation(string xml)
    {
        var analysis = WatchListXmlEditorAnalyzer.Analyze(
            xml, _documentKind, _validationContext, _knownAgents, _fileSystem);
        return ToValidationErrors(analysis);
    }

    // ???????????????????????????????????????????????????????????????
    // ERROR PANEL
    // ???????????????????????????????????????????????????????????????

    // navigate: only for an explicit Validate/Save. The background re-check runs while the user types,
    // and moving the caret then sent it to the first error (often line 1) mid-edit.
    private void ShowErrors(List<ValidationErrorViewModel> errors, bool navigate = false)
    {
        ValidationErrors.Clear();
        foreach (var e in errors)
            ValidationErrors.Add(e);

        IsErrorPanelVisible = errors.Count > 0;
        IsXmlValid = errors.All(error => error.Severity != "Error");
        if (_structureDirty) RebuildStructure();
        else ApplyProblemsToStructure();

        var firstError = errors.FirstOrDefault(error => error.Severity == "Error") ?? errors.FirstOrDefault();
        if (firstError is not null)
        {
            ErrorLine = firstError.Line;
            ErrorPosition = firstError.Column;
            if (navigate)
                NavigateToError?.Invoke(firstError.Line, firstError.Column);
        }
    }

}
