using LateFeeBox.Web.Data;
using LateFeeBox.Web.Options;
using Microsoft.Extensions.Options;

namespace LateFeeBox.Web.Services;

public sealed class BaleLongPollingWorker(
    JsonStore store,
    BaleApiClient bale,
    BotUpdateHandler handler,
    IOptions<BaleOptions> options,
    ILogger<BaleLongPollingWorker> logger) : BackgroundService
{
    private readonly BaleOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.UseLongPolling)
        {
            logger.LogInformation("Bale Long Polling is disabled.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.BotToken))
        {
            logger.LogWarning("Bale Long Polling did not start because Bale:BotToken is empty.");
            return;
        }

        await DeleteWebhookBestEffortAsync(stoppingToken);
        logger.LogInformation("Bale Long Polling started with timeout {TimeoutSeconds} seconds.", _options.LongPollingTimeoutSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var lastId = await store.ReadAsync(state => state.LastProcessedUpdateId, stoppingToken);
                var updates = await bale.GetUpdatesAsync(lastId + 1, Math.Clamp(_options.LongPollingTimeoutSeconds, 1, 40), stoppingToken);
                var retryFinancialUpdate = false;
                foreach (var update in updates.OrderBy(x => x.UpdateId))
                {
                    try
                    {
                        await handler.HandleAsync(update, stoppingToken);
                        await MarkProcessedAsync(update.UpdateId, stoppingToken);
                    }
                    catch (Exception ex) when (IsFinancialUpdate(update))
                    {
                        logger.LogError(
                            ex,
                            "Financial Bale update {UpdateId} failed and will be retried without advancing the offset.",
                            update.UpdateId);
                        retryFinancialUpdate = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(
                            ex,
                            "Failed to process non-financial Bale update {UpdateId}; it is skipped to keep the bot running.",
                            update.UpdateId);
                        await MarkProcessedAsync(update.UpdateId, stoppingToken);
                    }
                }

                if (retryFinancialUpdate)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (TaskCanceledException)
            {
                // A network timeout is normal for long polling. Start the next poll immediately.
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Bale Long Polling request failed. Retrying in 5 seconds.");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }


    private Task MarkProcessedAsync(long updateId, CancellationToken cancellationToken)
        => store.WriteAsync(
            state => state.LastProcessedUpdateId = Math.Max(state.LastProcessedUpdateId, updateId),
            cancellationToken);

    private static bool IsFinancialUpdate(Models.BaleUpdate update)
        => update.PreCheckoutQuery is not null ||
           update.Message?.SuccessfulPayment is not null ||
           update.EditedMessage?.SuccessfulPayment is not null;

    private async Task DeleteWebhookBestEffortAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 3 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await bale.DeleteWebhookAsync(cancellationToken);
                logger.LogInformation("Any existing Bale webhook was deleted before Long Polling started.");
                return;
            }
            catch (Exception ex) when (attempt < 3)
            {
                logger.LogWarning(ex, "Could not delete the Bale webhook (attempt {Attempt}/3).", attempt);
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not confirm webhook deletion. Long Polling will still start.");
            }
        }
    }
}
