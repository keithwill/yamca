using System.Collections.Concurrent;
using System.Reflection;

namespace Yamca.Web.Services;

/// <summary>
/// Loads the long-form help markdown shown in the settings help modals. The
/// canonical copies live in the repo's <c>doc/</c> folder and are embedded into
/// this assembly (see <c>Yamca.Web.csproj</c>) under the logical name
/// <c>doc.&lt;key&gt;.md</c>, so they travel with the packed global tool.
/// </summary>
public static class HelpDocs
{
    private static readonly Assembly Assembly = typeof(HelpDocs).Assembly;
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    /// <summary>
    /// Returns the raw markdown for the given doc <paramref name="key"/> (a file
    /// name without extension, e.g. <c>"tools-and-permissions"</c>), or an empty
    /// string when no matching doc is embedded. Help-specific cleanup (dropping the
    /// leading heading, images, and cross-document links) happens at render time in
    /// <see cref="MarkdownRenderer.ToHelpHtml"/>.
    /// </summary>
    public static string Load(string key)
        => Cache.GetOrAdd(key, static k =>
        {
            using var stream = Assembly.GetManifestResourceStream($"doc.{k}.md");
            if (stream is null)
                return string.Empty;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        });
}
