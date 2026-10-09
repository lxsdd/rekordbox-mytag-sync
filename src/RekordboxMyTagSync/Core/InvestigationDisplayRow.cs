namespace RekordboxMyTagSync.Core;

/// <summary>
/// Plain, read-only diagnosis. Explicit columns and a short cause code keep
/// ContentId visible while horizontal scrolling long paths / raw DB row IDs.
/// </summary>
public sealed record InvestigationDisplayRow(
    string ReasonCode,
    string? ContentId,
    string? Path,
    string? MyTag,
    string Details)
{
    public static InvestigationDisplayRow FromPreview(PreviewDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var pos = detail.Message.IndexOf(':');
        var reason = pos > 0 ? detail.Message[..pos] : detail.Kind.ToString();
        return new InvestigationDisplayRow(
            reason, detail.ContentId, detail.Path,
            detail.Tag is null ? null : detail.Tag.Group + " / " + detail.Tag.Value,
            detail.Message);
    }
}
