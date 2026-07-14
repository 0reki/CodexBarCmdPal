using System.Diagnostics;
using System.Text.Json;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CodexToys.Tests;

[TestClass]
public sealed class CodexToysStatusClientTests
{
    [TestMethod]
    public void RequestRefreshReturnsImmediatelyAndRefreshesNeverOverlap()
    {
        var scanner = new BlockingScanner();
        using var client = new CodexToysStatusClient(scanner, new NullLimitsSource());
        var stopwatch = Stopwatch.StartNew();

        client.RequestRefresh();
        stopwatch.Stop();
        Assert.IsTrue(scanner.Started.Wait(TimeSpan.FromSeconds(5)));
        for (var index = 0; index < 20; index++)
        {
            client.RequestRefresh();
        }

        scanner.Release.Set();
        Assert.IsTrue(SpinWait.SpinUntil(
            () => client.CurrentState.Snapshot is not null && !client.CurrentState.IsRefreshing,
            TimeSpan.FromSeconds(5)));
        Assert.IsTrue(stopwatch.ElapsedMilliseconds < 100);
        Assert.AreEqual(1, scanner.MaxActiveCalls);
        Assert.IsTrue(scanner.CallCount <= 2);
    }

    [TestMethod]
    public void PageReturnsLoadingContentWhileAcquisitionIsBlocked()
    {
        var scanner = new BlockingScanner();
        using var client = new CodexToysStatusClient(scanner, new NullLimitsSource());
        var page = new CodexToysStatusPage(client);
        var stopwatch = Stopwatch.StartNew();

        var content = page.GetContent();
        stopwatch.Stop();

        Assert.HasCount(1, content);
        Assert.IsTrue(stopwatch.ElapsedMilliseconds < 100);
        var form = (FormContent)content[0];
        Assert.IsFalse(form.TemplateJson.Contains("$when", StringComparison.Ordinal));
        using var data = JsonDocument.Parse(form.DataJson);
        Assert.AreEqual("Loading Codex usage...", data.RootElement.GetProperty("subtitle").GetString());
        Assert.IsFalse(data.RootElement.TryGetProperty("statusMessage", out _));
        Assert.IsTrue(scanner.Started.Wait(TimeSpan.FromSeconds(5)));
        scanner.Release.Set();
    }

    [TestMethod]
    public void DetailSubtitlePreservesSubscriptionAndAppendsUpdateTime()
    {
        var scanner = new BlockingScanner();
        using var client = new CodexToysStatusClient(scanner, new NullLimitsSource());
        var page = new CodexToysStatusPage(client);
        page.UpdateState(new CodexToysRefreshState(
            new CodexToysStatusSnapshot
            {
                UpdatedAt = "2026-07-14T11:46:00+08:00",
                Providers =
                [
                    new CodexToysProviderSnapshot
                    {
                        Id = "codex",
                        Name = "Codex",
                        Subtitle = "ChatGPT Plus",
                        UpdatedAt = "2026-07-14T11:46:00+08:00",
                    },
                ],
            },
            false,
            null,
            1));

        var form = (FormContent)page.GetContent()[0];
        using var data = JsonDocument.Parse(form.DataJson);
        var subtitle = data.RootElement.GetProperty("subtitle").GetString();
        StringAssert.StartsWith(subtitle, "ChatGPT Plus - Updated ");
        scanner.Release.Set();
    }

    [TestMethod]
    public void FailedRefreshPreservesLastGoodSnapshot()
    {
        var scanner = new OneSuccessThenFailureScanner();
        using var client = new CodexToysStatusClient(scanner, new NullLimitsSource());
        client.RequestRefresh();
        Assert.IsTrue(SpinWait.SpinUntil(
            () => client.CurrentState.Snapshot is not null && !client.CurrentState.IsRefreshing,
            TimeSpan.FromSeconds(5)));
        var good = client.CurrentState.Snapshot;

        client.RequestRefresh();
        Assert.IsTrue(SpinWait.SpinUntil(
            () => client.CurrentState.RefreshError is not null,
            TimeSpan.FromSeconds(5)));

        Assert.AreSame(good, client.CurrentState.Snapshot);
    }

    [TestMethod]
    public void RemoteFailureKeepsFreshLocalSnapshotAndPublishesDetailError()
    {
        var scanner = new OneSuccessThenFailureScanner();
        using var client = new CodexToysStatusClient(
            scanner,
            new FailedLimitsSource());

        client.RequestRefresh();
        Assert.IsTrue(SpinWait.SpinUntil(
            () => client.CurrentState.RefreshError is not null,
            TimeSpan.FromSeconds(5)));

        Assert.IsNotNull(client.CurrentState.Snapshot);
        Assert.AreEqual("remote test failure", client.CurrentState.RefreshError);
    }

    [TestMethod]
    public void LocalFailureDoesNotAllowNextPipelineToOverlapOutstandingRemoteTask()
    {
        var scanner = new AlwaysFailScanner();
        var limits = new GatedLimitsSource();
        using var client = new CodexToysStatusClient(scanner, limits);
        client.RequestRefresh();
        Assert.IsTrue(SpinWait.SpinUntil(
            () => client.CurrentState.RefreshError is not null,
            TimeSpan.FromSeconds(5)));

        client.RequestRefresh();
        Assert.IsFalse(scanner.SecondCallStarted.Wait(TimeSpan.FromMilliseconds(100)));
        Assert.AreEqual(1, scanner.CallCount);

        limits.Release.TrySetResult(new CodexUsageLimitsFetchResult(null, null));
        Assert.IsTrue(scanner.SecondCallStarted.Wait(TimeSpan.FromSeconds(5)));
    }

    private sealed class BlockingScanner : ICodexUsageScanner
    {
        private int _activeCalls;
        private int _callCount;
        private int _maxActiveCalls;

        public ManualResetEventSlim Started { get; } = new(false);

        public ManualResetEventSlim Release { get; } = new(false);

        public int CallCount => Volatile.Read(ref _callCount);

        public int MaxActiveCalls => Volatile.Read(ref _maxActiveCalls);

        public void InvalidateRoots()
        {
        }

        public CodexToysStatusSnapshot ReadSnapshot(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            var active = Interlocked.Increment(ref _activeCalls);
            InterlockedExtensions.Max(ref _maxActiveCalls, active);
            Started.Set();
            try
            {
                Release.Wait(cancellationToken);
                return new CodexToysStatusSnapshot
                {
                    Version = 2,
                    UpdatedAt = DateTimeOffset.Now.ToString("O"),
                    Providers = [new CodexToysProviderSnapshot { Id = "codex", Name = "Codex" }],
                };
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }
    }

    private sealed class NullLimitsSource : ICodexUsageLimitsSource
    {
        public Task<CodexUsageLimitsFetchResult> FetchAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new CodexUsageLimitsFetchResult(null, null));
        }
    }

    private sealed class FailedLimitsSource : ICodexUsageLimitsSource
    {
        public Task<CodexUsageLimitsFetchResult> FetchAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new CodexUsageLimitsFetchResult(null, "remote test failure"));
        }
    }

    private sealed class OneSuccessThenFailureScanner : ICodexUsageScanner
    {
        private int _calls;

        public void InvalidateRoots()
        {
        }

        public CodexToysStatusSnapshot ReadSnapshot(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) > 1)
            {
                throw new IOException("test failure");
            }

            return new CodexToysStatusSnapshot
            {
                Version = 2,
                UpdatedAt = DateTimeOffset.Now.ToString("O"),
                Providers = [new CodexToysProviderSnapshot { Id = "codex", Name = "Codex" }],
            };
        }
    }

    private sealed class AlwaysFailScanner : ICodexUsageScanner
    {
        private int _calls;

        public int CallCount => Volatile.Read(ref _calls);

        public ManualResetEventSlim SecondCallStarted { get; } = new(false);

        public void InvalidateRoots()
        {
        }

        public CodexToysStatusSnapshot ReadSnapshot(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 2)
            {
                SecondCallStarted.Set();
            }

            throw new IOException("local test failure");
        }
    }

    private sealed class GatedLimitsSource : ICodexUsageLimitsSource
    {
        public TaskCompletionSource<CodexUsageLimitsFetchResult> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CodexUsageLimitsFetchResult> FetchAsync(CancellationToken cancellationToken)
        {
            return Release.Task;
        }
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int location, int candidate)
        {
            var current = Volatile.Read(ref location);
            while (candidate > current)
            {
                var observed = Interlocked.CompareExchange(ref location, candidate, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }
}
