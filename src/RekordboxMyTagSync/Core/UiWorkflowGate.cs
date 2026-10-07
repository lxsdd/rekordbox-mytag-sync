namespace RekordboxMyTagSync.Core;

public sealed record UiWorkflowInputs(
    bool SettingsValid,
    bool BridgeSourceSafe,
    bool TargetLibrarySafe,
    bool RekordboxClosed,
    bool DatabaseAccessQualified,
    bool PreviewExists,
    bool PreviewValid,
    bool PreviewFresh,
    bool BackupAvailable,
    bool BackupMatchesTarget);

public sealed record UiWorkflowState(
    bool CanBuildPreview,
    bool CanApply,
    bool CanRestore,
    IReadOnlyList<string> Blockers);

public static class UiWorkflowGate
{
    public static UiWorkflowState Evaluate(UiWorkflowInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var blockers = new List<string>();
        if (!inputs.SettingsValid)
            blockers.Add("Settings are invalid or incomplete.");
        if (!inputs.BridgeSourceSafe)
            blockers.Add("Bridge source is missing, changing, or unsafe.");
        if (!inputs.TargetLibrarySafe)
            blockers.Add("Target rekordbox library is missing, ambiguous, or unsafe.");
        if (!inputs.RekordboxClosed)
            blockers.Add("rekordbox is running.");
        if (!inputs.DatabaseAccessQualified)
            blockers.Add("SQLCipher/database-version compatibility is not qualified.");

        var canBuildPreview =
            inputs.SettingsValid &&
            inputs.BridgeSourceSafe &&
            inputs.TargetLibrarySafe &&
            inputs.RekordboxClosed &&
            inputs.DatabaseAccessQualified;

        if (!inputs.PreviewExists)
            blockers.Add("No preview has been built.");
        else
        {
            if (!inputs.PreviewValid)
                blockers.Add("The preview is invalid or contains conflicts.");
            if (!inputs.PreviewFresh)
                blockers.Add("The preview is stale and must be rebuilt.");
        }

        var canApply =
            canBuildPreview &&
            inputs.PreviewExists &&
            inputs.PreviewValid &&
            inputs.PreviewFresh;

        if (!inputs.BackupAvailable)
            blockers.Add("No validated rolling backup is available.");
        else if (!inputs.BackupMatchesTarget)
            blockers.Add("The rolling backup does not match the selected target library.");

        var canRestore =
            inputs.TargetLibrarySafe &&
            inputs.RekordboxClosed &&
            inputs.BackupAvailable &&
            inputs.BackupMatchesTarget;

        return new UiWorkflowState(
            canBuildPreview,
            canApply,
            canRestore,
            blockers.Distinct(StringComparer.Ordinal).ToArray());
    }
}
