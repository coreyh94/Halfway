using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Halfway.Core;

public sealed record DiagnosticEvent(
    long Sequence,
    DateTimeOffset Timestamp,
    string Category,
    string Name,
    IReadOnlyDictionary<string, string> Facts);

public sealed class DiagnosticBuffer
{
    public const int DefaultCapacity = 256;
    private static readonly string[] ProhibitedFactNameParts =
    {
        "terminaloutput", "transcript", "prompt", "partialinput", "submittedinput", "userinput",
        "environment", "commandline", "apikey", "token", "password", "passwd", "secret", "filecontents"
    };
    private readonly object _gate = new();
    private readonly Queue<DiagnosticEvent> _events;
    private readonly int _capacity;
    private long _nextSequence;

    public DiagnosticBuffer(int capacity = DefaultCapacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _events = new Queue<DiagnosticEvent>(capacity);
    }

    public void Record(string category, string name, DateTimeOffset timestamp, IEnumerable<KeyValuePair<string, string>>? facts = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var safeFacts = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (facts is not null)
        {
            foreach (var fact in facts)
            {
                if (!IsAllowedFactName(fact.Key)) continue;
                safeFacts[fact.Key] = DiagnosticRedactor.Redact(fact.Value);
            }
        }

        lock (_gate)
        {
            var item = new DiagnosticEvent(++_nextSequence, timestamp, category, name,
                new ReadOnlyDictionary<string, string>(safeFacts));
            if (_events.Count == _capacity) _events.Dequeue();
            _events.Enqueue(item);
        }
    }

    public IReadOnlyList<DiagnosticEvent> Snapshot()
    {
        lock (_gate) return _events.OrderBy(item => item.Sequence).ToArray();
    }

    internal static bool IsAllowedFactName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var normalized = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return !ProhibitedFactNameParts.Any(normalized.Contains);
    }
}

public static partial class DiagnosticRedactor
{
    private const string Redacted = "[REDACTED]";

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        var redacted = BearerPattern().Replace(value, "$1" + Redacted);
        redacted = AssignmentPattern().Replace(redacted, "$1" + Redacted);
        redacted = ConnectionStringPasswordPattern().Replace(redacted, "$1" + Redacted);
        return WindowsPathPattern().Replace(redacted, "[PATH]");
    }

    [GeneratedRegex("(?i)(bearer\\s+)[A-Za-z0-9._~+/=-]+")]
    private static partial Regex BearerPattern();

    [GeneratedRegex("(?i)((?:api[_-]?key|token|password|passwd|secret)\\s*[:=]\\s*)[^\\s,;]+")]
    private static partial Regex AssignmentPattern();

    [GeneratedRegex("(?i)((?:pwd|password)\\s*=\\s*)[^;]+")]
    private static partial Regex ConnectionStringPasswordPattern();

    [GeneratedRegex("(?i)(?:[a-z]:\\\\|\\\\\\\\)[^\\s,;]+")]
    private static partial Regex WindowsPathPattern();
}

public sealed class DiagnosticExporter
{
    public const int SchemaVersion = 1;

    public async Task ExportAsync(string path, IReadOnlyList<DiagnosticEvent> events, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(events);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", SchemaVersion);
        writer.WriteString("product", "Halfway");
        writer.WriteStartArray("events");
        foreach (var item in events.OrderBy(item => item.Sequence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteStartObject();
            writer.WriteNumber("sequence", item.Sequence);
            writer.WriteString("timestamp", item.Timestamp.ToUniversalTime().ToString("O"));
            writer.WriteString("category", DiagnosticRedactor.Redact(item.Category));
            writer.WriteString("name", DiagnosticRedactor.Redact(item.Name));
            writer.WriteStartObject("facts");
            foreach (var fact in item.Facts.Where(fact => DiagnosticBuffer.IsAllowedFactName(fact.Key)).OrderBy(fact => fact.Key, StringComparer.Ordinal))
                writer.WriteString(fact.Key, DiagnosticRedactor.Redact(fact.Value));
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
