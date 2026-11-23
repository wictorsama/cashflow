using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CashFlow.Entries.Infrastructure.Messaging;

/// <summary>Background loop that drains the outbox. It backs off when the broker or database is unavailable.</summary>
public sealed partial class OutboxRelay(OutboxProcessor processor, ILogger<OutboxRelay> logger) : BackgroundService
{
    private const int BatchSize = 100;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GaugeRefresh = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastGauge = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var published = await processor.ProcessBatchAsync(BatchSize, stoppingToken);

                if (DateTimeOffset.UtcNow - lastGauge >= GaugeRefresh)
                {
                    EntriesTelemetry.SetOutboxPending(await processor.CountPendingAsync(stoppingToken));
                    lastGauge = DateTimeOffset.UtcNow;
                }

                // A full batch means there is probably more waiting: go again without sleeping.
                if (published < BatchSize)
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogRelayFailed(ex, ErrorDelay);
                await Task.Delay(ErrorDelay, stoppingToken);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox relay failed; retrying in {Delay}.")]
    private partial void LogRelayFailed(Exception exception, TimeSpan delay);
}
