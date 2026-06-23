using NUnit.Framework;
using Yamca.Agent.Workspace;

namespace Yamca.Agent.Tests.Workspace;

[TestFixture]
public class WorkspaceScaffoldTests
{
    private string _repoRoot = null!;

    [SetUp]
    public void SetUp()
    {
        var baseDir = Path.GetFullPath(Path.GetTempPath());
        _repoRoot = Path.Combine(baseDir, "yamca-tests", "repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoRoot);
    }

    [TearDown]
    public void TearDown()
    {
        try { if (Directory.Exists(_repoRoot)) Directory.Delete(_repoRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    private string GitignorePath => Path.Combine(_repoRoot, ".yamca", ".gitignore");

    [Test]
    public void EnsureGitignore_FreshRepo_CreatesFileWithRequiredRules()
    {
        WorkspaceScaffold.EnsureGitignore(_repoRoot);

        Assert.That(File.Exists(GitignorePath), Is.True);

        var lines = File.ReadAllLines(GitignorePath).Select(l => l.Trim()).ToArray();
        Assert.That(lines, Does.Contain("/chat/"));
        Assert.That(lines, Does.Contain("/project.json"));
        // The dedicated throughput-metrics store must be ignored too.
        Assert.That(lines, Does.Contain("/metrics.db"));
        // Self-ignoring: the file lists itself so an untracked .gitignore leaves git status clean.
        Assert.That(lines, Does.Contain("/.gitignore"));
    }

    [Test]
    public void EnsureGitignore_CalledTwice_IsNoOp()
    {
        WorkspaceScaffold.EnsureGitignore(_repoRoot);
        var afterFirst = File.ReadAllText(GitignorePath);

        WorkspaceScaffold.EnsureGitignore(_repoRoot);
        var afterSecond = File.ReadAllText(GitignorePath);

        Assert.That(afterSecond, Is.EqualTo(afterFirst));
    }

    [Test]
    public void EnsureGitignore_ExistingFileMissingRule_AppendsRuleAndPreservesContent()
    {
        var yamcaDir = Path.Combine(_repoRoot, ".yamca");
        Directory.CreateDirectory(yamcaDir);
        // A pre-existing file that already silences itself but not the chat directory, plus an
        // unrelated custom rule yamca does not manage.
        File.WriteAllText(GitignorePath, "/.gitignore\n/my-custom-thing/\n");

        WorkspaceScaffold.EnsureGitignore(_repoRoot);

        var lines = File.ReadAllLines(GitignorePath).Select(l => l.Trim()).ToArray();
        Assert.That(lines, Does.Contain("/chat/"), "missing rule should be appended");
        Assert.That(lines, Does.Contain("/my-custom-thing/"), "unmanaged content should be preserved");
        // The rule that was already present must not be duplicated.
        Assert.That(lines.Count(l => l == "/.gitignore"), Is.EqualTo(1));
    }
}
