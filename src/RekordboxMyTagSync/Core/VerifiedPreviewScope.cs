namespace RekordboxMyTagSync.Core;

public sealed record VerifiedPreviewScope(
    IReadOnlyList<BridgeTrack> Sources,
    IReadOnlyList<RekordboxTrackSnapshot> Targets,
    IReadOnlyList<PreviewDetail> ExcludedTargets,
    PathAlias TemporaryAlias);
