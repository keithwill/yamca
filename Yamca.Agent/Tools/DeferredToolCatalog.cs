using System.Text;
using System.Text.Json;

namespace Yamca.Agent.Tools;

/// <summary>Shared formatting for the deferred-tool catalog and schemas. Both
/// <c>lookup_tool</c> and the agent loop's dispatch self-correction render through
/// here so the model sees identical output regardless of how it discovered a tool.
///
/// IMPORTANT (cache): catalog/schema text produced here is delivered to the model as
/// <em>tool-result content at the tail of the conversation</em> (or as a frozen
/// session-start message), never as part of the prefix tool array. That is what lets
/// deferred tools exist without invalidating llama-server's prompt prefix cache when
/// they are discovered or invoked late in a session.</summary>
internal static class DeferredToolCatalog
{
    /// <summary>One <c>name — summary</c> line per deferred tool. Used by the
    /// session-start hint and by <c>lookup_tool</c> when called with no arguments.</summary>
    public static string Summaries(IReadOnlyList<ITool> deferred)
    {
        if (deferred.Count == 0) return "(no deferred tools are currently available)";
        var sb = new StringBuilder();
        for (var i = 0; i < deferred.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append("- ").Append(deferred[i].Name).Append(" — ").Append(FirstLine(deferred[i].Description));
        }
        return sb.ToString();
    }

    /// <summary>Comma-separated deferred tool names — the most compact catalog cue.</summary>
    public static string Names(IReadOnlyList<ITool> deferred) =>
        deferred.Count == 0
            ? "(no deferred tools are currently available)"
            : string.Join(", ", deferred.Select(t => t.Name));

    /// <summary>A JSON array of <c>{ name, description, parameters }</c> objects giving the
    /// full argument schema for each tool. Used by <c>lookup_tool</c> with explicit names and
    /// by the dispatch self-correction path.</summary>
    public static string Schemas(IReadOnlyList<ITool> tools)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartArray();
            foreach (var t in tools)
            {
                w.WriteStartObject();
                w.WriteString("name", t.Name);
                w.WriteString("description", t.Description);
                w.WritePropertyName("parameters");
                using var doc = JsonDocument.Parse(t.ParametersSchema);
                doc.RootElement.WriteTo(w);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private const int MaxSummaryLength = 160;

    private static string FirstLine(string description)
    {
        if (string.IsNullOrWhiteSpace(description)) return "(no description)";
        var nl = description.IndexOf('\n');
        var line = (nl >= 0 ? description[..nl] : description).Trim();
        return line.Length > MaxSummaryLength ? line[..MaxSummaryLength].TrimEnd() + "…" : line;
    }
}
