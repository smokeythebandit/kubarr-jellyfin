using Jellyfin.Plugin.Kubarr.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kubarr.Services;

/// <summary>
/// Pulls Kubarr's user list on start and every few minutes. Kubarr also
/// pushes changes as they happen; this catches any push that was missed
/// while Jellyfin was down.
/// </summary>
public class SyncPollService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly KubarrClient _kubarr;
    private readonly UserLinker _linker;
    private readonly ILogger<SyncPollService> _logger;

    public SyncPollService(KubarrClient kubarr, UserLinker linker, ILogger<SyncPollService> logger)
    {
        _kubarr = kubarr;
        _linker = linker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_kubarr.IsConfigured)
            {
                try
                {
                    var snapshot = await _kubarr.GetSnapshotAsync(stoppingToken).ConfigureAwait(false);
                    if (snapshot is not null)
                    {
                        await _linker.ApplySnapshotAsync(snapshot).ConfigureAwait(false);
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _logger.LogWarning(e, "Kubarr user sync failed; retrying later");
                }
            }

            await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
        }
    }
}
