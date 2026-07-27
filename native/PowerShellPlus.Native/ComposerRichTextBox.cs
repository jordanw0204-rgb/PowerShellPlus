using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace PowerShellPlus.Native;

internal sealed record ComposerTokenDescriptor(string Id, string Path, string Label, AttachmentPreviewKind Kind);

/// <summary>
/// Rich command editor that renders attachment paths as interactive tokens while
/// exposing a canonical plain-text value to the rest of the application.
/// </summary>
internal sealed class ComposerRichTextBox : RichTextBox
{
    private readonly List<ComposerTokenDescriptor> tokens = [];
    private readonly HashSet<string> expandedTokenIds = new(StringComparer.Ordinal);
    private string canonicalText = string.Empty;
    private string tokenSignature = string.Empty;
    private bool rebuilding;
    internal event Action<string>? PlainTextPasted;

    public ComposerRichTextBox()
    {
        Document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            ColumnWidth = double.PositiveInfinity
        };
        DataObject.AddPastingHandler(this, HandleDataObjectPasting);
        UpdateLineLimit();
    }

    public int MinLines { get; set; } = 1;

    private int maxLines = 8;
    public int MaxLines
    {
        get => maxLines;
        set
        {
            maxLines = Math.Max(1, value);
            UpdateLineLimit();
        }
    }

    public string Text
    {
        get => canonicalText;
        set => SetCanonicalText(value ?? string.Empty);
    }

    public int CaretIndex
    {
        get => CanonicalOffsetFor(CaretPosition);
        set => CaretPosition = PointerForCanonicalOffset(Math.Clamp(value, 0, canonicalText.Length));
    }

    public string SelectedText
    {
        get
        {
            var start = CanonicalOffsetFor(Selection.Start);
            var end = CanonicalOffsetFor(Selection.End);
            return canonicalText[Math.Min(start, end)..Math.Max(start, end)];
        }
        set => ReplaceCanonicalSelection(value ?? string.Empty);
    }

    public void ApplyComposerFontSize(double value)
    {
        FontSize = value;
        Document.FontSize = value;
        UpdateLineLimit();
    }

    public void SetAttachmentTokens(IEnumerable<ComposerTokenDescriptor> descriptors)
    {
        var next = descriptors.ToList();
        var signature = string.Join('\u001f', next.Select(value => $"{value.Id}\u001e{value.Path}\u001e{value.Label}\u001e{value.Kind}\u001e{expandedTokenIds.Contains(value.Id)}"));
        var mustRender = signature != tokenSignature || NeedsRetokenization(next);
        tokens.Clear();
        tokens.AddRange(next);
        tokenSignature = signature;
        if (mustRender) RenderCanonicalText(CaretIndex);
    }

    internal IReadOnlyList<string> RenderedTokenLabelsForTest => Document.Blocks.OfType<Paragraph>()
        .SelectMany(value => EnumerateInlines(value.Inlines))
        .OfType<Hyperlink>()
        .Where(value => value.Tag is ComposerTokenDescriptor)
        .Select(value => new TextRange(value.ContentStart, value.ContentEnd).Text)
        .ToArray();

    internal bool UsesThemedScrollbarForTest => Resources.Contains(typeof(ScrollBar));

    internal bool ToggleFirstTokenForTest()
    {
        var token = tokens.FirstOrDefault(value => canonicalText.Contains(value.Path, StringComparison.OrdinalIgnoreCase));
        if (token is null) return false;
        ToggleToken(token, canonicalText.IndexOf(token.Path, StringComparison.OrdinalIgnoreCase));
        var expanded = RenderedTokenLabelsForTest.FirstOrDefault() == token.Path;
        ToggleToken(token, canonicalText.IndexOf(token.Path, StringComparison.OrdinalIgnoreCase));
        return expanded && RenderedTokenLabelsForTest.FirstOrDefault() == token.Label;
    }

    internal void SimulatePlainTextPasteForTest(string text)
    {
        ReplaceCanonicalSelection(text);
        PlainTextPasted?.Invoke(text);
    }

    public void Clear() => Text = string.Empty;

    public new void SelectAll() => Selection.Select(Document.ContentStart, Document.ContentEnd);

    internal void InsertLineBreakAtCaret() => ReplaceCanonicalSelection("\n");

    internal bool DeleteToCurrentLineBoundary(bool beforeCaret)
    {
        var caret = CaretIndex;
        var boundary = beforeCaret
            ? caret == 0 ? 0 : canonicalText.LastIndexOf('\n', caret - 1) + 1
            : canonicalText.IndexOf('\n', caret);
        if (!beforeCaret && boundary < 0) boundary = canonicalText.Length;
        var start = beforeCaret ? boundary : caret;
        var end = beforeCaret ? caret : boundary;
        if (start >= end) return false;
        canonicalText = canonicalText.Remove(start, end - start);
        RenderCanonicalText(start);
        return true;
    }

    internal bool TryMoveCaretByVisualLine(int direction)
    {
        if (direction == 0) return false;
        var before = CaretIndex;
        var command = direction < 0 ? EditingCommands.MoveUpByLine : EditingCommands.MoveDownByLine;
        if (!command.CanExecute(null, this)) return false;
        command.Execute(null, this);
        return CaretIndex != before;
    }

    protected override void OnTextChanged(TextChangedEventArgs e)
    {
        if (rebuilding) return;
        canonicalText = ExtractCanonicalText();
        base.OnTextChanged(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (e.Key == Key.C) { CopyCanonicalSelection(); e.Handled = true; }
            else if (e.Key == Key.X) { CopyCanonicalSelection(); ReplaceCanonicalSelection(string.Empty); e.Handled = true; }
            else if (e.Key == Key.V && ClipboardContainsPlainTextOnly() && TryGetClipboardText(out var text))
            {
                ReplaceCanonicalSelection(text);
                PlainTextPasted?.Invoke(text);
                e.Handled = true;
            }
        }
        base.OnPreviewKeyDown(e);
    }

    private void HandleDataObjectPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.SourceDataObject.GetDataPresent(DataFormats.FileDrop, true)
            || e.SourceDataObject.GetDataPresent(DataFormats.Bitmap, true)
            || e.SourceDataObject.GetData(DataFormats.UnicodeText, true) is not string text
            || string.IsNullOrWhiteSpace(text)) return;
        Dispatcher.BeginInvoke(() => PlainTextPasted?.Invoke(text), System.Windows.Threading.DispatcherPriority.Background);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        // Do not snap blank-space clicks to the nearest inline. With snapping enabled,
        // clicking anywhere after a collapsed attachment could resolve back to its
        // Hyperlink and toggle it instead of placing the caret in the editor.
        var pointer = GetPositionFromPoint(e.GetPosition(this), false);
        var parent = pointer?.Parent as DependencyObject;
        while (parent is not null && parent is not Hyperlink) parent = parent is FrameworkContentElement element ? element.Parent : null;
        if (parent is Hyperlink { Tag: ComposerTokenDescriptor token } hyperlink)
        {
            ToggleToken(token, CanonicalOffsetFor(hyperlink.ElementStart));
            Focus();
            Keyboard.Focus(this);
            e.Handled = true;
            return;
        }
        base.OnPreviewMouseLeftButtonDown(e);
    }

    internal bool BlankSpaceDoesNotToggleAttachmentForTest()
    {
        if (tokens.Count == 0 || ActualWidth <= 0 || ActualHeight <= 0) return false;
        var before = RenderedTokenLabelsForTest.ToArray();
        var pointer = GetPositionFromPoint(new Point(Math.Max(0, ActualWidth - 2), Math.Max(1, ActualHeight / 2)), false);
        var parent = pointer?.Parent as DependencyObject;
        while (parent is not null && parent is not Hyperlink) parent = parent is FrameworkContentElement element ? element.Parent : null;
        return parent is not Hyperlink { Tag: ComposerTokenDescriptor }
            && before.SequenceEqual(RenderedTokenLabelsForTest);
    }

    private void SetCanonicalText(string value)
    {
        if (value == canonicalText && !NeedsRetokenization(tokens)) return;
        canonicalText = NormalizeNewlines(value);
        RenderCanonicalText(Math.Min(CaretIndex, canonicalText.Length));
    }

    private void ReplaceCanonicalSelection(string value)
    {
        var start = CanonicalOffsetFor(Selection.Start);
        var end = CanonicalOffsetFor(Selection.End);
        if (start > end) (start, end) = (end, start);
        var replacement = NormalizeNewlines(value);
        canonicalText = canonicalText[..start] + replacement + canonicalText[end..];
        RenderCanonicalText(start + replacement.Length);
    }

    private void CopyCanonicalSelection()
    {
        var text = SelectedText;
        if (text.Length == 0) return;
        try { Clipboard.SetText(text, TextDataFormat.UnicodeText); }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException) { }
    }

    private static bool ClipboardContainsPlainTextOnly()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            return data?.GetDataPresent(DataFormats.UnicodeText, true) == true
                && data.GetDataPresent(DataFormats.FileDrop, true) == false
                && data.GetDataPresent(DataFormats.Bitmap, true) == false;
        }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException) { return false; }
    }

    private static bool TryGetClipboardText(out string text)
    {
        text = string.Empty;
        try
        {
            text = Clipboard.GetText(TextDataFormat.UnicodeText);
            return text.Length > 0;
        }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException) { return false; }
    }

    private void RenderCanonicalText(int caretOffset)
    {
        rebuilding = true;
        try
        {
            var paragraph = new Paragraph { Margin = new Thickness(0), Padding = new Thickness(0) };
            var cursor = 0;
            while (cursor < canonicalText.Length)
            {
                var match = FindNextToken(cursor);
                if (match.Token is null)
                {
                    AddPlainText(paragraph, canonicalText[cursor..]);
                    break;
                }
                if (match.Index > cursor) AddPlainText(paragraph, canonicalText[cursor..match.Index]);
                paragraph.Inlines.Add(CreateTokenInline(match.Token));
                cursor = match.Index + match.Token.Path.Length;
            }
            if (canonicalText.Length == 0) paragraph.Inlines.Add(new Run(string.Empty));
            Document.Blocks.Clear();
            Document.Blocks.Add(paragraph);
            Document.FontFamily = FontFamily;
            Document.FontSize = FontSize;
            CaretPosition = PointerForCanonicalOffset(Math.Clamp(caretOffset, 0, canonicalText.Length));
        }
        finally { rebuilding = false; }
        base.OnTextChanged(new TextChangedEventArgs(TextChangedEvent, UndoAction.None));
    }

    private (int Index, ComposerTokenDescriptor? Token) FindNextToken(int start)
    {
        var bestIndex = -1;
        ComposerTokenDescriptor? best = null;
        foreach (var token in tokens)
        {
            var index = canonicalText.IndexOf(token.Path, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0 || bestIndex >= 0 && index >= bestIndex) continue;
            bestIndex = index;
            best = token;
        }
        return (bestIndex, best);
    }

    private Hyperlink CreateTokenInline(ComposerTokenDescriptor token)
    {
        var expanded = expandedTokenIds.Contains(token.Id);
        var hyperlink = new Hyperlink(new Run(expanded ? token.Path : token.Label))
        {
            Tag = token,
            Foreground = new SolidColorBrush(TokenColor(token.Kind)),
            Background = new SolidColorBrush(Color.FromArgb(48, 88, 91, 112)),
            FontWeight = FontWeights.SemiBold,
            TextDecorations = null,
            Cursor = Cursors.Hand,
            ToolTip = expanded ? "Collapse file path" : $"Expand {token.Path}"
        };
        hyperlink.MouseLeftButtonDown += (_, eventArgs) =>
        {
            ToggleToken(token, CanonicalOffsetFor(hyperlink.ElementStart));
            eventArgs.Handled = true;
        };
        return hyperlink;
    }

    private void ToggleToken(ComposerTokenDescriptor token, int caret)
    {
        if (!expandedTokenIds.Add(token.Id)) expandedTokenIds.Remove(token.Id);
        tokenSignature = string.Empty;
        RenderCanonicalText(caret + token.Path.Length);
    }

    private static Color TokenColor(AttachmentPreviewKind kind) => kind switch
    {
        AttachmentPreviewKind.Image => Color.FromRgb(166, 227, 161),
        AttachmentPreviewKind.Media => Color.FromRgb(137, 180, 250),
        AttachmentPreviewKind.Text => Color.FromRgb(249, 226, 175),
        _ => Color.FromRgb(203, 166, 247)
    };

    private static void AddPlainText(Paragraph paragraph, string text)
    {
        var parts = NormalizeNewlines(text).Split('\n');
        for (var index = 0; index < parts.Length; index++)
        {
            if (parts[index].Length > 0) paragraph.Inlines.Add(new Run(parts[index]));
            if (index + 1 < parts.Length) paragraph.Inlines.Add(new LineBreak());
        }
    }

    private string ExtractCanonicalText()
    {
        var builder = new System.Text.StringBuilder();
        var firstBlock = true;
        foreach (var block in Document.Blocks)
        {
            if (!firstBlock) builder.Append('\n');
            firstBlock = false;
            if (block is Paragraph paragraph) AppendInlines(builder, paragraph.Inlines);
        }
        return NormalizeNewlines(builder.ToString());
    }

    private static void AppendInlines(System.Text.StringBuilder builder, InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is Hyperlink { Tag: ComposerTokenDescriptor token }) builder.Append(token.Path);
            else if (inline is Run run) builder.Append(run.Text);
            else if (inline is LineBreak) builder.Append('\n');
            else if (inline is Span span) AppendInlines(builder, span.Inlines);
        }
    }

    private bool NeedsRetokenization(IReadOnlyCollection<ComposerTokenDescriptor> descriptors)
    {
        var rendered = Document.Blocks.OfType<Paragraph>()
            .SelectMany(value => EnumerateInlines(value.Inlines))
            .OfType<Hyperlink>()
            .Where(value => value.Tag is ComposerTokenDescriptor)
            .Select(value => ((ComposerTokenDescriptor)value.Tag, new TextRange(value.ContentStart, value.ContentEnd).Text))
            .ToList();
        var expected = ExpectedTokenSequence(descriptors);
        if (rendered.Count != expected.Count) return true;
        for (var index = 0; index < expected.Count; index++)
        {
            var descriptor = rendered[index].Item1;
            var expectedText = expandedTokenIds.Contains(expected[index].Id) ? expected[index].Path : expected[index].Label;
            if (descriptor.Id != expected[index].Id || rendered[index].Item2 != expectedText) return true;
        }
        return false;
    }

    private List<ComposerTokenDescriptor> ExpectedTokenSequence(IReadOnlyCollection<ComposerTokenDescriptor> descriptors)
    {
        var result = new List<ComposerTokenDescriptor>();
        var cursor = 0;
        while (cursor < canonicalText.Length)
        {
            var bestIndex = -1;
            ComposerTokenDescriptor? best = null;
            foreach (var descriptor in descriptors)
            {
                var index = canonicalText.IndexOf(descriptor.Path, cursor, StringComparison.OrdinalIgnoreCase);
                if (index < 0 || bestIndex >= 0 && index >= bestIndex) continue;
                bestIndex = index;
                best = descriptor;
            }
            if (best is null) break;
            result.Add(best);
            cursor = bestIndex + best.Path.Length;
        }
        return result;
    }

    private static IEnumerable<Inline> EnumerateInlines(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            yield return inline;
            if (inline is Span span && inline is not Hyperlink)
                foreach (var child in EnumerateInlines(span.Inlines)) yield return child;
        }
    }

    private int CanonicalOffsetFor(TextPointer target)
    {
        var offset = 0;
        var firstBlock = true;
        foreach (var block in Document.Blocks.OfType<Paragraph>())
        {
            if (!firstBlock) offset++;
            firstBlock = false;
            if (target.CompareTo(block.ContentStart) <= 0) return offset;
            if (TryOffsetInInlines(block.Inlines, target, ref offset, out var result)) return result;
        }
        return Math.Clamp(offset, 0, canonicalText.Length);
    }

    private static bool TryOffsetInInlines(InlineCollection inlines, TextPointer target, ref int offset, out int result)
    {
        foreach (var inline in inlines)
        {
            if (target.CompareTo(inline.ElementStart) <= 0) { result = offset; return true; }
            if (inline is Hyperlink { Tag: ComposerTokenDescriptor token })
            {
                if (target.CompareTo(inline.ElementEnd) < 0) { result = offset; return true; }
                offset += token.Path.Length;
            }
            else if (inline is Run run)
            {
                if (target.CompareTo(run.ContentEnd) < 0)
                {
                    result = offset + new TextRange(run.ContentStart, target).Text.Length;
                    return true;
                }
                offset += run.Text.Length;
            }
            else if (inline is LineBreak) offset++;
            else if (inline is Span span && TryOffsetInInlines(span.Inlines, target, ref offset, out result)) return true;
        }
        result = offset;
        return false;
    }

    private TextPointer PointerForCanonicalOffset(int target)
    {
        var offset = 0;
        foreach (var block in Document.Blocks.OfType<Paragraph>())
        {
            if (TryPointerInInlines(block.Inlines, target, ref offset, out var pointer)) return pointer;
            offset++;
        }
        return Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
    }

    private static bool TryPointerInInlines(InlineCollection inlines, int target, ref int offset, out TextPointer pointer)
    {
        foreach (var inline in inlines)
        {
            if (inline is Hyperlink { Tag: ComposerTokenDescriptor token })
            {
                if (target <= offset) { pointer = inline.ElementStart; return true; }
                if (target <= offset + token.Path.Length) { pointer = inline.ElementEnd; return true; }
                offset += token.Path.Length;
            }
            else if (inline is Run run)
            {
                if (target <= offset + run.Text.Length)
                {
                    pointer = run.ContentStart.GetPositionAtOffset(Math.Max(0, target - offset), LogicalDirection.Forward) ?? run.ContentEnd;
                    return true;
                }
                offset += run.Text.Length;
            }
            else if (inline is LineBreak)
            {
                if (target <= offset) { pointer = inline.ElementStart; return true; }
                offset++;
            }
            else if (inline is Span span && TryPointerInInlines(span.Inlines, target, ref offset, out pointer)) return true;
        }
        pointer = null!;
        return false;
    }

    private void UpdateLineLimit()
    {
        var lineHeight = Math.Max(14, FontSize * 1.45);
        MaxHeight = Math.Ceiling(lineHeight * maxLines + Padding.Top + Padding.Bottom + BorderThickness.Top + BorderThickness.Bottom);
    }

    private static string NormalizeNewlines(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
