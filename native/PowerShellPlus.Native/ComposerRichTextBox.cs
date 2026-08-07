using System.Runtime.InteropServices;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace PowerShellPlus.Native;

internal sealed record ComposerTokenDescriptor(string Id, string Path, string Label, AttachmentPreviewKind Kind);
internal sealed record ComposerInputLatencyResult(
    TimeSpan Total,
    double P50DispatchMilliseconds,
    double P95DispatchMilliseconds,
    double MaximumDispatchMilliseconds,
    double P95EditMilliseconds,
    double MaximumEditMilliseconds,
    int CharacterCount,
    int LayoutUpdates,
    bool TextMatches,
    string SlowOperations);

/// <summary>
/// Rich command editor that renders attachment paths as interactive tokens while
/// exposing a canonical plain-text value to the rest of the application.
/// </summary>
internal sealed class ComposerRichTextBox : RichTextBox
{
    private static readonly FieldInfo? DispatcherOperationMethodField = typeof(System.Windows.Threading.DispatcherOperation)
        .GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic);
    private readonly List<ComposerTokenDescriptor> tokens = [];
    private readonly HashSet<string> expandedTokenIds = new(StringComparer.Ordinal);
    private string canonicalText = string.Empty;
    private string tokenSignature = string.Empty;
    private bool rebuilding;
    private bool canonicalTextDirty;
    private int canonicalExtractionCount;
    internal event Action<string>? PlainTextPasted;

    public ComposerRichTextBox()
    {
        // PowerShell-style command composition favors deterministic low-latency
        // editing over RichTextBox's FlowDocument undo journal. The app already
        // provides persistent per-terminal History for recalling submissions.
        IsUndoEnabled = false;
        UndoLimit = 0;
        Document = CreateDocument();
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
        get
        {
            SynchronizeCanonicalText();
            return canonicalText;
        }
        set => SetCanonicalText(value ?? string.Empty);
    }

    public int CaretIndex
    {
        get
        {
            SynchronizeCanonicalText();
            return CanonicalOffsetFor(CaretPosition);
        }
        set
        {
            SynchronizeCanonicalText();
            CaretPosition = PointerForCanonicalOffset(Math.Clamp(value, 0, canonicalText.Length));
        }
    }

    public string SelectedText
    {
        get
        {
            SynchronizeCanonicalText();
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
        SynchronizeCanonicalText();
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
        SynchronizeCanonicalText();
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
        // A RichTextBox edits a FlowDocument. Walking the whole document here
        // makes every keystroke progressively more expensive, especially for a
        // long restored draft. Keep the canonical representation lazy and sync
        // it once when a consumer (send, persistence, attachment parsing) asks.
        canonicalTextDirty = true;
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
        UpdateLayout();
        var before = RenderedTokenLabelsForTest.ToArray();
        var pointer = GetPositionFromPoint(new Point(Math.Max(0, ActualWidth - 2), Math.Max(1, ActualHeight / 2)), false);
        var parent = pointer?.Parent as DependencyObject;
        while (parent is not null && parent is not Hyperlink) parent = parent is FrameworkContentElement element ? element.Parent : null;
        return parent is not Hyperlink { Tag: ComposerTokenDescriptor }
            && before.SequenceEqual(RenderedTokenLabelsForTest);
    }

    private void SetCanonicalText(string value)
    {
        SynchronizeCanonicalText();
        if (value == canonicalText && !NeedsRetokenization(tokens)) return;
        canonicalText = NormalizeNewlines(value);
        RenderCanonicalText(Math.Min(CaretIndex, canonicalText.Length));
    }

    private void ReplaceCanonicalSelection(string value)
    {
        SynchronizeCanonicalText();
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
            if (!TryRenderPlainCanonicalText(caretOffset))
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
            canonicalTextDirty = false;
        }
        finally
        {
            rebuilding = false;
        }
        base.OnTextChanged(new TextChangedEventArgs(TextChangedEvent, UndoAction.None));
    }

    private bool TryRenderPlainCanonicalText(int caretOffset)
    {
        if (FindNextToken(0).Token is not null) return false;

        Paragraph paragraph;
        Run run;
        if (Document.Blocks.Count == 1
            && Document.Blocks.FirstBlock is Paragraph existingParagraph
            && existingParagraph.Inlines.Count == 1
            && existingParagraph.Inlines.FirstInline is Run existingRun)
        {
            paragraph = existingParagraph;
            run = existingRun;
        }
        else
        {
            paragraph = new Paragraph { Margin = new Thickness(0), Padding = new Thickness(0) };
            run = new Run();
            paragraph.Inlines.Add(run);
            Document.Blocks.Clear();
            Document.Blocks.Add(paragraph);
        }

        run.Text = canonicalText;
        Document.FontFamily = FontFamily;
        Document.FontSize = FontSize;
        CaretPosition = run.ContentStart.GetPositionAtOffset(Math.Clamp(caretOffset, 0, canonicalText.Length), LogicalDirection.Forward)
            ?? run.ContentEnd;
        return true;
    }

    private FlowDocument CreateDocument() => new()
    {
        PagePadding = new Thickness(0),
        ColumnWidth = double.PositiveInfinity,
        FontFamily = FontFamily,
        FontSize = FontSize
    };

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
        canonicalExtractionCount++;
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

    private void SynchronizeCanonicalText()
    {
        if (!canonicalTextDirty || rebuilding) return;
        canonicalText = ExtractCanonicalText();
        canonicalTextDirty = false;
    }

    internal (TimeSpan Elapsed, int ExtractionsDuringTyping, bool CanonicalTextMatches) SimulateFastTypingForTest(int characterCount)
    {
        SetCanonicalText(string.Empty);
        var extractionBaseline = canonicalExtractionCount;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (var index = 0; index < characterCount; index++)
        {
            CaretPosition = Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
            CaretPosition.InsertTextInRun("x");
        }
        timer.Stop();
        var extractionsDuringTyping = canonicalExtractionCount - extractionBaseline;
        var matches = Text.Length == characterCount;
        return (timer.Elapsed, extractionsDuringTyping, matches);
    }

    internal async Task AgeEditorForTestAsync(int cycles, int payloadLength)
    {
        var payload = new string('a', payloadLength);
        for (var index = 0; index < cycles; index++)
        {
            SetCanonicalText(payload);
            UpdateLayout();
            SetCanonicalText(string.Empty);
            UpdateLayout();
            // Measure retained editor state, not queued rendering from an
            // impossible stream of 32,000 draft characters per second. A real
            // send/clear cycle gives WPF idle time while the terminal/agent
            // handles the submission; ContextIdle deterministically drains that
            // work before the next cycle without hiding retained-state growth.
            await Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
    }

    internal bool CanUndoForTest => CanUndo;
    internal int UndoLimitForTest => UndoLimit;

    internal async Task<ComposerInputLatencyResult> SimulateQueuedTypingForTestAsync(int characterCount, int intervalMilliseconds)
    {
        SetCanonicalText(string.Empty);
        UpdateLayout();
        var dispatcherDelays = new List<double>(characterCount);
        var editDurations = new List<double>(characterCount);
        var operations = new List<System.Windows.Threading.DispatcherOperation>(characterCount);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var layoutUpdates = 0;
        var operationStarts = new Dictionary<System.Windows.Threading.DispatcherOperation, (double Started, string Name)>();
        var slowOperations = new List<(double Duration, string Name)>();
        var operationTotals = new Dictionary<string, (int Count, double Total, double Maximum)>(StringComparer.Ordinal);
        EventHandler layoutHandler = (_, _) => layoutUpdates++;
        System.Windows.Threading.DispatcherHookEventHandler operationStarted = (_, args) =>
        {
            var callback = DispatcherOperationMethodField?.GetValue(args.Operation) as Delegate;
            var callbackName = callback?.Method is { } method
                ? $"{method.DeclaringType?.Name}.{method.Name}"
                : "dispatcher callback";
            operationStarts[args.Operation] = (timer.Elapsed.TotalMilliseconds, $"{args.Operation.Priority}:{callbackName}");
        };
        System.Windows.Threading.DispatcherHookEventHandler operationCompleted = (_, args) =>
        {
            if (!operationStarts.Remove(args.Operation, out var started)) return;
            var duration = timer.Elapsed.TotalMilliseconds - started.Started;
            var aggregate = operationTotals.GetValueOrDefault(started.Name);
            operationTotals[started.Name] = (aggregate.Count + 1, aggregate.Total + duration, Math.Max(aggregate.Maximum, duration));
            if (duration >= 5) slowOperations.Add((duration, started.Name));
        };
        LayoutUpdated += layoutHandler;
        Dispatcher.Hooks.OperationStarted += operationStarted;
        Dispatcher.Hooks.OperationCompleted += operationCompleted;
        Dispatcher.Hooks.OperationAborted += operationCompleted;
        try
        {
            await Task.Run(async () =>
            {
                for (var index = 0; index < characterCount; index++)
                {
                    var scheduledMilliseconds = timer.Elapsed.TotalMilliseconds;
                    operations.Add(Dispatcher.InvokeAsync(() =>
                    {
                        var startedMilliseconds = timer.Elapsed.TotalMilliseconds;
                        dispatcherDelays.Add(startedMilliseconds - scheduledMilliseconds);
                        CaretPosition = Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
                        CaretPosition.InsertTextInRun("x");
                        editDurations.Add(timer.Elapsed.TotalMilliseconds - startedMilliseconds);
                    }, System.Windows.Threading.DispatcherPriority.Input));
                    if (intervalMilliseconds > 0) await Task.Delay(intervalMilliseconds).ConfigureAwait(false);
                }
            });
            await Task.WhenAll(operations.Select(value => value.Task));
            await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.Render);
            timer.Stop();
            var orderedDispatch = dispatcherDelays.Order().ToArray();
            var orderedEdits = editDurations.Order().ToArray();
            return new ComposerInputLatencyResult(
                timer.Elapsed,
                Percentile(orderedDispatch, .50),
                Percentile(orderedDispatch, .95),
                orderedDispatch.LastOrDefault(),
                Percentile(orderedEdits, .95),
                orderedEdits.LastOrDefault(),
                characterCount,
                layoutUpdates,
                Text.Length == characterCount,
                string.Join(" | ", operationTotals
                    .OrderByDescending(value => value.Value.Total)
                    .Take(8)
                    .Select(value => $"{value.Key} total={value.Value.Total:F1}ms count={value.Value.Count} max={value.Value.Maximum:F1}ms"))
                + (slowOperations.Count == 0 ? string.Empty : " || slow: " + string.Join(" | ", slowOperations
                    .OrderByDescending(value => value.Duration)
                    .Take(8)
                    .Select(value => $"{value.Name}={value.Duration:F1}ms"))));
        }
        finally
        {
            LayoutUpdated -= layoutHandler;
            Dispatcher.Hooks.OperationStarted -= operationStarted;
            Dispatcher.Hooks.OperationCompleted -= operationCompleted;
            Dispatcher.Hooks.OperationAborted -= operationCompleted;
        }
    }

    private static double Percentile(IReadOnlyList<double> orderedValues, double percentile)
    {
        if (orderedValues.Count == 0) return 0;
        var index = Math.Clamp((int)Math.Ceiling(orderedValues.Count * percentile) - 1, 0, orderedValues.Count - 1);
        return orderedValues[index];
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
