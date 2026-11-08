---
feature: XmlEditor
status: Implemented
owner: TODO
code: [TestControllerGrpc/Views/RawXmlEditorWindow.xaml, TestControllerGrpc/ViewModels/WatchItemXmlEditorViewModel.cs, TestControllerGrpc.Core/Services/WatchListXmlEditorAnalyzer.cs, TestControllerGrpc/Services/XmlEditorTyping.cs, TestControllerGrpc/Services/XmlEditorQuickFixApplier.cs]
tests: [TestControllerGrpc.Tests/ViewModels/WatchItemXmlEditorViewModelTests.cs, TestControllerGrpc.Tests/Services/WatchListXmlEditorAnalyzerTests.cs, TestControllerGrpc.Tests/Services/XmlEditorTypingTests.cs, TestControllerGrpc.Tests/Ui/XmlHighlightingTests.cs]
last-verified: 2026-11-08
---

# WatchList XML editor

## What it does
- Edits a WatchItem's XML directly in WPF with syntax highlighting, background validation and quick fixes.
- Typing assist (`XmlEditorTyping`) and quick fixes (`XmlEditorQuickFixApplier`) apply each edit as one undo step; the caret moves to an error only on explicit Validate or a blocked Save.
- Apply/Save writes back to `WatchList.xml`; an unsaved-close prompt and a Tag-rename confirmation guard against losing work.

## How to use
- WPF: open a WatchItem's XML from the tree, edit, **Validate**, then **Apply** or **Save**.

## Config / files
- Edits `WatchList.xml` in the controller folder.

## How to test
```powershell
dotnet test TestControllerGrpc.Tests --filter "FullyQualifiedName~XmlEditor|FullyQualifiedName~XmlHighlighting"
```

## Known gaps
- The "edit by instruction" and token-aware intelligence in [prompts/Prompt_XmlEditor_Intelligence.md](prompts/Prompt_XmlEditor_Intelligence.md): TODO verify which parts are built.

## Links
- [prompts/Prompt_XmlEditor_Intelligence.md](prompts/Prompt_XmlEditor_Intelligence.md)
