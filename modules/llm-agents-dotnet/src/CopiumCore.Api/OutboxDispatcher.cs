namespace CopiumCore.Api;

public interface IEventSink
{
    Task PublishAsync(OutboxEvent eventItem, CancellationToken cancellationToken);
}

public sealed class LoggingEventSink(ILogger<LoggingEventSink> logger) : IEventSink
{
    public Task PublishAsync(OutboxEvent eventItem, CancellationToken cancellationToken)
    {
        logger.LogInformation("Published outbox event {EventId} of type {Type} for {AggregateId}",
            eventItem.EventId, eventItem.Type, eventItem.AggregateId);
        return Task.CompletedTask;
    }
}

public sealed class OutboxDispatcher(
    IReviewStore store,
    IEventSink sink,
    ILogger<OutboxDispatcher> logger,
    IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lifetime.ApplicationStopping);
        while (!linked.IsCancellationRequested)
        {
            try
            {
                var pending = await store.PendingEventsAsync(linked.Token);
                foreach (var eventItem in pending)
                {
                    try
                    {
                        await sink.PublishAsync(eventItem, linked.Token);
                        await store.MarkPublishedAsync(eventItem.EventId, linked.Token);
                    }
                    catch (Exception ex) when (!linked.IsCancellationRequested)
                    {
                        logger.LogWarning(ex, "Outbox delivery failed for {EventId}; it will be retried", eventItem.EventId);
                    }
                }
                await Task.Delay(TimeSpan.FromMilliseconds(250), linked.Token);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Outbox polling failed; retrying");
                await Task.Delay(TimeSpan.FromSeconds(1), linked.Token);
            }
        }
    }
}
