using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Views;

internal sealed class XmlDiagnosticBackgroundRenderer(
    Func<IReadOnlyList<ValidationErrorViewModel>> getDiagnostics,
    Func<string, Brush?> getBrush) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid) return;

        foreach (var diagnostic in getDiagnostics())
        {
            var brush = getBrush(diagnostic.Severity);
            if (brush is null || diagnostic.Line < 1
                || diagnostic.Line > textView.Document.LineCount)
                continue;

            var line = textView.Document.GetLineByNumber(diagnostic.Line);
            if (line.Length == 0) continue;

            var offset = line.Offset + Math.Clamp(diagnostic.Column - 1, 0, line.Length - 1);
            var length = Math.Min(Math.Max(1, diagnostic.Length), line.EndOffset - offset);
            if (length <= 0) continue;

            var pen = new Pen(brush, 1);
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(
                         textView, new DiagnosticSegment(offset, length), false))
            {
                var y = rect.Bottom - 1;
                var x = rect.Left;
                var end = rect.Right;
                var up = true;
                while (x < end)
                {
                    var next = Math.Min(x + 3, end);
                    drawingContext.DrawLine(pen, new System.Windows.Point(x, y),
                        new System.Windows.Point(next, up ? y - 1 : y + 1));
                    x = next;
                    up = !up;
                }
            }
        }
    }

    private readonly record struct DiagnosticSegment(int Offset, int Length) : ISegment
    {
        public int EndOffset => Offset + Length;
    }
}
