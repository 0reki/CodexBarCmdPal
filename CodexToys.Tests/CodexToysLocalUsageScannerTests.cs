using System.Text;
using System.Text.Json;
using System.Globalization;

namespace CodexToys.Tests;

[TestClass]
public sealed class CodexToysLocalUsageScannerTests
{
    [TestMethod]
    public void UnchangedScanDoesNotOpenOrReadSessionFiles()
    {
        using var fixture = new SessionFixture();
        fixture.Write(TokenLine(100, 20));
        var scanner = fixture.CreateScanner();

        var first = scanner.ReadSnapshot(CancellationToken.None);
        var second = scanner.ReadSnapshot(CancellationToken.None);

        Assert.AreEqual<ulong?>(120, first.Providers[0].ThirtyDayTokens);
        Assert.AreEqual<ulong?>(120, second.Providers[0].ThirtyDayTokens);
        Assert.AreEqual(0, scanner.LastDiagnostics!.OpenedFiles);
        Assert.AreEqual(1, scanner.LastDiagnostics.ReusedFiles);
        Assert.AreEqual(0L, scanner.LastDiagnostics.BytesRead);
    }

    [TestMethod]
    public void AppendReadsOnlyNewBytesAndCountsEventOnce()
    {
        using var fixture = new SessionFixture();
        fixture.Write(TokenLine(100, 20));
        var scanner = fixture.CreateScanner();
        scanner.ReadSnapshot(CancellationToken.None);

        var appended = TokenLine(40, 5);
        fixture.Append(appended);
        var afterAppend = scanner.ReadSnapshot(CancellationToken.None);
        var appendDiagnostics = scanner.LastDiagnostics!;
        var unchanged = scanner.ReadSnapshot(CancellationToken.None);

        Assert.AreEqual<ulong?>(165, afterAppend.Providers[0].ThirtyDayTokens);
        Assert.AreEqual<ulong?>(165, unchanged.Providers[0].ThirtyDayTokens);
        Assert.AreEqual(1, appendDiagnostics.OpenedFiles);
        Assert.IsTrue(appendDiagnostics.BytesRead >= fixture.LastAppendByteCount);
        Assert.IsTrue(appendDiagnostics.BytesRead <= fixture.LastAppendByteCount + 512);
        Assert.AreEqual(0, scanner.LastDiagnostics!.OpenedFiles);
        Assert.AreEqual(0L, scanner.LastDiagnostics.BytesRead);
    }

    [TestMethod]
    public void PartialTrailingLineIsCommittedOnlyAfterNewlineArrives()
    {
        using var fixture = new SessionFixture();
        fixture.Write(TokenLine(100, 20));
        var scanner = fixture.CreateScanner();
        scanner.ReadSnapshot(CancellationToken.None);

        var partial = TokenLine(30, 3).TrimEnd('\r', '\n');
        fixture.Append(partial);
        var beforeNewline = scanner.ReadSnapshot(CancellationToken.None);
        fixture.Append(Environment.NewLine);
        var afterNewline = scanner.ReadSnapshot(CancellationToken.None);

        Assert.AreEqual<ulong?>(120, beforeNewline.Providers[0].ThirtyDayTokens);
        Assert.AreEqual<ulong?>(153, afterNewline.Providers[0].ThirtyDayTokens);
    }

    [TestMethod]
    public void TruncationRebuildsFileContribution()
    {
        using var fixture = new SessionFixture();
        fixture.Write(TokenLine(100, 20) + TokenLine(40, 5));
        var scanner = fixture.CreateScanner();
        var before = scanner.ReadSnapshot(CancellationToken.None);

        fixture.Write(TokenLine(7, 2));
        var after = scanner.ReadSnapshot(CancellationToken.None);

        Assert.AreEqual<ulong?>(165, before.Providers[0].ThirtyDayTokens);
        Assert.AreEqual<ulong?>(9, after.Providers[0].ThirtyDayTokens);
        Assert.AreEqual(1, scanner.LastDiagnostics!.OpenedFiles);
    }

    [TestMethod]
    public void CorruptCacheFallsBackToFullRebuild()
    {
        using var fixture = new SessionFixture();
        fixture.Write(TokenLine(12, 4));
        fixture.CreateScanner().ReadSnapshot(CancellationToken.None);
        File.WriteAllText(fixture.CachePath, "not-json");

        var scanner = fixture.CreateScanner();
        var snapshot = scanner.ReadSnapshot(CancellationToken.None);

        Assert.AreEqual<ulong?>(16, snapshot.Providers[0].ThirtyDayTokens);
        Assert.AreEqual(1, scanner.LastDiagnostics!.OpenedFiles);
    }

    [TestMethod]
    public void UnsupportedCacheVersionFallsBackToFullRebuild()
    {
        using var fixture = new SessionFixture();
        fixture.Write(TokenLine(12, 4));
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.CachePath)!);
        File.WriteAllText(
            fixture.CachePath,
            "{\"version\":999,\"scannerSemanticsVersion\":1,\"signature\":\"old\",\"files\":{}}");

        var scanner = fixture.CreateScanner();
        var snapshot = scanner.ReadSnapshot(CancellationToken.None);

        Assert.AreEqual<ulong?>(16, snapshot.Providers[0].ThirtyDayTokens);
        Assert.AreEqual(1, scanner.LastDiagnostics!.OpenedFiles);
    }

    [TestMethod]
    public void CacheWriteFailureDoesNotFailCompletedScanOrLeaveTemporaryFile()
    {
        using var fixture = new SessionFixture();
        fixture.Write(TokenLine(12, 4));
        var scanner = new CodexToysLocalUsageScanner([fixture.Root], 30, fixture.Root);

        var snapshot = scanner.ReadSnapshot(CancellationToken.None);

        Assert.AreEqual<ulong?>(16, snapshot.Providers[0].ThirtyDayTokens);
        Assert.IsEmpty(Directory.GetFiles(
            Path.GetDirectoryName(fixture.Root)!,
            $"{Path.GetFileName(fixture.Root)}.*.tmp"));
    }

    [TestMethod]
    public void CompleteMalformedLineIsSkippedWithoutBlockingLaterRecords()
    {
        using var fixture = new SessionFixture();
        fixture.Write("{not-json token_count}" + Environment.NewLine + TokenLine(12, 4));

        var snapshot = fixture.CreateScanner().ReadSnapshot(CancellationToken.None);

        Assert.AreEqual<ulong?>(16, snapshot.Providers[0].ThirtyDayTokens);
    }

    [TestMethod]
    public void DeletingAndRenamingFilesReconcilesContributions()
    {
        using var fixture = new SessionFixture();
        fixture.Write(TokenLine(12, 4));
        var scanner = fixture.CreateScanner();
        scanner.ReadSnapshot(CancellationToken.None);

        var renamed = Path.Combine(Path.GetDirectoryName(fixture.SessionPath)!, "renamed.jsonl");
        File.Move(fixture.SessionPath, renamed);
        var afterRename = scanner.ReadSnapshot(CancellationToken.None);
        File.Delete(renamed);
        var afterDelete = scanner.ReadSnapshot(CancellationToken.None);

        Assert.AreEqual<ulong?>(16, afterRename.Providers[0].ThirtyDayTokens);
        Assert.IsNull(afterDelete.Providers[0].ThirtyDayTokens);
    }

    [TestMethod]
    public void DayWindowRolloverPrunesExpiredContributionsWithoutOpeningFile()
    {
        var clock = DateTime.Today;
        using var fixture = new SessionFixture(clock.AddDays(-1));
        fixture.Write(TokenLine(12, 4, clock.AddDays(-1)));
        var scanner = fixture.CreateScanner(2, () => clock);
        var before = scanner.ReadSnapshot(CancellationToken.None);

        clock = clock.AddDays(1);
        var after = scanner.ReadSnapshot(CancellationToken.None);

        Assert.AreEqual<ulong?>(16, before.Providers[0].ThirtyDayTokens);
        Assert.IsNull(after.Providers[0].ThirtyDayTokens);
        Assert.AreEqual(0, scanner.LastDiagnostics!.OpenedFiles);
        Assert.AreEqual(1, scanner.LastDiagnostics.ReusedFiles);
    }

    [TestMethod]
    public void AppendPreservesModelAndCumulativeTokenState()
    {
        using var fixture = new SessionFixture();
        fixture.Write(TurnContextLine("gpt-5-codex") + TotalTokenLine(100, 10));
        var scanner = fixture.CreateScanner();
        scanner.ReadSnapshot(CancellationToken.None);

        fixture.Append(TotalTokenLine(150, 20));
        var after = scanner.ReadSnapshot(CancellationToken.None);

        Assert.AreEqual<ulong?>(170, after.Providers[0].ThirtyDayTokens);
        Assert.AreEqual("gpt-5-codex", after.Providers[0].TopModel);
    }

    [TestMethod]
    public void FingerprintMismatchRebuildsGrowingReplacement()
    {
        using var fixture = new SessionFixture();
        fixture.Write(TokenLine(100, 20));
        var scanner = fixture.CreateScanner();
        scanner.ReadSnapshot(CancellationToken.None);

        fixture.Write(TokenLine(7, 2) + TokenLine(3, 1));
        var after = scanner.ReadSnapshot(CancellationToken.None);

        Assert.AreEqual<ulong?>(13, after.Providers[0].ThirtyDayTokens);
        Assert.AreEqual(1, scanner.LastDiagnostics!.OpenedFiles);
    }

    private static string TokenLine(ulong input, ulong output, DateTime? timestamp = null)
    {
        var timestampValue = (timestamp is DateTime value
            ? new DateTimeOffset(value, TimeZoneInfo.Local.GetUtcOffset(value))
            : DateTimeOffset.Now).ToString("O", CultureInfo.InvariantCulture);
        return JsonSerializer.Serialize(new
        {
            timestamp = timestampValue,
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                info = new
                {
                    last_token_usage = new
                    {
                        input_tokens = input,
                        cached_input_tokens = 0,
                        output_tokens = output,
                    },
                },
            },
        }) + Environment.NewLine;
    }

    private static string TotalTokenLine(ulong input, ulong output)
    {
        var timestamp = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
        return JsonSerializer.Serialize(new
        {
            timestamp,
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                info = new
                {
                    total_token_usage = new
                    {
                        input_tokens = input,
                        cached_input_tokens = 0,
                        output_tokens = output,
                    },
                },
            },
        }) + Environment.NewLine;
    }

    private static string TurnContextLine(string model)
    {
        return JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            type = "turn_context",
            payload = new { model },
        }) + Environment.NewLine;
    }

    private sealed class SessionFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "CodexToys.Tests",
            Guid.NewGuid().ToString("N"));

        public SessionFixture(DateTime? sessionDate = null)
        {
            var today = sessionDate?.Date ?? DateTime.Today;
            Root = Path.Combine(_directory, "sessions");
            var day = Path.Combine(
                Root,
                today.ToString("yyyy", CultureInfo.InvariantCulture),
                today.ToString("MM", CultureInfo.InvariantCulture),
                today.ToString("dd", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(day);
            SessionPath = Path.Combine(day, "rollout-test.jsonl");
            CachePath = Path.Combine(_directory, "cache", "usage.json");
        }

        public string Root { get; }

        public string SessionPath { get; }

        public string CachePath { get; }

        public int LastAppendByteCount { get; private set; }

        public CodexToysLocalUsageScanner CreateScanner(
            int scanDays = 30,
            Func<DateTime>? todayProvider = null) => new([Root], scanDays, CachePath, todayProvider);

        public void Write(string value)
        {
            File.WriteAllText(SessionPath, value, new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(SessionPath, DateTime.UtcNow.AddSeconds(1));
        }

        public void Append(string value)
        {
            LastAppendByteCount = Encoding.UTF8.GetByteCount(value);
            File.AppendAllText(SessionPath, value, new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(SessionPath, DateTime.UtcNow.AddSeconds(1));
        }

        public void Dispose()
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
