using NUnit.Framework;
using Yamca.Web.Services.Metrics;

namespace Yamca.Web.Tests;

[TestFixture]
public class MetricSeriesKeyLabelTests
{
    private static readonly System.Guid Ep = System.Guid.Parse("7dede56b-0000-0000-0000-000000000000");

    [Test]
    public void NameAndModel_JoinedWithDot()
    {
        var key = new MetricSeriesKey(Ep, "glm-5.1");
        Assert.That(key.Label("z.ai", "http://x"), Is.EqualTo("z.ai · glm-5.1"));
    }

    [Test]
    public void NameOnly_UsesName_NotUrl()
    {
        var key = new MetricSeriesKey(Ep, "");
        Assert.That(key.Label("z.ai", "http://x"), Is.EqualTo("z.ai"));
    }

    [Test]
    public void ModelOnly_UsesModel()
    {
        var key = new MetricSeriesKey(Ep, "glm-5.1");
        Assert.That(key.Label("", "http://x"), Is.EqualTo("glm-5.1"));
    }

    [Test]
    public void NoNameNoModel_PrefersUrlOverEndpointId()
    {
        var key = new MetricSeriesKey(Ep, "");
        Assert.That(key.Label("", "http://localhost:8080"), Is.EqualTo("http://localhost:8080"));
    }

    [Test]
    public void NoNameNoModel_NoUrl_FallsBackToShortEndpointId()
    {
        var key = new MetricSeriesKey(Ep, "");
        Assert.That(key.Label("", null), Is.EqualTo("endpoint 7dede56b"));
        Assert.That(key.Label(""), Is.EqualTo("endpoint 7dede56b")); // default url arg
    }

    [Test]
    public void NoNameNoModel_NoUrl_EmptyGuid_IsUnknown()
    {
        var key = new MetricSeriesKey(System.Guid.Empty, "");
        Assert.That(key.Label("", "   "), Is.EqualTo("(unknown endpoint)")); // whitespace url ignored
    }
}
