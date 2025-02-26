﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Exceptionless;
using Foundatio.Caching;
using Foundatio.Jobs;
using Foundatio.Lock;
using Foundatio.Metrics;
using Foundatio.Queues;
using Foundatio.Utility;
using Foundatio.Xunit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Jobs;

public abstract class JobQueueTestsBase : TestWithLoggingBase
{
    private readonly ActivitySource _activitySource = new(nameof(JobQueueTestsBase));

    public JobQueueTestsBase(ITestOutputHelper output) : base(output) { }

    protected abstract IQueue<SampleQueueWorkItem> GetSampleWorkItemQueue(int retries, TimeSpan retryDelay);

    public virtual async Task ActivityWillFlowThroughQueueJobAsync()
    {
        using var queue = GetSampleWorkItemQueue(retries: 0, retryDelay: TimeSpan.Zero);
        await queue.DeleteQueueAsync();

        Activity parentActivity = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == nameof(JobQueueTestsBase) || s.Name == "Foundatio",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = a =>
            {
                if (a.OperationName != "ProcessQueueEntry")
                    return;

                Assert.Equal(parentActivity.RootId, a.RootId);
                Assert.Equal(parentActivity.SpanId, a.ParentSpanId);
            },
            ActivityStopped = a => { }
        };
        ActivitySource.AddActivityListener(listener);

        parentActivity = _activitySource.StartActivity("Parent");
        Assert.NotNull(parentActivity);

        var enqueueTask = await queue.EnqueueAsync(new SampleQueueWorkItem
        {
            Created = SystemClock.UtcNow,
            Path = "somepath"
        });

        // clear activity and then verify that      Activity.Current = null;

        var job = new SampleQueueJob(queue, null, Log);
        await job.RunAsync();

        var stats = await queue.GetQueueStatsAsync();
        Assert.Equal(0, stats.Queued);
        Assert.Equal(1, stats.Enqueued);
        Assert.Equal(1, stats.Dequeued);
    }

    public virtual async Task CanRunQueueJobAsync()
    {
        const int workItemCount = 100;
        using var queue = GetSampleWorkItemQueue(retries: 0, retryDelay: TimeSpan.Zero);
        await queue.DeleteQueueAsync();

        var enqueueTask = Run.InParallelAsync(workItemCount, index => queue.EnqueueAsync(new SampleQueueWorkItem
        {
            Created = SystemClock.UtcNow,
            Path = "somepath" + index
        }));

        var job = new SampleQueueJob(queue, null, Log);
        await SystemClock.SleepAsync(10);
        await Task.WhenAll(job.RunUntilEmptyAsync(), enqueueTask);

        var stats = await queue.GetQueueStatsAsync();
        Assert.Equal(0, stats.Queued);
        Assert.Equal(workItemCount, stats.Enqueued);
        Assert.Equal(workItemCount, stats.Dequeued);
    }

    public virtual async Task CanRunQueueJobWithLockFailAsync()
    {
        const int workItemCount = 10;
        const int allowedLockCount = 5;
        Log.SetLogLevel<ThrottlingLockProvider>(LogLevel.Trace);

        using var queue = GetSampleWorkItemQueue(retries: 3, retryDelay: TimeSpan.Zero);
        await queue.DeleteQueueAsync();

        var enqueueTask = Run.InParallelAsync(workItemCount, index =>
        {
            _logger.LogInformation($"Enqueue #{index}");
            return queue.EnqueueAsync(new SampleQueueWorkItem
            {
                Created = SystemClock.UtcNow,
                Path = "somepath" + index
            });
        });

        var lockProvider = new ThrottlingLockProvider(new InMemoryCacheClient(new InMemoryCacheClientOptions()), allowedLockCount, TimeSpan.FromDays(1), Log);
        var job = new SampleQueueJobWithLocking(queue, null, lockProvider, Log);
        await SystemClock.SleepAsync(10);
        _logger.LogInformation("Starting RunUntilEmptyAsync");
        await Task.WhenAll(job.RunUntilEmptyAsync(), enqueueTask);
        _logger.LogInformation("Done RunUntilEmptyAsync");

        var stats = await queue.GetQueueStatsAsync();
        Assert.Equal(0, stats.Queued);
        Assert.Equal(workItemCount, stats.Enqueued);
        Assert.Equal(allowedLockCount, stats.Completed);
        Assert.Equal(allowedLockCount * 4, stats.Abandoned);
        Assert.Equal(allowedLockCount, stats.Deadletter);
    }

    public virtual async Task CanRunMultipleQueueJobsAsync()
    {
        const int jobCount = 5;
        const int workItemCount = 100;

        Log.SetLogLevel<SampleQueueWithRandomErrorsAndAbandonsJob>(LogLevel.Information);
        Log.SetLogLevel<InMemoryMetricsClient>(LogLevel.None);

        using var metrics = new InMemoryMetricsClient(new InMemoryMetricsClientOptions { LoggerFactory = Log, Buffered = true });
        var queues = new List<IQueue<SampleQueueWorkItem>>();
        try
        {
            for (int i = 0; i < jobCount; i++)
            {
                var q = GetSampleWorkItemQueue(retries: 1, retryDelay: TimeSpan.Zero);
                await q.DeleteQueueAsync();
#pragma warning disable CS0618 // Type or member is obsolete
                q.AttachBehavior(new MetricsQueueBehavior<SampleQueueWorkItem>(metrics, "test", loggerFactory: Log));
#pragma warning restore CS0618 // Type or member is obsolete
                queues.Add(q);
            }
            _logger.LogInformation("Done setting up queues");

            var enqueueTask = Run.InParallelAsync(workItemCount, index =>
            {
                var queue = queues[RandomData.GetInt(0, jobCount - 1)];
                return queue.EnqueueAsync(new SampleQueueWorkItem
                {
                    Created = SystemClock.UtcNow,
                    Path = RandomData.GetString()
                });
            });
            _logger.LogInformation("Done enqueueing");

            var cancellationTokenSource = new CancellationTokenSource();
            await Run.InParallelAsync(jobCount, async index =>
            {
                var queue = queues[index - 1];
                var job = new SampleQueueWithRandomErrorsAndAbandonsJob(queue, metrics, Log);
                await job.RunUntilEmptyAsync(cancellationTokenSource.Token);
                await cancellationTokenSource.CancelAsync();
            });
            _logger.LogInformation("Done running jobs until empty");

            await enqueueTask;

            var queueStats = new List<QueueStats>();
            for (int i = 0; i < queues.Count; i++)
            {
                var stats = await queues[i].GetQueueStatsAsync();
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation("Queue#{Id}: Working: {Working} Completed: {Completed} Abandoned: {Abandoned} Error: {Errors} Deadletter: {Deadletter}", i, stats.Working, stats.Completed, stats.Abandoned, stats.Errors, stats.Deadletter);
                queueStats.Add(stats);
            }
            _logger.LogInformation("Done getting queue stats");

            await metrics.FlushAsync();
            _logger.LogInformation("Done flushing metrics");

            var queueSummary = await metrics.GetQueueStatsAsync("test.samplequeueworkitem");
            Assert.Equal(queueStats.Sum(s => s.Completed), queueSummary.Completed.Count);
            Assert.InRange(queueStats.Sum(s => s.Completed), 0, workItemCount);
        }
        finally
        {
            foreach (var q in queues)
            {
                await q.DeleteQueueAsync();
                q.Dispose();
            }
        }
    }
}
