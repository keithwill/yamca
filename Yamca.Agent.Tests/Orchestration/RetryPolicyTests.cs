using NUnit.Framework;
using Yamca.Agent.Orchestration;

namespace Yamca.Agent.Tests.Orchestration;

[TestFixture]
public class RetryPolicyTests
{
    private static readonly TimeSpan Base = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Max = TimeSpan.FromSeconds(600);

    [Test]
    public void DelayFor_DoublesPerAttempt()
    {
        Assert.That(RetryPolicy.DelayFor(1, Base, Max), Is.EqualTo(TimeSpan.FromSeconds(30)));
        Assert.That(RetryPolicy.DelayFor(2, Base, Max), Is.EqualTo(TimeSpan.FromSeconds(60)));
        Assert.That(RetryPolicy.DelayFor(3, Base, Max), Is.EqualTo(TimeSpan.FromSeconds(120)));
        Assert.That(RetryPolicy.DelayFor(4, Base, Max), Is.EqualTo(TimeSpan.FromSeconds(240)));
    }

    [Test]
    public void DelayFor_ClampsToMax()
    {
        Assert.That(RetryPolicy.DelayFor(6, Base, Max), Is.EqualTo(Max));
        Assert.That(RetryPolicy.DelayFor(100, Base, Max), Is.EqualTo(Max));
    }

    [Test]
    public void DelayFor_TreatsNonPositiveAttemptAsFirst()
    {
        Assert.That(RetryPolicy.DelayFor(0, Base, Max), Is.EqualTo(Base));
        Assert.That(RetryPolicy.DelayFor(-3, Base, Max), Is.EqualTo(Base));
    }

    [Test]
    public void ShouldPark_AtAttemptCap()
    {
        Assert.That(RetryPolicy.ShouldPark(2, 3), Is.False);
        Assert.That(RetryPolicy.ShouldPark(3, 3), Is.True);
        Assert.That(RetryPolicy.ShouldPark(4, 3), Is.True);
    }

    [Test]
    public void ShouldPark_ZeroMaxAttempts_ParksImmediately()
    {
        Assert.That(RetryPolicy.ShouldPark(1, 0), Is.True);
    }
}
