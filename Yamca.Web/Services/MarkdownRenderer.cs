using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Yamca.Web.Services;

public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    public static string ToHtml(string markdown)
        => string.IsNullOrEmpty(markdown) ? string.Empty : Markdown.ToHtml(markdown, Pipeline);

    /// <summary>
    /// Renders a <c>doc/*.md</c> file for display in a settings help modal. The
    /// docs are written for a docs site, so before rendering we strip the parts
    /// that don't resolve inside the modal: the leading <c>#</c> title (it would
    /// duplicate the dialog's own title), images (their files aren't served),
    /// cross-document / in-page-anchor links (unwrapped to plain text), and the
    /// trailing "See also" sections (lists of links to other docs).
    /// </summary>
    public static string ToHelpHtml(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return string.Empty;

        var document = Markdown.Parse(markdown, Pipeline);
        StripForHelp(document);

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
    }

    private static readonly string[] RelatedHeadings = ["see also", "related", "further reading"];

    private static void StripForHelp(MarkdownDocument document)
    {
        // 1. Drop the doc's leading H1 so it doesn't duplicate the modal title.
        if (document.Count > 0 && document[0] is HeadingBlock { Level: 1 })
            document.RemoveAt(0);

        // 2. Drop "related links" sections (e.g. "## See also") whole — the heading
        //    plus everything under it up to the next heading of the same or higher
        //    level. They're just lists of cross-document links, useless in the modal.
        RemoveRelatedSections(document);

        // 3. Strip images and unwrap cross-document / anchor links to plain text.
        foreach (var link in document.Descendants<LinkInline>().ToList())
        {
            if (link.IsImage)
                link.Remove();
            else if (!IsResolvableUrl(link.Url))
                Unwrap(link);
        }

        // 4. Remove paragraphs left empty once a standalone image was stripped.
        foreach (var paragraph in document.Descendants<ParagraphBlock>().ToList())
        {
            if (paragraph.Inline?.FirstChild is null)
                (paragraph.Parent as ContainerBlock)?.Remove(paragraph);
        }
    }

    private static void RemoveRelatedSections(MarkdownDocument document)
    {
        var blocks = document.ToList();
        for (var i = 0; i < blocks.Count; i++)
        {
            if (blocks[i] is not HeadingBlock heading)
                continue;
            if (!RelatedHeadings.Contains(HeadingText(heading).Trim().ToLowerInvariant()))
                continue;

            document.Remove(heading);
            for (var j = i + 1; j < blocks.Count; j++)
            {
                if (blocks[j] is HeadingBlock next && next.Level <= heading.Level)
                    break;
                document.Remove(blocks[j]);
            }
        }
    }

    private static string HeadingText(HeadingBlock heading)
        => heading.Inline is null
            ? string.Empty
            : string.Concat(heading.Inline.Descendants<LiteralInline>().Select(l => l.Content.ToString()));

    private static bool IsResolvableUrl(string? url)
        => url is not null &&
           (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase));

    /// <summary>Replaces a link with its own child inlines, keeping the text but
    /// dropping the (unresolvable) anchor.</summary>
    private static void Unwrap(LinkInline link)
    {
        Inline? child;
        while ((child = link.LastChild) is not null)
        {
            child.Remove();
            link.InsertAfter(child);
        }
        link.Remove();
    }
}
