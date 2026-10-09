using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using RekordboxMyTagSync.Core;

namespace RekordboxMyTagSync;

public partial class MainWindow
{
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
        var settingsFingerprint = JsonSerializer.Serialize(settings);
        var folder = settings.EffectiveBridgeDirectory
            ?? throw new InvalidDataException("Bridge directory is missing.");
        using var cancel = new CancellationTokenSource();
        _identityCancellation = cancel;
        InvalidatePreview("Rebuilding verified read-only preview.");
        PreviewGrid.ItemsSource = null;
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
