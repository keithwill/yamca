using Yamca.Agent.Chat;

namespace Yamca.Agent.Tests.Chat;

[TestFixture]
public class SessionDiagnosticsLogTests
{
    [Test]
    public void Log_AssignsMonotonicSequence()
    {
        var log = new SessionDiagnosticsLog();
        log.Log(DiagnosticCategory.Session, "a");
        log.Log(DiagnosticCategory.Model, "b");

        var entries = log.Snapshot();
        Assert.That(entries.Select(e => e.Seq), Is.EqualTo(new[] { 1, 2 }));
        Assert.That(entries.Select(e => e.Message), Is.EqualTo(new[] { "a", "b" }));
    }

    [Test]
    public void Log_IgnoresEmptyMessages()
    {
        var log = new SessionDiagnosticsLog();
        log.Log(DiagnosticCategory.Model, "");
        Assert.That(log.Count, Is.Zero);
    }

    [Test]
    public void Buffer_EvictsOldestPastCapacity_ButKeepsSequenceMonotonic()
    {
        var log = new SessionDiagnosticsLog(capacity: 3);
        for (var i = 1; i <= 5; i++)
            log.Log(DiagnosticCategory.Model, $"m{i}");

        var entries = log.Snapshot();
        Assert.That(entries, Has.Count.EqualTo(3));
        // Oldest two evicted; sequence numbers reflect total appended, not buffer position.
        Assert.That(entries.Select(e => e.Message), Is.EqualTo(new[] { "m3", "m4", "m5" }));
        Assert.That(entries.Select(e => e.Seq), Is.EqualTo(new[] { 3, 4, 5 }));
    }

    [Test]
    public void Clear_EmptiesBuffer()
    {
        var log = new SessionDiagnosticsLog();
        log.Log(DiagnosticCategory.Session, "x");
        log.Clear();
        Assert.That(log.Count, Is.Zero);
        Assert.That(log.Snapshot(), Is.Empty);
    }

    [Test]
    public void Constructor_RejectsNonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionDiagnosticsLog(0));
    }
}
