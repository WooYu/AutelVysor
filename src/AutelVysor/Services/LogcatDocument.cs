using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AutelVysor.Services;

public enum LogcatLevel { Unknown = -1, Verbose, Debug, Info, Warn, Error, Fatal }

/// <summary>All supplied filters combine. A selected app without a resolved PID matches nothing.</summary>
public sealed record LogcatFilter
{
    public string? Text { get; init; }
    public string? Tag { get; init; }
    public int? Pid { get; init; }
    public LogcatLevel? MinimumLevel { get; init; }
    public bool OnlySelectedApp { get; init; }
    public int? SelectedAppPid { get; init; }
}

/// <summary>A parsed header with its following exception lines, in source order.</summary>
public sealed class LogcatRecord
{
    private readonly List<string> _lines = new();
    private readonly System.Collections.ObjectModel.ReadOnlyCollection<string> _readOnlyLines;
    private string? _text;
    internal LogcatRecord(long sequence, LogcatLevel level, string? tag, int? pid, string line)
    {
        Sequence = sequence; Level = level; Tag = tag; Pid = pid;
        _lines.Add(line); _readOnlyLines = _lines.AsReadOnly();
    }
    public long Sequence { get; }
    public LogcatLevel Level { get; }
    public string? Tag { get; }
    public int? Pid { get; }
    public bool IsStructured => Level != LogcatLevel.Unknown && Pid.HasValue;
    public IReadOnlyList<string> SourceLines => _readOnlyLines;
    public string Text => _text ??= string.Join("\n", _lines);
    internal int CharacterCount { get; private set; }
    internal void AddLine(string line) { _lines.Add(line); _text = null; CharacterCount += line.Length + 1; }
    internal void InitializeCharacterCount() => CharacterCount = _lines[0].Length + 1;
}

/// <summary>
/// Shared live/snapshot/import document. Retains a bounded suffix, never a promise of a complete
/// device log. Callers serialize UI updates; mutations and query enumeration are synchronized.
/// Export normalizes line endings to LF and omits a final line terminator.
/// </summary>
public sealed class LogcatDocument
{
    private static readonly Regex Threadtime = new(
        @"^(?:\d{4}-)?\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d+\s+(?<pid>\d+)\s+\d+\s+(?<level>[VDIWEFA])\s+(?<tag>[^:]+):",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex BriefOrTime = new(
        @"^(?:(?:\d{4}-)?\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d+\s+)?(?<level>[VDIWEFA])/(?<tag>[^\r\n(]+)\(\s*(?<pid>\d+)\s*\):",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex ExceptionStart = new(
        @"^(?:Caused by:|Suppressed:|(?:[\w$]+\.)*[\w$]*(?:Exception|Error)(?::|$))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly object _gate = new();
    private readonly LinkedList<LogcatRecord> _records = new();
    private readonly int _maxRecords, _maxSourceCharacters, _maxLineLength;
    private int _retainedLines, _retainedCharacters;
    private long _receivedLines, _droppedLines, _truncatedLines, _truncatedCharacters, _sequence;

    public LogcatDocument(int maxRecords = 10000, int maxSourceCharacters = 4000000, int maxLineLength = 16384)
    {
        if (maxRecords < 1) throw new ArgumentOutOfRangeException(nameof(maxRecords));
        if (maxSourceCharacters < 1) throw new ArgumentOutOfRangeException(nameof(maxSourceCharacters));
        if (maxLineLength < 1) throw new ArgumentOutOfRangeException(nameof(maxLineLength));
        _maxRecords = maxRecords; _maxSourceCharacters = maxSourceCharacters; _maxLineLength = maxLineLength;
    }
    /// <summary>
    /// Read a source without materializing whole lines: keep only the bounded line prefix and
    /// count discarded characters. CR, LF and CRLF are logical separators, even across reads.
    /// Uses default retention limits. The caller owns the reader; cancellation is checked on
    /// each 4096-character chunk (a synchronous reader must return before cancellation is seen).
    /// </summary>
    public static LogcatDocument ReadFrom(TextReader reader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var document = new LogcatDocument();
        var buffer = new char[4096];
        var prefix = new char[Math.Min(document._maxLineLength, document._maxSourceCharacters - 1)];
        var prefixLength = 0;
        long sourceLength = 0;
        var previousWasCr = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = reader.Read(buffer, 0, buffer.Length);
            cancellationToken.ThrowIfCancellationRequested();
            if (count == 0) break;
            for (var i = 0; i < count; i++)
            {
                var character = buffer[i];
                if (character == '\n' && previousWasCr) { previousWasCr = false; continue; }
                previousWasCr = false;
                if (character == '\r' || character == '\n')
                {
                    document.AppendCore(prefix.AsSpan(0, prefixLength), sourceLength - prefixLength);
                    prefixLength = 0; sourceLength = 0;
                    previousWasCr = character == '\r';
                }
                else
                {
                    if (prefixLength < prefix.Length) prefix[prefixLength++] = character;
                    sourceLength++;
                }
            }
        }
        if (sourceLength > 0) document.AppendCore(prefix.AsSpan(0, prefixLength), sourceLength - prefixLength);
        return document;
    }
    public int RetainedRecordCount { get { lock (_gate) return _records.Count; } }
    public int RetainedLineCount { get { lock (_gate) return _retainedLines; } }
    /// <summary>Budget includes one terminator per line, so exported character count is at most this value.</summary>
    public int RetainedCharacterCount { get { lock (_gate) return _retainedCharacters; } }
    public long ReceivedLineCount { get { lock (_gate) return _receivedLines; } }
    public long DroppedLineCount { get { lock (_gate) return _droppedLines; } }
    public long TruncatedLineCount { get { lock (_gate) return _truncatedLines; } }
    public long TruncatedCharacterCount { get { lock (_gate) return _truncatedCharacters; } }
    public bool HasRetentionLoss { get { lock (_gate) return _droppedLines > 0 || _truncatedLines > 0; } }

    public void Clear()
    {
        lock (_gate)
        {
            _records.Clear(); _retainedLines = _retainedCharacters = 0;
            _receivedLines = _droppedLines = _truncatedLines = _truncatedCharacters = _sequence = 0;
        }
    }
    public void SetSource(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate) { Clear(); Append(text); }
    }
    /// <summary>Append complete lines from a chunk. Use AppendLine for each live line callback.
    /// Chunks must end on line boundaries; partial lines are not buffered.</summary>
    public void Append(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n' && text[i] != '\r') continue;
                AppendCore(text.AsSpan(start, i - start));
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                start = i + 1;
            }
            if (start < text.Length) AppendCore(text.AsSpan(start));
        }
    }
    public void AppendLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        // Some callbacks retain the terminator. Multiple lines use the same parser as imported sources.
        if (line.IndexOfAny(new[] { '\r', '\n' }) >= 0) { Append(line); return; }
        lock (_gate) AppendCore(line.AsSpan());
    }
    private void AppendCore(ReadOnlySpan<char> source, long discardedCharacters = 0)
    {
        _receivedLines++;
        var length = Math.Min(source.Length, Math.Min(_maxLineLength, _maxSourceCharacters - 1));
        if (length < source.Length || discardedCharacters > 0)
        {
            _truncatedLines++; _truncatedCharacters += source.Length - length + discardedCharacters;
        }
        var line = source[..length].ToString();
        var match = Threadtime.Match(line);
        if (!match.Success) match = BriefOrTime.Match(line);
        var structured = match.Success && int.TryParse(match.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out _);
        if (structured)
        {
            var pid = int.Parse(match.Groups["pid"].Value, CultureInfo.InvariantCulture);
            var level = match.Groups["level"].Value[0] switch
            {
                'V' => LogcatLevel.Verbose, 'D' => LogcatLevel.Debug, 'I' => LogcatLevel.Info,
                'W' => LogcatLevel.Warn, 'E' => LogcatLevel.Error, _ => LogcatLevel.Fatal
            };
            AddRecord(new LogcatRecord(++_sequence, level, match.Groups["tag"].Value.Trim(), pid, line));
        }
        else if (_records.Last?.Value is { IsStructured: true } previous && IsContinuation(line)) previous.AddLine(line);
        else AddRecord(new LogcatRecord(++_sequence, LogcatLevel.Unknown, null, null, line));
        _retainedLines++; _retainedCharacters += line.Length + 1;
        while (_records.Count > _maxRecords || _retainedCharacters > _maxSourceCharacters)
        {
            var first = _records.First!.Value;
            _retainedCharacters -= first.CharacterCount; _retainedLines -= first.SourceLines.Count;
            _droppedLines += first.SourceLines.Count; _records.RemoveFirst();
        }
    }
    private void AddRecord(LogcatRecord record) { record.InitializeCharacterCount(); _records.AddLast(record); }
    private static bool IsContinuation(string line) =>
        line.Length == 0 || char.IsWhiteSpace(line[0]) || ExceptionStart.IsMatch(line);

    public IReadOnlyList<LogcatRecord> GetVisible(LogcatFilter? filter = null)
    {
        lock (_gate) return _records.Where(record => Matches(record, filter)).ToArray();
    }
    public string ExportText(LogcatFilter? filter = null)
    {
        lock (_gate)
        {
            var output = new StringBuilder(_retainedCharacters);
            var first = true;
            foreach (var record in _records)
            {
                if (!Matches(record, filter)) continue;
                if (!first) output.Append('\n');
                first = false;
                output.Append(record.Text);
            }
            return output.ToString();
        }
    }
    private static bool Matches(LogcatRecord record, LogcatFilter? filter)
    {
        if (filter is null) return true;
        if (!string.IsNullOrEmpty(filter.Text) && !record.Text.Contains(filter.Text, StringComparison.OrdinalIgnoreCase)) return false;
        var hasStructuredFilter = !string.IsNullOrWhiteSpace(filter.Tag) || filter.Pid.HasValue || filter.MinimumLevel.HasValue || filter.OnlySelectedApp;
        if (hasStructuredFilter && !record.IsStructured) return false;
        if (!string.IsNullOrWhiteSpace(filter.Tag) && !string.Equals(record.Tag, filter.Tag.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        if (filter.Pid.HasValue && filter.Pid != record.Pid) return false;
        if (filter.OnlySelectedApp && (!filter.SelectedAppPid.HasValue || filter.SelectedAppPid != record.Pid)) return false;
        return !filter.MinimumLevel.HasValue || record.Level >= filter.MinimumLevel.Value;
    }
}
