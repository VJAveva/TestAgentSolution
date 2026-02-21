using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TestControllerGrpc.ViewModels;

/// <summary>
/// ViewModel for the standalone Raw XML Editor window.
/// Provides XML editing with validation, auto-format, and save/cancel commands.
/// </summary>
public sealed partial class RawXmlEditorViewModel : ObservableObject
{
    private readonly string _originalXml;

    [ObservableProperty] private string _xmlText = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _statusSeverity = "Info"; // Info, Success, Error
    [ObservableProperty] private bool _isXmlValid = true;
    [ObservableProperty] private int _errorLine = -1;
    [ObservableProperty] private int _errorPosition = -1;

    /// <summary>Set to true when the user clicks Save and validation passes.</summary>
    public bool DialogAccepted { get; private set; }

    /// <summary>The final XML text to return to the caller after a successful save.</summary>
    public string ResultXml { get; private set; } = "";

    /// <summary>Raised to request the owning Window to close.</summary>
    public event Action? RequestClose;

    /// <summary>Raised when the editor should navigate to a specific error line.</summary>
    public event Action<int, int>? NavigateToError;

    public RawXmlEditorViewModel(string initialXml)
    {
        _originalXml = initialXml;
        XmlText = initialXml;
        StatusMessage = "Ready";
        StatusSeverity = "Info";
    }

    [RelayCommand]
    private void Save()
    {
        var (valid, errorMsg, line, pos) = ValidateXmlInternal(XmlText);
        if (!valid)
        {
            IsXmlValid = false;
            ErrorLine = line;
            ErrorPosition = pos;
            StatusMessage = $"Cannot save — {errorMsg}";
            StatusSeverity = "Error";
            NavigateToError?.Invoke(line, pos);
            return;
        }

        // Additional check: root must be <WatchItem>
        try
        {
            var el = XElement.Parse(XmlText);
            if (el.Name.LocalName != "WatchItem")
            {
                IsXmlValid = false;
                StatusMessage = "Cannot save — Root element must be <WatchItem>.";
                StatusSeverity = "Error";
                return;
            }
        }
        catch (Exception ex)
        {
            IsXmlValid = false;
            StatusMessage = $"Cannot save — {ex.Message}";
            StatusSeverity = "Error";
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
        var (valid, errorMsg, line, pos) = ValidateXmlInternal(XmlText);
        if (valid)
        {
            IsXmlValid = true;
            ErrorLine = -1;
            ErrorPosition = -1;
            StatusMessage = "? XML is valid";
            StatusSeverity = "Success";
        }
        else
        {
            IsXmlValid = false;
            ErrorLine = line;
            ErrorPosition = pos;
            StatusMessage = $"? Line {line}, Pos {pos}: {errorMsg}";
            StatusSeverity = "Error";
            NavigateToError?.Invoke(line, pos);
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
            StatusMessage = $"? Cannot format — Line {ex.LineNumber}, Pos {ex.LinePosition}: {ex.Message}";
            StatusSeverity = "Error";
            NavigateToError?.Invoke(ex.LineNumber, ex.LinePosition);
        }
        catch (Exception ex)
        {
            StatusMessage = $"? Cannot format — {ex.Message}";
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
        StatusMessage = "Reverted to original";
        StatusSeverity = "Info";
    }

    /// <summary>Validates XML text and returns (isValid, errorMessage, lineNumber, linePosition).</summary>
    private static (bool Valid, string Error, int Line, int Position) ValidateXmlInternal(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return (false, "XML text is empty.", 1, 1);

        try
        {
            var settings = new XmlReaderSettings
            {
                ConformanceLevel = ConformanceLevel.Fragment,
                DtdProcessing = DtdProcessing.Ignore,
            };

            using var sr = new StringReader(xml);
            using var reader = XmlReader.Create(sr, settings);
            while (reader.Read()) { }
            return (true, "", -1, -1);
        }
        catch (XmlException ex)
        {
            return (false, ex.Message, ex.LineNumber, ex.LinePosition);
        }
    }
}
