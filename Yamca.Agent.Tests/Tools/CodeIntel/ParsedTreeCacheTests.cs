using NUnit.Framework;
using Yamca.Agent.Tools.CodeIntel;

namespace Yamca.Agent.Tests.Tools.CodeIntel;

[TestFixture]
public class ParsedTreeCacheTests
{
    [Test]
    public void MissingEntry_TryGetReturnsFalse()
    {
        var cache = new ParsedTreeCache();
        Assert.That(cache.TryGet("c:/no/such/file.cs", DateTime.UtcNow, 1, out _), Is.False);
    }

    [Test]
    public void Set_ThenTryGet_ReturnsValue()
    {
        var cache = new ParsedTreeCache();
        var t = new DateTime(2026, 5, 28, 12, 0, 0, DateTimeKind.Utc);

        cache.Set("c:/a.cs", t, 100, "rendered");

        Assert.That(cache.TryGet("c:/a.cs", t, 100, out var got), Is.True);
        Assert.That(got, Is.EqualTo("rendered"));
    }

    [Test]
    public void MtimeChange_InvalidatesEntry()
    {
        var cache = new ParsedTreeCache();
        cache.Set("c:/a.cs", new DateTime(2026, 5, 28, 12, 0, 0, DateTimeKind.Utc), 100, "old");

        Assert.That(cache.TryGet("c:/a.cs", new DateTime(2026, 5, 28, 12, 0, 1, DateTimeKind.Utc), 100, out _),
            Is.False);
    }

    [Test]
    public void SizeChange_InvalidatesEntry()
    {
        var cache = new ParsedTreeCache();
        var t = new DateTime(2026, 5, 28, 12, 0, 0, DateTimeKind.Utc);
        cache.Set("c:/a.cs", t, 100, "old");

        Assert.That(cache.TryGet("c:/a.cs", t, 101, out _), Is.False);
    }

    [Test]
    public void PathLookup_IsCaseInsensitive()
    {
        var cache = new ParsedTreeCache();
        var t = new DateTime(2026, 5, 28, 12, 0, 0, DateTimeKind.Utc);
        cache.Set("C:/Repos/X.cs", t, 1, "v");

        Assert.That(cache.TryGet("c:/repos/x.cs", t, 1, out var got), Is.True);
        Assert.That(got, Is.EqualTo("v"));
    }
}
