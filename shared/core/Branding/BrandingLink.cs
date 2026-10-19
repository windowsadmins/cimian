namespace Cimian.Core.Branding;

/// <summary>Where a banner click goes.</summary>
public enum BrandingLinkKind
{
    /// <summary>A page inside Managed Software Center, as a <c>cimian://</c> URI.</summary>
    Internal,

    /// <summary>A web page, opened in the default browser.</summary>
    External,
}

/// <summary>
/// A banner's click target. Accepts Cimian's own <c>cimian://</c> links, Munki's
/// <c>munki://</c> page names (so a Munki <c>showcase_template.html</c> href carries over),
/// and http(s) URLs. Anything else is ignored and the banner is not clickable.
/// </summary>
public sealed record BrandingLink(BrandingLinkKind Kind, Uri Uri)
{
    /// <summary>Parses a banner <c>href</c>; null when it is empty, <c>#</c> or not supported.</summary>
    public static BrandingLink? Parse(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
            return null;

        var text = href.Trim();
        if (text == "#")
            return null;

        // Munki page names are not valid URI hosts once they carry a space or %20,
        // so they are read as text before any URI parsing.
        if (text.StartsWith("munki://", StringComparison.OrdinalIgnoreCase))
            return FromMunki(text);

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return null;

        return uri.Scheme.ToLowerInvariant() switch
        {
            "http" or "https" => new BrandingLink(BrandingLinkKind.External, uri),
            "cimian" => new BrandingLink(BrandingLinkKind.Internal, uri),
            _ => null,
        };
    }

    // Munki addresses pages as munki://<page>, where <page> is "updates", "categories",
    // "myitems", or a prefix plus a name: detail-, category-, updatedetail-, filter-,
    // developer- (mschtml.swift). The name may already be percent-encoded.
    private static BrandingLink? FromMunki(string text)
    {
        var page = text["munki://".Length..].Trim('/');
        var cut = page.IndexOfAny(['?', '#']);
        if (cut >= 0)
            page = page[..cut];

        static BrandingLink Internal(string path) =>
            new(BrandingLinkKind.Internal, new Uri($"cimian://{path}"));

        static string Escape(string name) => Uri.EscapeDataString(Uri.UnescapeDataString(name));

        foreach (var (prefix, verb) in new[]
                 {
                     ("updatedetail-", "updatedetail"),
                     ("detail-", "item"),
                     ("category-", "category"),
                 })
        {
            if (page.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var name = page[prefix.Length..];
                return name.Length == 0 ? Internal("software") : Internal($"{verb}/{Escape(name)}");
            }
        }

        return page.ToLowerInvariant() switch
        {
            "updates" or "categories" or "myitems" => Internal(page.ToLowerInvariant()),
            "" or "software" => Internal("software"),
            // filter- and developer- list pages have no Cimian equivalent yet.
            _ when page.StartsWith("filter-", StringComparison.OrdinalIgnoreCase)
                || page.StartsWith("developer-", StringComparison.OrdinalIgnoreCase) => Internal("software"),
            _ => null,
        };
    }
}
