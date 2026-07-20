using System.Text.Json;
using Halfway.Core;
using Xunit;

namespace Halfway.Core.Tests;

public sealed class DiagnosticsTests
{
    [Fact]
    public void BufferRetainsNewestEventsInDeterministicOrder()
    {
        var buffer = new DiagnosticBuffer(2);
        buffer.Record("app", "first", DateTimeOffset.UnixEpoch);
        buffer.Record("app", "second", DateTimeOffset.UnixEpoch.AddSeconds(1));
        buffer.Record("app", "third", DateTimeOffset.UnixEpoch.AddSeconds(2));

        Assert.Equal(["second", "third"], buffer.Snapshot().Select(item => item.Name));
        Assert.Equal([2L, 3L], buffer.Snapshot().Select(item => item.Sequence));
    }

    [Fact]
    public async Task ConcurrentWritesHaveUniqueOrderedSequences()
    {
        var buffer = new DiagnosticBuffer(100);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(index => Task.Run(() =>
            buffer.Record("runtime", "fact", DateTimeOffset.UnixEpoch, new Dictionary<string, string> { ["index"] = index.ToString() }))));

        Assert.Equal(Enumerable.Range(1, 100).Select(value => (long)value), buffer.Snapshot().Select(item => item.Sequence));
    }

    [Fact]
    public void BufferRejectsSensitiveFieldCategoriesAndRedactsValues()
    {
        var buffer = new DiagnosticBuffer();
        buffer.Record("runtime", "write-failed", DateTimeOffset.UnixEpoch, new Dictionary<string, string>
        {
            ["terminalOutput"] = "private transcript",
            ["partialInput"] = "unfinished",
            ["submittedInput"] = "command",
            ["errorMessage"] = "token=abc123 password: hunter2 Bearer xyz.123 at C:\\Users\\person\\private.txt",
            ["outcome"] = "failed"
        });

        var item = Assert.Single(buffer.Snapshot());
        Assert.Equal("failed", item.Facts["outcome"]);
        Assert.Equal("token=[REDACTED] password: [REDACTED] Bearer [REDACTED] at [PATH]", item.Facts["errorMessage"]);
        Assert.DoesNotContain(item.Facts.Keys, key => key.Contains("input", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(item.Facts.Keys, key => key.Contains("terminal", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExportHasVersionAndDeterministicEventAndFactOrdering()
    {
        var path = TemporaryPath();
        try
        {
            var exporter = new DiagnosticExporter();
            var events = new[]
            {
                new DiagnosticEvent(2, DateTimeOffset.UnixEpoch.AddSeconds(2), "session", "second", new Dictionary<string, string> { ["z"] = "last", ["a"] = "first" }),
                new DiagnosticEvent(1, DateTimeOffset.UnixEpoch, "app", "first", new Dictionary<string, string>())
            };
            await exporter.ExportAsync(path, events);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(DiagnosticExporter.SchemaVersion, document.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(["first", "second"], document.RootElement.GetProperty("events").EnumerateArray().Select(item => item.GetProperty("name").GetString()));
            Assert.Equal(["a", "z"], document.RootElement.GetProperty("events")[1].GetProperty("facts").EnumerateObject().Select(item => item.Name));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task ExportAppliesFinalRedactionPass()
    {
        var path = TemporaryPath();
        try
        {
            var events = new[] { new DiagnosticEvent(1, DateTimeOffset.UnixEpoch, "app", "failure", new Dictionary<string, string> { ["message"] = "api_key=topsecret; Password=alsosecret", ["Terminal Transcript"] = "must-not-export" }) };
            await new DiagnosticExporter().ExportAsync(path, events);
            var json = await File.ReadAllTextAsync(path);

            Assert.DoesNotContain("topsecret", json);
            Assert.DoesNotContain("alsosecret", json);
            Assert.DoesNotContain("must-not-export", json);
            Assert.DoesNotContain("Terminal Transcript", json);
            Assert.Contains("[REDACTED]", json);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task ExportFailureDoesNotMutateDiagnostics()
    {
        var buffer = new DiagnosticBuffer();
        buffer.Record("lifecycle", "completed", DateTimeOffset.UnixEpoch);
        var before = buffer.Snapshot();

        await Assert.ThrowsAnyAsync<Exception>(() => new DiagnosticExporter().ExportAsync("\0invalid", before));

        Assert.Equal(before, buffer.Snapshot());
    }

    [Fact]
    public void RecordingDiagnosticsCreatesNoLifecycleOrAlertFacts()
    {
        var registry = new SessionRegistry();
        var primary = new AgentSession(Guid.NewGuid(), "Planner", AgentKind.Primary, null);
        registry.Register(primary);
        var buffer = new DiagnosticBuffer();

        buffer.Record("application", "startup", DateTimeOffset.UnixEpoch);

        Assert.Empty(registry.Events);
        Assert.Single(buffer.Snapshot());
    }

    private static string TemporaryPath() => Path.Combine(Path.GetTempPath(), $"halfway-diagnostics-{Guid.NewGuid():N}.json");
}
