using NUnit.Framework;
using Yamca.Agent.Tools.ScriptExecution;
using Yamca.Agent.Tools.ShellExecution;

namespace Yamca.Agent.Tests.Tools;

[TestFixture]
public class ShellResolverTests
{
    private static ShellResolver NewResolver() => new(new InterpreterResolver());

    [Test]
    public void Auto_PicksDocumentedShellForThisOs()
    {
        var resolution = NewResolver().ResolveDetailed(ShellPreference.Auto);

        Assert.That(resolution.FellBack, Is.False);
        Assert.That(resolution.Requested, Is.EqualTo(ShellPreference.Auto));
        Assert.That(resolution.Shell.ExecutablePath, Is.Not.Empty);

        if (OperatingSystem.IsWindows())
            Assert.That(resolution.Shell.Kind, Is.AnyOf(ShellKind.Pwsh, ShellKind.WindowsPowerShell, ShellKind.Cmd));
        else
            Assert.That(resolution.Shell.Kind, Is.AnyOf(ShellKind.Bash, ShellKind.Sh));
    }

    [Test]
    public void UnavailablePreference_FallsBackToAuto()
    {
        var resolver = NewResolver();
        // Pick a preference that cannot resolve on this OS: cmd/Windows PowerShell are
        // Windows-only; bash is the non-Windows analogue. The "wrong-OS" choice falls back.
        var wrongOsPreference = OperatingSystem.IsWindows() ? ShellPreference.Sh : ShellPreference.Cmd;

        var resolution = resolver.ResolveDetailed(wrongOsPreference);

        // sh may legitimately exist on Windows via Git/MSYS on PATH; only assert fallback
        // semantics for the choice we know is impossible on each OS.
        if (!OperatingSystem.IsWindows())
        {
            Assert.That(resolution.FellBack, Is.True);
            Assert.That(resolution.Requested, Is.EqualTo(ShellPreference.Cmd));
            Assert.That(resolution.Shell.Kind, Is.AnyOf(ShellKind.Bash, ShellKind.Sh));
        }
    }

    [Test]
    public void BuildCommandStartInfo_MapsArgumentsToResolvedKind()
    {
        var resolver = NewResolver();
        var resolution = resolver.ResolveDetailed(ShellPreference.Auto);

        var psi = resolver.BuildCommandStartInfo("echo hi", workingDirectory: Path.GetTempPath());

        Assert.That(psi.FileName, Is.EqualTo(resolution.Shell.ExecutablePath));
        Assert.That(psi.ArgumentList, Does.Contain("echo hi"));

        switch (resolution.Shell.Kind)
        {
            case ShellKind.Pwsh:
            case ShellKind.WindowsPowerShell:
                Assert.That(psi.ArgumentList, Does.Contain("-Command"));
                Assert.That(psi.ArgumentList, Does.Contain("-NoProfile"));
                break;
            case ShellKind.Cmd:
                Assert.That(psi.ArgumentList, Does.Contain("/c"));
                break;
            case ShellKind.GitBash:
            case ShellKind.Bash:
            case ShellKind.Sh:
                Assert.That(psi.ArgumentList, Does.Contain("-c"));
                break;
        }
    }

    [Test]
    public void AvailablePreferences_ContainsTheAutoResolvedShell_AndNoFallbacks()
    {
        var resolver = NewResolver();
        var available = resolver.AvailablePreferences();

        Assert.That(available, Does.Not.Contain(ShellPreference.Auto), "Auto is implicit, never listed");

        // Every listed preference must genuinely resolve to its own shell (no fallback).
        foreach (var pref in available)
            Assert.That(resolver.ResolveDetailed(pref).FellBack, Is.False, $"{pref} was listed but falls back");

        // The shell auto-detect lands on is, by definition, installed — so its preference is listed.
        var autoKind = resolver.ResolveDetailed(ShellPreference.Auto).Shell.Kind;
        var expected = autoKind switch
        {
            ShellKind.Pwsh => ShellPreference.Pwsh,
            ShellKind.WindowsPowerShell => ShellPreference.WindowsPowerShell,
            ShellKind.Cmd => ShellPreference.Cmd,
            ShellKind.GitBash => ShellPreference.GitBash,
            ShellKind.Bash => ShellPreference.Bash,
            ShellKind.Sh => ShellPreference.Sh,
            _ => (ShellPreference?)null,
        };
        Assert.That(available, Does.Contain(expected!.Value));
    }

    [Test]
    public void ResolveDetailed_IsCachedPerPreference()
    {
        var resolver = NewResolver();

        var first = resolver.ResolveDetailed(ShellPreference.Auto);
        var second = resolver.ResolveDetailed(ShellPreference.Auto);

        Assert.That(second, Is.SameAs(first));
    }
}
