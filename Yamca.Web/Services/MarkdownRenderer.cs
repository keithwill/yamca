using Markdig;

namespace Yamca.Web.Services;

public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    public static string ToHtml(string markdown)
        => string.IsNullOrEmpty(markdown) ? string.Empty : Markdown.ToHtml(markdown, Pipeline);
}
