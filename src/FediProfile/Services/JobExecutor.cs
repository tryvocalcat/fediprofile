using FediProfile.Services;

namespace FediProfile.Services;

/// <summary>
/// Background service that periodically polls the job queue and processes
/// pending ActivityPub activities (Follow, Undo-Follow, Create fan-out).
/// </summary>
public sealed class JobExecutor(
    IServiceScopeFactory serviceScopeFactory,
    ILogger<JobExecutor> logger) : BackgroundService
{
    private const string ClassName = nameof(JobExecutor);

    /// <summary>Delay after processing at least one job.</summary>
    private const int DelayWithJobs = 30_000;   // 30 seconds

    /// <summary>Delay when the queue was empty.</summary>
    private const int DelayNoJobs = 120_000;    // 2 minutes

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("{Name} is running (delays: {WithJobs}ms / {NoJobs}ms)",
            ClassName, DelayWithJobs, DelayNoJobs);

        // Initial delay to let the app start up
        await Task.Delay(DelayNoJobs, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            int jobsProcessed = 0;

            using IServiceScope scope = serviceScopeFactory.CreateScope();

            var processor = scope.ServiceProvider.GetRequiredService<JobProcessor>();

            try
            {
                jobsProcessed = await processor.DoWorkAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error occurred executing {Name}", ClassName);
            }

            await Task.Delay(jobsProcessed > 0 ? DelayWithJobs : DelayNoJobs, stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("{Name} is stopping.", ClassName);
        await base.StopAsync(stoppingToken);
    }
}
