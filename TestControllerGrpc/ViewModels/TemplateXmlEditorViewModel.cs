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
/// Represents a single validation error for the Template XML editor error panel.
/// </summary>
public sealed partial class ValidationErrorViewModel : ObservableObject
{
    [ObservableProperty] private int _line;
    [ObservableProperty] private int _column;
    [ObservableProperty] private int _length = 1;
    [ObservableProperty] private string _errorType = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _severity = "Error";
    public IReadOnlyList<XmlEditorQuickFix> QuickFixes { get; set; } = [];
}

/// <summary>
/// Represents a node in the structure preview tree.
/// </summary>
public sealed partial class TemplatePreviewNode : ObservableObject
{
    [ObservableProperty] private string _displayText = "";
    [ObservableProperty] private string _nodeIcon = "";
    [ObservableProperty] private bool _isExpanded = true;
    public ObservableCollection<TemplatePreviewNode> Children { get; } = new();
}

/// <summary>
/// ViewModel for the standalone Template XML Editor window.
/// Provides structure-aware XML editing with validation, auto-format, auto-fix,
/// error panel, and structure preview for Templates XML files.
/// </summary>
public sealed partial class TemplateXmlEditorViewModel : ObservableObject
{
    private readonly string _originalXml;
    private readonly WatchListConfig? _validationContext;
    private readonly IReadOnlyCollection<string>? _knownAgents;
    private readonly IPreflightFileSystem? _fileSystem;
    private readonly IReadOnlyCollection<XmlEditorAgent>? _agentStatuses;
    private int _analysisVersion;

    [ObservableProperty] private string _xmlText = "";
    [ObservableProperty] private string _windowTitle = "Template XML Editor";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _statusSeverity = "Info"; // Info, Success, Error
    [ObservableProperty] private bool _isXmlValid = true;
    [ObservableProperty] private int _errorLine = -1;
    [ObservableProperty] private int _errorPosition = -1;
    [ObservableProperty] private double _editorFontSize = 13.0;

    public double[] AvailableFontSizes { get; } =
        [9, 10, 11, 12, 13, 14, 16, 18, 20, 24, 28, 32];

    // Summary panel
    [ObservableProperty] private string _templateSummary = "";
    [ObservableProperty] private int _templateCount;
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
    [ObservableProperty] private bool _isPreviewVisible;
    public ObservableCollection<TemplatePreviewNode> PreviewRoots { get; } = new();

    /// <summary>Set to true when the user clicks Save and validation passes.</summary>
    public bool DialogAccepted { get; private set; }

    /// <summary>The final XML text to return to the caller after a successful save.</summary>
    public string ResultXml { get; private set; } = "";

    /// <summary>Raised to request the owning Window to close.</summary>
    public event Action? RequestClose;

    /// <summary>Raised when the editor should navigate to a specific error line.</summary>
    public event Action<int, int>? NavigateToError;
    public event Action<XmlEditorQuickFix>? QuickFixRequested;

    public TemplateXmlEditorViewModel(
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
        XmlText = initialXml;
        StatusMessage = "Ready";
        StatusSeverity = "Info";
        UpdateSummary();
    }

    public async Task RefreshDiagnosticsAsync()
    {
        var version = Interlocked.Increment(ref _analysisVersion);
        var xml = XmlText;
        var analysis = await Task.Run(() => WatchListXmlEditorAnalyzer.Analyze(
            xml, XmlEditorDocumentKind.Templates, _validationContext, _knownAgents, _fileSystem));
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
            XmlText, XmlEditorDocumentKind.Templates, fix, _validationContext);

    public IReadOnlyList<XmlEditorCompletion> GetValueCompletions(string attributeName, bool insideEvent) =>
        WatchListXmlEditorAnalyzer.GetValueCompletions(
            XmlText, XmlEditorDocumentKind.Templates, attributeName, insideEvent,
            _validationContext, _agentStatuses);

    public IReadOnlyList<XmlEditorCompletion> GetTokenCompletions() =>
        WatchListXmlEditorAnalyzer.GetTokenCompletions(
            XmlText, XmlEditorDocumentKind.Templates, _validationContext);

    public IReadOnlyList<XmlEditorCompletion> GetElementCompletions() =>
        WatchListXmlEditorAnalyzer.GetElementCompletions(XmlEditorDocumentKind.Templates);

    public IReadOnlyList<XmlEditorCompletion> GetAttributeCompletions(string elementName) =>
        WatchListXmlEditorAnalyzer.GetAttributeCompletions(elementName);

    public string? GetHoverText(int offset) =>
        WatchListXmlEditorAnalyzer.GetHoverText(
            XmlText, XmlEditorDocumentKind.Templates, offset, _validationContext, _agentStatuses);

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

    partial void OnXmlTextChanged(string value) => UpdateSummary();

    private void UpdateSummary()
    {
        try
        {
            var doc = XElement.Parse(XmlText);
            var templates = doc.Elements("Template").ToList();
            TemplateCount = templates.Count;
            TotalActionCount = CountActions(doc);

            var ids = templates.Select(t => t.Attribute("ID")?.Value ?? "?");
            TemplateSummary = $"{TemplateCount} template(s), {TotalActionCount} action(s)";

            // Update preview if visible
            if (IsPreviewVisible)
                RebuildPreview(doc);
        }
        catch
        {
            TemplateSummary = "Invalid XML";
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
        ShowErrors(errors);
        if (blockingErrors > 0)
        {
            StatusMessage = $"Cannot save - {blockingErrors} validation error(s)";
            StatusSeverity = "Error";
            IsXmlValid = false;
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
        ShowErrors(errors);
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
            StatusMessage = "XML formatted successfully";
            StatusSeverity = "Success";
        }
        catch (XmlException ex)
        {
            IsXmlValid = false;
            ErrorLine = ex.LineNumber;
            ErrorPosition = ex.LinePosition;
            StatusMessage = $"Cannot format - Line {ex.LineNumber}, Pos {ex.LinePosition}: {ex.Message}";
            StatusSeverity = "Error";
            NavigateToError?.Invoke(ex.LineNumber, ex.LinePosition);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Cannot format - {ex.Message}";
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
            if (root is null || root.Name.LocalName != "Templates")
            {
                StatusMessage = "Cannot auto-fix - Root must be <Templates>";
                StatusSeverity = "Error";
                return;
            }

            var fixCount = 0;

            foreach (var template in root.Elements("Template"))
            {
                foreach (var el in template.Descendants())
                {
                    switch (el.Name.LocalName)
                    {
                        case "ActionGroup":
                            if (el.Attribute("ExecutionType") is null)
                            {
                                el.SetAttributeValue("ExecutionType", "Sequential");
                                fixCount++;
                            }
                            if (el.Attribute("FailAndContinue") is { } fac)
                            {
                                // Normalize boolean value
                                if (string.Equals(fac.Value, "True", StringComparison.Ordinal))
                                {
                                    fac.Value = "true";
                                    fixCount++;
                                }
                                else if (string.Equals(fac.Value, "False", StringComparison.Ordinal))
                                {
                                    fac.Value = "false";
                                    fixCount++;
                                }
                            }
                            break;

                        case "Action":
                            // Normalize FailAndContinue
                            if (el.Attribute("FailAndContinue") is { } facAction)
                            {
                                if (string.Equals(facAction.Value, "True", StringComparison.Ordinal))
                                {
                                    facAction.Value = "true";
                                    fixCount++;
                                }
                                else if (string.Equals(facAction.Value, "False", StringComparison.Ordinal))
                                {
                                    facAction.Value = "false";
                                    fixCount++;
                                }
                            }
                            // Normalize IsReboot
                            if (el.Attribute("IsReboot") is { } isReboot)
                            {
                                if (string.Equals(isReboot.Value, "True", StringComparison.Ordinal))
                                {
                                    isReboot.Value = "true";
                                    fixCount++;
                                }
                                else if (string.Equals(isReboot.Value, "False", StringComparison.Ordinal))
                                {
                                    isReboot.Value = "false";
                                    fixCount++;
                                }
                            }
                            break;
                    }
                }
            }

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
                StatusMessage = $"Auto-fixed {fixCount} issue(s) and reformatted";
                StatusSeverity = "Success";
            }
            else
            {
                StatusMessage = "No issues to fix - XML reformatted";
                StatusSeverity = "Info";
            }

            // Re-validate after fix
            var remaining = RunFullValidation(XmlText);
            if (remaining.Count > 0)
            {
                ShowErrors(remaining);
                StatusMessage += $" ({remaining.Count} remaining error(s))";
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
            StatusMessage = $"Cannot auto-fix - XML is malformed: Line {ex.LineNumber}: {ex.Message}";
            StatusSeverity = "Error";
            NavigateToError?.Invoke(ex.LineNumber, ex.LinePosition);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Cannot auto-fix - {ex.Message}";
            StatusSeverity = "Error";
        }
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
    private void TogglePreview()
    {
        IsPreviewVisible = !IsPreviewVisible;
        if (IsPreviewVisible)
        {
            try
            {
                var doc = XElement.Parse(XmlText);
                RebuildPreview(doc);
            }
            catch
            {
                PreviewRoots.Clear();
                PreviewRoots.Add(new TemplatePreviewNode { DisplayText = "Invalid XML", NodeIcon = "!" });
            }
        }
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
            xml, XmlEditorDocumentKind.Templates, _validationContext, _knownAgents, _fileSystem);
        return ToValidationErrors(analysis);
    }

    private List<ValidationErrorViewModel> RunLegacyValidation(string xml)
    {
        var errors = new List<ValidationErrorViewModel>();

        if (string.IsNullOrWhiteSpace(xml))
        {
            errors.Add(new ValidationErrorViewModel
            {
                Line = 1, Column = 1,
                ErrorType = "Empty", Message = "XML text is empty."
            });
            return errors;
        }

        // A. XML Well-Formed check
        XElement root;
        try
        {
            root = XElement.Parse(xml, LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            errors.Add(new ValidationErrorViewModel
            {
                Line = ex.LineNumber, Column = ex.LinePosition,
                ErrorType = "Syntax", Message = ex.Message
            });
            return errors;
        }

        // B. Root must be <Templates>
        if (root.Name.LocalName != "Templates")
        {
            var li = (IXmlLineInfo)root;
            errors.Add(new ValidationErrorViewModel
            {
                Line = li.HasLineInfo() ? li.LineNumber : 1,
                Column = li.HasLineInfo() ? li.LinePosition : 1,
                ErrorType = "Structure", Message = "Root element must be <Templates>."
            });
            return errors;
        }

        // Validate each Template
        foreach (var template in root.Elements())
        {
            var tli = (IXmlLineInfo)template;
            var tLine = tli.HasLineInfo() ? tli.LineNumber : 1;
            var tCol = tli.HasLineInfo() ? tli.LinePosition : 1;

            if (template.Name.LocalName != "Template")
            {
                errors.Add(new ValidationErrorViewModel
                {
                    Line = tLine, Column = tCol,
                    ErrorType = "Structure",
                    Message = $"Unexpected element <{template.Name.LocalName}> under <Templates>. Expected <Template>."
                });
                continue;
            }

            // Each Template must have ID
            if (template.Attribute("ID") is null || string.IsNullOrWhiteSpace(template.Attribute("ID")?.Value))
            {
                errors.Add(new ValidationErrorViewModel
                {
                    Line = tLine, Column = tCol,
                    ErrorType = "Attribute", Message = "<Template> missing required attribute: ID"
                });
            }

            // Validate children recursively
            ValidateChildren(template, errors);
        }

        return errors;
    }

    private static void ValidateChildren(XElement parent, List<ValidationErrorViewModel> errors)
    {
        foreach (var el in parent.Elements())
        {
            var li = (IXmlLineInfo)el;
            var line = li.HasLineInfo() ? li.LineNumber : 1;
            var col = li.HasLineInfo() ? li.LinePosition : 1;
            var name = el.Name.LocalName;

            switch (name)
            {
                case "ActionGroup":
                    RequireAttr(el, "ExecutionType", errors, line, col);
                    ValidateChildren(el, errors);
                    break;

                case "Action":
                    var actionType = el.Attribute("Type")?.Value ?? "";
                    if (string.IsNullOrWhiteSpace(actionType))
                    {
                        errors.Add(new ValidationErrorViewModel
                        {
                            Line = line, Column = col,
                            ErrorType = "Attribute", Message = "<Action> missing required attribute: Type"
                        });
                    }
                    else if (actionType == "RunRemoteCommand")
                    {
                        RequireAttr(el, "AgentName", errors, line, col);
                        RequireAttr(el, "Command", errors, line, col);
                        RequireAttr(el, "Timeout", errors, line, col);
                    }
                    else if (actionType == "SendMail")
                    {
                        RequireAttr(el, "From", errors, line, col);
                        RequireAttr(el, "To", errors, line, col);
                        RequireAttr(el, "Title", errors, line, col);
                    }
                    break;

                case "Initialize":
                    RequireAttr(el, "ParameterFile", errors, line, col);
                    break;

                case "Ref":
                    RequireAttr(el, "TemplateID", errors, line, col);
                    break;

                default:
                    errors.Add(new ValidationErrorViewModel
                    {
                        Line = line, Column = col,
                        ErrorType = "Structure",
                        Message = $"Unexpected element <{name}>. Expected ActionGroup, Action, Initialize, or Ref."
                    });
                    break;
            }
        }
    }

    private static void RequireAttr(XElement el, string attrName,
        List<ValidationErrorViewModel> errors, int line, int col)
    {
        if (el.Attribute(attrName) is null || string.IsNullOrWhiteSpace(el.Attribute(attrName)?.Value))
        {
            errors.Add(new ValidationErrorViewModel
            {
                Line = line, Column = col,
                ErrorType = "Attribute",
                Message = $"<{el.Name.LocalName}> missing required attribute: {attrName}"
            });
        }
    }

    // ???????????????????????????????????????????????????????????????
    // ERROR PANEL
    // ???????????????????????????????????????????????????????????????

    private void ShowErrors(List<ValidationErrorViewModel> errors)
    {
        ValidationErrors.Clear();
        foreach (var e in errors)
            ValidationErrors.Add(e);

        IsErrorPanelVisible = errors.Count > 0;
        IsXmlValid = errors.All(error => error.Severity != "Error");

        var firstError = errors.FirstOrDefault(error => error.Severity == "Error") ?? errors.FirstOrDefault();
        if (firstError is not null)
        {
            ErrorLine = firstError.Line;
            ErrorPosition = firstError.Column;
            NavigateToError?.Invoke(firstError.Line, firstError.Column);
        }
    }

    // ???????????????????????????????????????????????????????????????
    // STRUCTURE PREVIEW
    // ???????????????????????????????????????????????????????????????

    private void RebuildPreview(XElement root)
    {
        PreviewRoots.Clear();
        var rootNode = new TemplatePreviewNode
        {
            DisplayText = "Templates",
            NodeIcon = "TL",
        };

        foreach (var templateEl in root.Elements("Template"))
        {
            var id = templateEl.Attribute("ID")?.Value ?? "?";
            var tNode = new TemplatePreviewNode
            {
                DisplayText = $"Template: {id}",
                NodeIcon = "T",
            };
            BuildPreviewChildren(templateEl, tNode);
            rootNode.Children.Add(tNode);
        }

        PreviewRoots.Add(rootNode);
    }

    private static void BuildPreviewChildren(XElement parent, TemplatePreviewNode parentNode)
    {
        foreach (var el in parent.Elements())
        {
            var name = el.Name.LocalName;
            var node = name switch
            {
                "ActionGroup" => new TemplatePreviewNode
                {
                    DisplayText = $"[{el.Attribute("ExecutionType")?.Value ?? "Seq"}] {el.Attribute("Tag")?.Value ?? ""}",
                    NodeIcon = el.Attribute("ExecutionType")?.Value == "Parallel" ? "||" : ">>",
                },
                "Action" => new TemplatePreviewNode
                {
                    DisplayText = FormatActionPreview(el),
                    NodeIcon = ResolveActionIcon(el),
                },
                "Initialize" => new TemplatePreviewNode
                {
                    DisplayText = $"Initialize: {el.Attribute("ParameterFile")?.Value ?? ""}",
                    NodeIcon = "i",
                },
                "Ref" => new TemplatePreviewNode
                {
                    DisplayText = $"Ref > {el.Attribute("TemplateID")?.Value ?? ""}",
                    NodeIcon = ">",
                },
                _ => new TemplatePreviewNode
                {
                    DisplayText = $"<{name}>",
                    NodeIcon = "?",
                },
            };

            if (name == "ActionGroup")
                BuildPreviewChildren(el, node);

            parentNode.Children.Add(node);
        }
    }

    private static string FormatActionPreview(XElement el)
    {
        var type = el.Attribute("Type")?.Value ?? "?";
        return type switch
        {
            "RunRemoteCommand" => $"{el.Attribute("AgentName")?.Value}: {el.Attribute("Command")?.Value}",
            "SendMail" => $"Mail > {el.Attribute("To")?.Value}: {el.Attribute("Title")?.Value}",
            _ => $"{el.Attribute("Command")?.Value} {el.Attribute("Parameters")?.Value}",
        };
    }

    private static string ResolveActionIcon(XElement el)
    {
        var type = el.Attribute("Type")?.Value ?? "";
        if (type == "SendMail") return "M";
        if (type == "RunRemoteCommand") return "R";
        var cmd = el.Attribute("Command")?.Value ?? "";
        try
        {
            var ext = System.IO.Path.GetExtension(cmd.Trim().Trim('"')).ToLowerInvariant();
            return ext switch
            {
                ".exe" => "X", ".bat" or ".cmd" => "B", ".ps1" => "P", ".msi" => "I",
                _ => "A"
            };
        }
        catch (ArgumentException) { return "A"; }
    }
}
