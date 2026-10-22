using AutelVysor.Services;
using System.IO;
using System.Threading;

var tests = new (string Name, Action Run)[]
{
    ("Common formats preserve source order and severity", Formats),
    ("Filters combine across retained records", Filters),
    ("Continuations follow their parent during filtering and export", Continuations),
    ("Malformed records are explicit and never acquire structured identity", Malformed),
    ("Record retention discloses losses", RecordRetention),
    ("Character retention and line truncation remain bounded", CharacterRetention),
    ("Sources reset cleanly and normalize newlines", SourceReplacement),
    ("Export preserves blank source records", BlankExport),
    ("Filtering does not destroy hidden records", Refilter),
    ("Large input remains bounded", LargeInput),
    ("Streaming import bounds huge newline-free input", StreamingHugeLine),
    ("Streaming import observes cancellation between chunks", StreamingCancellation),
    ("Streaming import preserves split CRLF and records", StreamingNewlines)
};
var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}
return failed == 0 ? 0 : 1;

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}, got {actual}");
}
static void True(bool value) { if (!value) throw new Exception("Expected true"); }
static void Formats()
{
    var doc = new LogcatDocument();
    doc.SetSource("10-22 12:34:56.789  123  456 D My.Tag: debug\nW/Other( 124): warning\n10-22 12:35:00.001 E/Crash( 123): failure\n2026-10-22 12:36:00.002 123 456 F Native: fatal");
    var rows = doc.GetVisible();
    Equal(4, rows.Count);
    Equal(LogcatLevel.Debug, rows[0].Level); Equal("My.Tag", rows[0].Tag); Equal(123, rows[0].Pid);
    Equal(LogcatLevel.Warn, rows[1].Level); Equal(124, rows[1].Pid);
    Equal(LogcatLevel.Error, rows[2].Level); Equal(LogcatLevel.Fatal, rows[3].Level);
    True(rows.Zip(rows.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence));
}
static void Filters()
{
    var doc = new LogcatDocument();
    doc.SetSource("I/App(42): Needle one\nE/App(42): NEEDLE two\nE/AppExtra(42): needle three\nF/App(43): needle four\nE/App(42): unrelated");
    var filter = new LogcatFilter { Text = "needle", Tag = "aPP", Pid = 42, MinimumLevel = LogcatLevel.Error, OnlySelectedApp = true, SelectedAppPid = 42 };
    Equal(1, doc.GetVisible(filter).Count);
    True(doc.ExportText(filter).Contains("NEEDLE two"));
    Equal(0, doc.GetVisible(filter with { SelectedAppPid = 43 }).Count);
    Equal(0, doc.GetVisible(filter with { SelectedAppPid = null }).Count);
}
static void Continuations()
{
    const string input = "E/Crash(7): failed\njava.lang.IllegalStateException: broken\n    at example.Main.run(Main.java:4)\nCaused by: java.io.IOException\n    ... 2 more\nI/Other(9): finished";
    var doc = new LogcatDocument(); doc.SetSource(input);
    Equal(2, doc.RetainedRecordCount); Equal(6, doc.RetainedLineCount);
    var filter = new LogcatFilter { Text = "Main.java", Tag = "Crash", Pid = 7 };
    Equal(1, doc.GetVisible(filter).Count);
    Equal(input[..input.LastIndexOf('\n')], doc.ExportText(filter));
    Equal(input, doc.ExportText());
}
static void Malformed()
{
    var doc = new LogcatDocument();
    doc.SetSource("E/Valid(1): fine\nthis is malformed Tag(42) ERROR\nE/Bad(no_pid): wrong\n10-22 12:00:00.000 999999999999 2 E Overflow: wrong\n    an orphan continuation");
    Equal(5, doc.RetainedRecordCount);
    Equal(4, doc.GetVisible().Count(r => r.Level == LogcatLevel.Unknown));
    Equal(1, doc.GetVisible(new LogcatFilter { MinimumLevel = LogcatLevel.Verbose }).Count);
    Equal(0, doc.GetVisible(new LogcatFilter { Pid = 42 }).Count);
    Equal(0, doc.GetVisible(new LogcatFilter { Tag = "Bad" }).Count);
    Equal(1, doc.GetVisible(new LogcatFilter { Text = "malformed" }).Count);
}
static void RecordRetention()
{
    var doc = new LogcatDocument(maxRecords: 2);
    doc.SetSource("E/Old(1): old\n    continuation\nI/New(2): second\nW/New(2): third");
    Equal(4L, doc.ReceivedLineCount); Equal(2L, doc.DroppedLineCount);
    Equal(2, doc.RetainedRecordCount); Equal(2, doc.RetainedLineCount);
    True(doc.HasRetentionLoss); True(!doc.ExportText().Contains("Old"));
    Equal("I/New(2): second\nW/New(2): third", doc.ExportText());
}
static void CharacterRetention()
{
    var doc = new LogcatDocument(maxRecords: 20, maxSourceCharacters: 40, maxLineLength: 20);
    doc.AppendLine(new string('x', 100));
    Equal(1L, doc.TruncatedLineCount); Equal(80L, doc.TruncatedCharacterCount);
    Equal(20, doc.ExportText().Length);
    doc.AppendLine(new string('y', 30));
    doc.AppendLine(new string('z', 20));
    True(doc.RetainedCharacterCount <= 40); True(doc.DroppedLineCount > 0);
    Equal(2L, doc.TruncatedLineCount);
    var exception = new LogcatDocument(maxSourceCharacters: 40, maxLineLength: 20);
    exception.AppendLine("E/X(1): failed");
    for (var i = 0; i < 20; i++) exception.AppendLine("    at example.Main");
    True(exception.RetainedCharacterCount <= 40); True(exception.DroppedLineCount > 0);
}
static void SourceReplacement()
{
    var doc = new LogcatDocument(maxRecords: 1);
    doc.SetSource("a\nb\n"); Equal(2L, doc.ReceivedLineCount);
    doc.SetSource("I/New(8): first\r\nI/New(8): second\rI/New(8): third\n");
    Equal(3L, doc.ReceivedLineCount); Equal(2L, doc.DroppedLineCount);
    Equal("I/New(8): third", doc.ExportText());
    doc.Clear(); Equal(0L, doc.ReceivedLineCount); Equal(0L, doc.DroppedLineCount);
    Equal(0, doc.GetVisible().Count); True(!doc.HasRetentionLoss);
    doc.Append(""); Equal(0L, doc.ReceivedLineCount);
}
static void Refilter()
{
    var doc = new LogcatDocument(); doc.SetSource("D/Tag(1): hidden\nE/Tag(1): visible");
    Equal(1, doc.GetVisible(new LogcatFilter { MinimumLevel = LogcatLevel.Error }).Count);
    Equal(2, doc.GetVisible().Count); Equal(2, doc.GetVisible(new LogcatFilter { Tag = "TAG" }).Count);
}
static void LargeInput()
{
    var doc = new LogcatDocument(maxRecords: 100, maxSourceCharacters: 5000, maxLineLength: 100);
    for (var i = 0; i < 20000; i++) doc.AppendLine($"I/App(2): message {i}");
    Equal(20000L, doc.ReceivedLineCount); True(doc.RetainedRecordCount <= 100);
    True(doc.RetainedCharacterCount <= 5000); Equal(20000L - doc.RetainedLineCount, doc.DroppedLineCount);
    True(doc.ExportText().EndsWith("message 19999", StringComparison.Ordinal));
}

static void BlankExport()
{
    var doc = new LogcatDocument(); doc.SetSource("\nmalformed\n\nlast");
    Equal("\nmalformed\n\nlast", doc.ExportText());
    Equal(4, doc.RetainedLineCount);
}

static void StreamingHugeLine()
{
    // The fixture produces 10 million characters without allocating an input-sized string.
    using var reader = new GeneratedLineReader(10_000_000);
    var doc = LogcatDocument.ReadFrom(reader);
    Equal(4096, reader.MaximumRequestedCount);
    True(reader.ReadCalls > 2000);
    Equal(1L, doc.ReceivedLineCount); Equal(1, doc.RetainedRecordCount);
    Equal(16384, doc.ExportText().Length); Equal(16385, doc.RetainedCharacterCount);
    Equal(1L, doc.TruncatedLineCount); Equal(10_000_000L - 16384, doc.TruncatedCharacterCount);
    Equal(0L, doc.DroppedLineCount); True(doc.HasRetentionLoss);
    Equal(LogcatLevel.Unknown, doc.GetVisible()[0].Level);
}
static void StreamingCancellation()
{
    using var cancellation = new CancellationTokenSource();
    using var reader = new GeneratedLineReader(10_000_000, () => cancellation.Cancel(), cancelAtRead: 3);
    try { LogcatDocument.ReadFrom(reader, cancellation.Token); throw new Exception("Expected cancellation"); }
    catch (OperationCanceledException) { Equal(3, reader.ReadCalls); }
    using var alreadyCancelledReader = new GeneratedLineReader(100);
    try { LogcatDocument.ReadFrom(alreadyCancelledReader, cancellation.Token); throw new Exception("Expected cancellation"); }
    catch (OperationCanceledException) { Equal(0, alreadyCancelledReader.ReadCalls); }
}
static void StreamingNewlines()
{
    // Put CR at the final position in the 4096-char buffer, with LF in the next chunk.
    var input = new string('x', 4095) + "\r\nE/Crash(7): failed\r    at example.Main\n\nI/App(8): done\r";
    using var reader = new StringReader(input);
    var doc = LogcatDocument.ReadFrom(reader);
    var reference = new LogcatDocument(); reference.SetSource(input);
    Equal(reference.ExportText(), doc.ExportText());
    Equal(reference.ReceivedLineCount, doc.ReceivedLineCount);
    Equal(reference.RetainedRecordCount, doc.RetainedRecordCount);
    Equal(1, doc.GetVisible(new LogcatFilter { Tag = "Crash", Text = "example.Main" }).Count);
    using var empty = new StringReader(""); Equal(0L, LogcatDocument.ReadFrom(empty).ReceivedLineCount);
    using var blank = new StringReader("\r\n\r\n"); Equal(2L, LogcatDocument.ReadFrom(blank).ReceivedLineCount);
}

sealed class GeneratedLineReader : TextReader
{
    private long _remaining;
    private readonly Action? _cancel;
    private readonly int _cancelAtRead;
    public GeneratedLineReader(long characters, Action? cancel = null, int cancelAtRead = -1)
    { _remaining = characters; _cancel = cancel; _cancelAtRead = cancelAtRead; }
    public int ReadCalls { get; private set; }
    public int MaximumRequestedCount { get; private set; }
    public override int Read(char[] buffer, int index, int count)
    {
        if (count > 4096 || buffer.Length > 4096) throw new Exception("Import requested an oversized read buffer");
        ReadCalls++; MaximumRequestedCount = Math.Max(MaximumRequestedCount, count);
        if (ReadCalls == _cancelAtRead) _cancel?.Invoke();
        var actual = (int)Math.Min(count, _remaining);
        Array.Fill(buffer, 'x', index, actual); _remaining -= actual;
        return actual;
    }
    public override string? ReadLine() => throw new Exception("Unbounded ReadLine must not be used");
    public override string ReadToEnd() => throw new Exception("Unbounded ReadToEnd must not be used");
}
