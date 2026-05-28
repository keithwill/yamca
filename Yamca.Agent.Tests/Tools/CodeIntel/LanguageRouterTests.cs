using NUnit.Framework;
using Yamca.Agent.Tools.CodeIntel;

namespace Yamca.Agent.Tests.Tools.CodeIntel;

[TestFixture]
public class LanguageRouterTests
{
    [TestCase("Foo.cs",          "c-sharp")]
    [TestCase("foo.py",          "python")]
    [TestCase("App.tsx",         "tsx")]
    [TestCase("module.ts",       "typescript")]
    [TestCase("script.js",       "javascript")]
    [TestCase("worker.mjs",      "javascript")]
    [TestCase("main.rs",         "rust")]
    [TestCase("main.go",         "go")]
    [TestCase("script.sh",       "bash")]
    [TestCase("README.HS",       "haskell")]    // case-insensitive
    [TestCase("nested/dir/x.rb", "ruby")]
    public void KnownExtensions_RouteToExpectedLanguage(string path, string expected)
    {
        Assert.That(LanguageRouter.GetLanguageId(path), Is.EqualTo(expected));
    }

    [TestCase("Dockerfile")]   // no extension, no special-case wired yet
    [TestCase("README.md")]    // markdown not in MVP
    [TestCase("data.xml")]     // unsupported
    [TestCase("noext")]
    [TestCase("")]
    public void UnsupportedPaths_ReturnNull(string path)
    {
        Assert.That(LanguageRouter.GetLanguageId(path), Is.Null);
    }

    [Test]
    public void SupportedLanguageIds_CoversCommonLanguages()
    {
        Assert.That(LanguageRouter.SupportedLanguageIds,
            Is.SupersetOf(new[] { "c-sharp", "python", "typescript", "javascript", "rust", "go" }));
    }
}
