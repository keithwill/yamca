using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Yamca.Agent.Tools.CodeIntel;

namespace Yamca.Agent.Tests.Tools.CodeIntel;

/// <summary>
/// Keeps the four things that must agree about supported languages in lockstep:
/// what <see cref="LanguageRouter"/> routes, which <see cref="ISymbolExtractor"/>s exist,
/// which <see cref="ILanguageNodeProfile"/>s exist, and which tree-sitter grammar binaries
/// survive the <c>TrimUnusedTreeSitterGrammars</c> prune in <c>Yamca.Web.csproj</c>.
/// Drift here is otherwise silent: a routed language with no kept grammar parses fine in
/// tests (they hit the full NuGet cache) but dead-ends in the packaged tool.
/// </summary>
[TestFixture]
public class GrammarPackagingGuardTests
{
    private static readonly string[] RoutedLanguageIds =
        LanguageRouter.SupportedLanguageIds.ToArray();

    [Test]
    public void EveryRoutedLanguage_HasAnExtractor()
    {
        Assert.That(RoutedLanguageIds, Is.EquivalentTo(LanguageIdsOf<ISymbolExtractor>()),
            "LanguageRouter and the ISymbolExtractor set have drifted — add/remove the extractor "
            + "or the route so each routed language has exactly one extractor.");
    }

    [Test]
    public void EveryRoutedLanguage_HasADedicatedNodeProfile()
    {
        // The generic profile ("*") is a fallback, not a per-language entry, so it's excluded.
        Assert.That(RoutedLanguageIds, Is.SubsetOf(LanguageIdsOf<ILanguageNodeProfile>()),
            "A routed language has no dedicated ILanguageNodeProfile — add one (or drop the route).");
    }

    [Test]
    public void RoutedLanguages_MatchKeptGrammarBinaries()
    {
        Assert.That(RoutedLanguageIds, Is.EquivalentTo(KeptGrammarLanguageIds()),
            "LanguageRouter and the kept-grammar list in Yamca.Web.csproj have drifted. A routed "
            + "language with no kept grammar fails to parse in the packaged tool; a kept grammar "
            + "with no route is dead weight. Update the _TsKeep globs to match.");
    }

    /// <summary>LanguageIds of every concrete, parameterless <typeparamref name="T"/> in the agent assembly, minus the "*" fallback.</summary>
    private static IEnumerable<string> LanguageIdsOf<T>() => typeof(LanguageRouter).Assembly.GetTypes()
        .Where(t => typeof(T).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false }
                    && t.GetConstructor(Type.EmptyTypes) is not null)
        .Select(t => (string)t.GetProperty(nameof(ISymbolExtractor.LanguageId))!.GetValue(Activator.CreateInstance(t))!)
        .Where(id => id != "*");

    /// <summary>Grammar ids parsed from the <c>_TsKeep</c> globs in Yamca.Web.csproj (the bare core "tree-sitter.*" runtime has no id and is skipped).</summary>
    private static IEnumerable<string> KeptGrammarLanguageIds()
    {
        var csproj = File.ReadAllText(FindWebCsproj());
        return Regex.Matches(csproj, @"runtimes[\\/]\*\*[\\/]\*tree-sitter-([a-z0-9-]+)\.\*")
            .Select(m => m.Groups[1].Value)
            .Distinct();
    }

    private static string FindWebCsproj()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Yamca.Web", "Yamca.Web.csproj");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(
            $"Could not locate Yamca.Web/Yamca.Web.csproj walking up from {AppContext.BaseDirectory}");
    }
}
