using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using RekordboxMyTagSync.Core;

namespace RekordboxMyTagSync;

public partial class MainWindow
{
    private ReadOnlyIdentityCache? _cachedIdentityEvidence;

    private async Task BuildVerifiedPreviewAsync()
    {
        if (_operationBusy) return;
        if (!_sourceSafe || _bridgeSnapshot is null ||
            !_targetSafe || !_databaseAccessQualified || _databaseSnapshot is null)
        {
            PreviewRunStatusTextBlock.Text =
                "Verified preview blocked: Inspect Source and Validate Target access first.";
            AppendDiagnostic(PreviewRunStatusTextBlock.Text);
            return;
        }

        AppSettings settings;
        try
        {
            settings = BuildSettingsFromUi();
            if (settings.EffectivePathAliases.Count != 0)
                throw new InvalidDataException(
                    "Auto-match expects no configured aliases; review them before running.");
        }
        catch (Exception ex)
        {
            PreviewRunStatusTextBlock.Text = $"Verified preview blocked: {ex.Message}";
            AppendDiagnostic(PreviewRunStatusTextBlock.Text);
            return;
        }

        var sourceSnapshot = _bridgeSnapshot;
        var database = _databaseSnapshot;
        var availableCache = _cachedIdentityEvidence;
        var settingsFingerprint = JsonSerializer.Serialize(settings);
        var folder = settings.EffectiveBridgeDirectory
            ?? throw new InvalidDataException("Bridge directory is missing.");
        using var cancel = new CancellationTokenSource();
        _identityCancellation = cancel;
        InvalidatePreview("Rebuilding verified read-only preview.");
        PreviewGrid.ItemsSource = null;
        PreviewInvestigationGrid.ItemsSource = null;
        PreviewInvestigationStatusTextBlock.Text = "Rechecking identity and resolving individual ContentId/MyTag exceptions…";
        PreviewRunStatusTextBlock.Text =
            "Auto-matching the libraries: refreshing stable source and checking all physical file identities…";
        SetOperationStatus("Verifying physical file identities for MyTag preview — read-only…", busy: true);
        UpdateWorkflowGate();
        await Dispatcher.Yield(DispatcherPriority.Background);

        try
        {
            var progress = new Progress<PhysicalIdentityProgress>(p =>
            {
                if (_identityCancellation != cancel) return;
                PreviewRunStatusTextBlock.Text =
                    $"Verifying unique file pairs: {p.Completed:N0}/{p.Total:N0}. " +
                    "No aliases or database rows are being changed.";
            });
            var result = await Task.Run(() =>
            {
                cancel.Token.ThrowIfCancellationRequested();
                var stable = BridgeSourceDiscovery.ReadStable(folder);
                var proposed = PathMatchAdvisor.Analyze(
                    stable.Tracks, database.Tracks, settings.EffectivePathAliases);
                if (proposed.Proposals.Count != 1)
                    throw new InvalidDataException(
                        $"Found {proposed.Proposals.Count} proposed root mappings; an exactly unique mapping is required.");
                var proposal = proposed.Proposals[0];
                var root = new PathAlias(proposal.SourceRoot, proposal.TargetRoot);
                var reuse = availableCache is not null &&
                    availableCache.IsValidFor(stable, database, root);
                var bridgeFile = new FileInfo(Path.Combine(
                    stable.DirectoryPath, BridgeSourceDiscovery.SnapshotFileName));
                var initialLength = bridgeFile.Length;
                var initialWriteTime = bridgeFile.LastWriteTimeUtc;
                var evidence = reuse
                    ? availableCache!.Report
                    : PhysicalFileIdentityVerifier.Verify(
                        stable.Tracks, database.Tracks, root, cancel.Token, progress);
                cancel.Token.ThrowIfCancellationRequested();
                bridgeFile.Refresh();
                if (!bridgeFile.Exists || bridgeFile.Length != initialLength ||
                    bridgeFile.LastWriteTimeUtc != initialWriteTime)
                    throw new InvalidDataException(
                        "Bridge export changed while checking physical file identity.");
                // This is an observation cache for read-only previews only.
                // Any changed database/source metadata causes fail-closed
                // invalidation rather than silently reusing a stale map.
                var observed = ReadOnlyIdentityCache.Capture(stable, database, root, evidence);
                if (!observed.IsValidFor(stable, database, root))
                    throw new InvalidDataException(
                        "Database/source changed since validation; re-inspect and revalidate.");
                if (evidence.VerifiedPairs is null ||
                    evidence.VerifiedPairs.Count != evidence.SamePhysicalFiles ||
                    evidence.SamePhysicalFiles == 0)
                    throw new InvalidDataException("No valid physical identity evidence available.");

                var scope = VerifiedPreviewScope.Build(
                    stable.Tracks, database.Tracks, root, evidence.VerifiedPairs);

                var stateFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RekordboxMyTagSync", "provenance");
                var provenancePath = ProvenanceStore.GetStatePath(
                    stateFolder, database.Identity);
                var provenance = ProvenanceStore.Load(provenancePath, database.Identity);
                var managed = ProvenanceStore.ToManagedAssignments(provenance, database);
                var includedIds = scope.Targets.Select(x => x.ContentId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var preview = PreviewEngine.Create(new PreviewRequest(
                    database.Identity.PreviewIdentity,
                    scope.Sources,
                    settings.EffectiveMappings,
                    scope.Targets,
                    managed.Where(x => includedIds.Contains(x.ContentId)).ToArray(),
                    new[] { root },
                    database.MyTagDefinitions));
                return (stable, evidence, scope, preview, observed, reuse);
            }, cancel.Token);

            cancel.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(sourceSnapshot, _bridgeSnapshot) ||
                !ReferenceEquals(database, _databaseSnapshot) ||
                !settingsFingerprint.Equals(JsonSerializer.Serialize(BuildSettingsFromUi()),
                    StringComparison.Ordinal) ||
                !_sourceSafe || !_targetSafe || !_databaseAccessQualified)
                throw new InvalidDataException(
                    "Source, target or settings changed during the verified preview.");

            _bridgeSnapshot = result.stable;
            _cachedIdentityEvidence = result.observed;
            PreviewGrid.ItemsSource = result.preview.Details;
            var pathByContentId = database.Tracks.ToDictionary(
                x => x.ContentId, x => x.Path, StringComparer.OrdinalIgnoreCase);
            var rawDuplicates = (database.DuplicateMyTagLinks ??
                Array.Empty<DuplicateMyTagLinkEvidence>())
                .Select(x => new PreviewDetail(
                    PreviewDetailKind.Conflict,
                    x.ContentId,
                    pathByContentId.GetValueOrDefault(x.ContentId),
                    new MyTagAssignment(x.Group, x.Value),
                    x.Reason +
                    $" MyTagIDs=[{string.Join(", ", x.MyTagIds)}]; " +
                    $"djmdSongMyTag row IDs=[{string.Join(", ", x.AssignmentRowIds)}]."))
                .ToArray();
            PreviewInvestigationGrid.ItemsSource = result.scope.ExcludedTargets
                .Concat(rawDuplicates)
                .Select(InvestigationDisplayRow.FromPreview).ToArray();
            var reasonGroups = result.scope.ExcludedTargets
                .GroupBy(x => x.Message, StringComparer.Ordinal)
                .OrderByDescending(x => x.Count())
                .Select(x => $"{x.Key.Split(':')[0]} {x.Count():N0}")
                .ToArray();
            PreviewInvestigationStatusTextBlock.Text =
                $"Excluded rekordbox ContentIDs: {result.scope.ExcludedTargets.Count:N0}. " +
                (reasonGroups.Length == 0 ? "None. " : string.Join(" · ", reasonGroups) + ". ") +
                $"Existing duplicate MyTag link groups: {rawDuplicates.Length:N0} across the complete rekordbox DB, " +
                $"{rawDuplicates.Count(x => includedIds.Contains(x.ContentId)):N0} in the physically matched preview scope. " +
                "Rows show original MyTagIDs and assignment-row IDs; NO automatic deletion. " +
                "An existing duplicate tag link does not prove duplicate music tracks.";
            PreviewCountsTextBlock.Text =
                $"Add {result.preview.Counts.Additions} · Remove {result.preview.Counts.Removals} · " +
                $"Correct {result.preview.Counts.AlreadyCorrect} · " +
                $"Conflicts {result.preview.Counts.Conflicts} · " +
                $"Unmatched target {result.scope.ExcludedTargets.Count}";
            PreviewRunStatusTextBlock.Text =
                $"Read-only verified MyTag preview: {result.scope.Targets.Count:N0} " +
                $"file-level rekordbox ContentIDs confirmed as the same physical files " +
                (result.reuse ? "(reused read-only same-session evidence; no file-ID rescan); " :
                    "(fresh read-only file-ID scan); ") +
                $"{result.scope.ExcludedTargets.Count:N0} rekordbox tracks excluded and listed below; " +
                $"{result.evidence.ExcludedSubsongs:N0} virtual subsongs remain excluded; " +
                $"{result.evidence.RecoveredMixedSubsongPaths:N0} files recovered from mixed " +
                "physical/virtual source path groups with native file-ID proof; " +
                $"{result.evidence.AmbiguousSourcePaths:N0} multi-entry source path groups total, " +
                $"{result.evidence.AmbiguousTargetPaths:N0} duplicate target path groups. " +
                (settings.EffectiveMappings.Count == 0
                    ? "No MyTag mappings configured yet; no tag changes proposed. "
                    : $"{settings.EffectiveMappings.Count} mapping rule(s) evaluated. ") +
                "Root mapping is TEMPORARY and read-only; Apply and Restore stay LOCKED.";
            // Intentionally never assign _approvedPreview. A display-only
            // report is not an authorization to mutate a real database.
            SetOperationStatus("Verified MyTag preview completed — no data or settings changed.");
            AppendDiagnostic(PreviewRunStatusTextBlock.Text);
        }
        catch (OperationCanceledException)
        {
            InvalidatePreview("Verified read-only preview canceled.");
            PreviewRunStatusTextBlock.Text = "Verified preview canceled — no partial results accepted.";
            SetOperationStatus(PreviewRunStatusTextBlock.Text);
            AppendDiagnostic(PreviewRunStatusTextBlock.Text);
        }
        catch (Exception ex)
        {
            InvalidatePreview("Verified read-only preview failed.");
            PreviewRunStatusTextBlock.Text = $"Verified preview blocked: {ex.Message}";
            SetOperationStatus(PreviewRunStatusTextBlock.Text, error: true);
            AppendDiagnostic(PreviewRunStatusTextBlock.Text);
        }
        finally
        {
            _identityCancellation = null;
            OperationProgressBar.Visibility = Visibility.Collapsed;
            UpdateWorkflowGate();
        }
    }
}
