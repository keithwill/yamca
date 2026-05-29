using NUnit.Framework;
using Yamca.Agent.Workspace;
using WorkspaceImpl = Yamca.Agent.Workspace.Workspace;

namespace Yamca.Agent.Tests.Workspace;

[TestFixture]
public class WorkspaceTests
{
    private string _root = null!;
    private string _outsideDir = null!;

    [SetUp]
    public void SetUp()
    {
        // Resolve %TEMP% through GetFullPath so the test root matches the same canonical
        // form Workspace will produce (avoids 8.3-name mismatch on Windows).
        var baseDir = Path.GetFullPath(Path.GetTempPath());

        _root = Path.Combine(baseDir, "yamca-tests", "root-" + Guid.NewGuid().ToString("N"));
        _outsideDir = Path.Combine(baseDir, "yamca-tests", "outside-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outsideDir);
    }

    [TearDown]
    public void TearDown()
    {
        TryDelete(_root);
        TryDelete(_outsideDir);
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* best-effort */ }
    }

    [Test]
    public void Constructor_TrimsTrailingSeparator()
    {
        var ws = new WorkspaceImpl(_root + Path.DirectorySeparatorChar);

        Assert.That(ws.RootPath, Is.EqualTo(_root));
    }

    [Test]
    public void RepositoryRoot_DefaultsToRootPath_WhenNotSupplied()
    {
        var ws = new WorkspaceImpl(_root);

        Assert.That(ws.RepositoryRoot, Is.EqualTo(_root));
    }

    [Test]
    public void RepositoryRoot_CanSitAboveRootPath()
    {
        // Open a subdirectory while the repository root is its parent — the case this feature fixes.
        var sub = Path.Combine(_root, "src", "feature");
        Directory.CreateDirectory(sub);

        var ws = new WorkspaceImpl(sub, _root);

        Assert.That(ws.RootPath, Is.EqualTo(sub));
        Assert.That(ws.RepositoryRoot, Is.EqualTo(_root));
    }

    [Test]
    public void RepositoryRoot_TrimsTrailingSeparator()
    {
        var ws = new WorkspaceImpl(_root, _outsideDir + Path.DirectorySeparatorChar);

        Assert.That(ws.RepositoryRoot, Is.EqualTo(_outsideDir));
    }

    [Test]
    public void RepositoryRoot_FallsBackToRootPath_WhenNonExistent()
    {
        var missing = Path.Combine(_outsideDir, "does-not-exist");

        var ws = new WorkspaceImpl(_root, missing);

        Assert.That(ws.RepositoryRoot, Is.EqualTo(_root));
    }

    [Test]
    public void Constructor_NonExistentRoot_Throws()
    {
        var missing = Path.Combine(_root, "does-not-exist");

        Assert.Throws<DirectoryNotFoundException>(() => new WorkspaceImpl(missing));
    }

    [Test]
    public void Constructor_NullOrEmpty_Throws()
    {
        Assert.Throws<ArgumentException>(() => new WorkspaceImpl(""));
        Assert.Throws<ArgumentException>(() => new WorkspaceImpl("   "));
    }

    [Test]
    public void Resolve_RelativePath_CombinesWithRoot()
    {
        var ws = new WorkspaceImpl(_root);

        var resolved = ws.Resolve("file.txt");

        Assert.That(resolved, Is.EqualTo(Path.Combine(_root, "file.txt")));
    }

    [Test]
    public void Resolve_NestedRelativePath_Works()
    {
        var ws = new WorkspaceImpl(_root);

        var resolved = ws.Resolve(Path.Combine("sub", "dir", "file.txt"));

        Assert.That(resolved, Is.EqualTo(Path.Combine(_root, "sub", "dir", "file.txt")));
    }

    [Test]
    public void Resolve_AbsolutePathInsideRoot_Returns()
    {
        var ws = new WorkspaceImpl(_root);
        var inside = Path.Combine(_root, "inside.txt");

        var resolved = ws.Resolve(inside);

        Assert.That(resolved, Is.EqualTo(inside));
    }

    [Test]
    public void Resolve_RelativeWithDotDotInsideRoot_Works()
    {
        var ws = new WorkspaceImpl(_root);

        var resolved = ws.Resolve(Path.Combine("sub", "..", "file.txt"));

        Assert.That(resolved, Is.EqualTo(Path.Combine(_root, "file.txt")));
    }

    [Test]
    public void Resolve_DotDotEscape_Throws()
    {
        var ws = new WorkspaceImpl(_root);

        var ex = Assert.Throws<PathOutsideWorkspaceException>(
            () => ws.Resolve(Path.Combine("..", "escape.txt")));

        Assert.That(ex!.RootPath, Is.EqualTo(_root));
        Assert.That(ex.ResolvedPath, Does.Not.StartWith(_root));
    }

    [Test]
    public void Resolve_DeeplyNestedDotDotEscape_Throws()
    {
        var ws = new WorkspaceImpl(_root);

        Assert.Throws<PathOutsideWorkspaceException>(
            () => ws.Resolve(Path.Combine("a", "b", "c", "..", "..", "..", "..", "escape.txt")));
    }

    [Test]
    public void Resolve_AbsolutePathOutsideRoot_Throws()
    {
        var ws = new WorkspaceImpl(_root);

        Assert.Throws<PathOutsideWorkspaceException>(
            () => ws.Resolve(Path.Combine(_outsideDir, "leak.txt")));
    }

    [Test]
    public void Resolve_SiblingWithSharedPrefix_Throws()
    {
        // Defends against a naive StartsWith check: a root of ".../foo" must NOT
        // be considered to contain ".../foo-sibling/file".
        var sibling = _root + "-sibling";
        Directory.CreateDirectory(sibling);
        try
        {
            var ws = new WorkspaceImpl(_root);

            Assert.Throws<PathOutsideWorkspaceException>(
                () => ws.Resolve(Path.Combine(sibling, "file.txt")));
        }
        finally
        {
            TryDelete(sibling);
        }
    }

    [Test]
    public void Resolve_RootItself_Returns()
    {
        var ws = new WorkspaceImpl(_root);

        var resolved = ws.Resolve(_root);

        Assert.That(resolved, Is.EqualTo(_root));
    }

    [Test]
    public void Resolve_NullOrEmpty_Throws()
    {
        var ws = new WorkspaceImpl(_root);

        Assert.Throws<ArgumentException>(() => ws.Resolve(""));
        Assert.Throws<ArgumentNullException>(() => ws.Resolve(null!));
    }

    [Test]
    public void Resolve_SymlinkInsideRootPointingOutside_Throws()
    {
        // Creating symlinks on Windows requires admin or Developer Mode. Skip if the
        // sandbox doesn't permit it — the test exists to assert behavior where it does.
        var linkPath = Path.Combine(_root, "link-to-outside");
        try
        {
            Directory.CreateSymbolicLink(linkPath, _outsideDir);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Ignore($"Cannot create symlink in this environment: {ex.Message}");
            return;
        }

        var ws = new WorkspaceImpl(_root);

        Assert.Throws<PathOutsideWorkspaceException>(
            () => ws.Resolve(Path.Combine("link-to-outside", "leak.txt")));
    }

    [Test]
    public void Resolve_SymlinkInsideRootPointingInside_Works()
    {
        var innerTarget = Path.Combine(_root, "real-dir");
        Directory.CreateDirectory(innerTarget);

        var linkPath = Path.Combine(_root, "link-to-inside");
        try
        {
            Directory.CreateSymbolicLink(linkPath, innerTarget);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Ignore($"Cannot create symlink in this environment: {ex.Message}");
            return;
        }

        var ws = new WorkspaceImpl(_root);

        var resolved = ws.Resolve(Path.Combine("link-to-inside", "file.txt"));

        Assert.That(resolved, Is.EqualTo(Path.Combine(innerTarget, "file.txt")));
    }
}
