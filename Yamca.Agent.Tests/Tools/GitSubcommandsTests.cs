using NUnit.Framework;
using Yamca.Agent.Tools.Git;

namespace Yamca.Agent.Tests.Tools;

[TestFixture]
public class GitSubcommandsTests
{
    [TestCase("status")]
    [TestCase("log")]
    [TestCase("diff")]
    [TestCase("show")]
    [TestCase("blame")]
    public void TryClassify_ReadOps_ClassifiedAsRead(string op)
    {
        Assert.That(GitSubcommands.TryClassify(op, out var isWrite), Is.True);
        Assert.That(isWrite, Is.False);
    }

    [TestCase("add")]
    [TestCase("commit")]
    [TestCase("restore")]
    [TestCase("switch")]
    [TestCase("branch")]
    [TestCase("stash")]
    [TestCase("fetch")]
    [TestCase("pull")]
    [TestCase("push")]
    public void TryClassify_WriteOps_ClassifiedAsWrite(string op)
    {
        Assert.That(GitSubcommands.TryClassify(op, out var isWrite), Is.True);
        Assert.That(isWrite, Is.True);
    }

    [TestCase("rebase")]   // intentionally not curated — complex/destructive
    [TestCase("reset")]
    [TestCase("clean")]
    [TestCase("config")]
    [TestCase("Status")]   // case-sensitive: git subcommands are lowercase
    [TestCase("")]
    [TestCase("status; rm -rf /")]
    public void TryClassify_UncuratedOps_Rejected(string op)
    {
        Assert.That(GitSubcommands.TryClassify(op, out _), Is.False);
    }

    [Test]
    public void ReadAndWriteSets_DoNotOverlap()
    {
        Assert.That(GitSubcommands.Read.Overlaps(GitSubcommands.Write), Is.False);
    }
}
