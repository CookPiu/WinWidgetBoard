using System.Globalization;
using System.Text;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Providers;

namespace WinWidgetBoard.UnitTests;

/// <summary>
/// The usage sources are the only thing standing between a live, half-written session file and
/// the card's numbers, so the cases pinned here are the ones that silently corrupt a total
/// rather than throw: a response counted once per content block, a running total summed instead
/// of differenced, a file re-read from the wrong offset, and a line that was still being
/// written when the tick ran.
/// </summary>
[TestClass]
public sealed class TranscriptUsageScannerTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "wwb-transcript-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "project-a"));
    }

    [TestCleanup]
    public void DeleteRoot()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // --- Claude ---------------------------------------------------------------------------

    [TestMethod(DisplayName =
        "UT-TOKUSE-001 [USE-001] One response line yields its four counters and identity")]
    public void ParsesCountersAndIdentity()
    {
        WriteSession(
            "a.jsonl",
            ClaudeLine("req_1", "msg_1", "claude-opus-5", "2026-08-28T09:00:00.000Z", 11, 22, 33, 44));

        IReadOnlyList<TranscriptUsageRecord> records = ScanClaude();

        Assert.AreEqual(1, records.Count);
        TranscriptUsageRecord record = records[0];
        Assert.AreEqual(TokenUsageContract.ClaudeVendorId, record.VendorId);
        Assert.AreEqual("req_1", record.RequestId);
        Assert.AreEqual("msg_1", record.MessageId);
        Assert.AreEqual("claude-opus-5", record.Model);
        Assert.AreEqual(11L, record.InputTokens);
        Assert.AreEqual(22L, record.OutputTokens);
        Assert.AreEqual(33L, record.CacheCreationTokens);
        Assert.AreEqual(44L, record.CacheReadTokens);
        // Cache reads are excluded from the billed figure and only appear in the total.
        Assert.AreEqual(66L, record.BilledTokens);
        Assert.AreEqual(110L, record.TotalTokens);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-002 [USE-001] A message content array is skipped, not parsed")]
    public void SkipsMessageContentWithoutFailing()
    {
        // The real transcripts carry the whole conversation in this array. It must not affect
        // the reading, and - the point of the design - must never be turned into a string.
        string line =
            """
            {"requestId":"req_1","timestamp":"2026-08-28T09:00:00.000Z","type":"assistant",
            "message":{"id":"msg_1","model":"claude-opus-5","role":"assistant",
            "content":[{"type":"thinking","thinking":"secret"},{"type":"tool_use",
            "input":{"command":"echo token"}}],
            "usage":{"input_tokens":5,"output_tokens":7,"cache_creation_input_tokens":0,
            "cache_read_input_tokens":0,"iterations":[{"input_tokens":5,"output_tokens":7}]}}}
            """.ReplaceLineEndings(string.Empty);
        WriteSession("a.jsonl", line);

        IReadOnlyList<TranscriptUsageRecord> records = ScanClaude();

        Assert.AreEqual(1, records.Count);
        Assert.AreEqual(5L, records[0].InputTokens);
        // "iterations" repeats the same counters per internal turn; summing it alongside the
        // totals would double-count every response that had one.
        Assert.AreEqual(7L, records[0].OutputTokens);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-003 [USE-001] Synthetic messages carry usage but are excluded")]
    public void ExcludesSyntheticModel()
    {
        WriteSession(
            "a.jsonl",
            ClaudeLine("req_1", "msg_1", "<synthetic>", "2026-08-28T09:00:00.000Z", 9, 9, 9, 9),
            ClaudeLine("req_2", "msg_2", "claude-opus-5", "2026-08-28T09:00:01.000Z", 1, 1, 1, 1));

        IReadOnlyList<TranscriptUsageRecord> records = ScanClaude();

        Assert.AreEqual(1, records.Count);
        Assert.AreEqual("msg_2", records[0].MessageId);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-004 [USE-001] Lines without a usage object are not records")]
    public void IgnoresNonResponseLines()
    {
        WriteSession(
            "a.jsonl",
            """{"type":"user","uuid":"u1","timestamp":"2026-08-28T09:00:00.000Z","message":{"role":"user","content":"hi"}}""",
            """{"type":"attachment","uuid":"a1"}""",
            ClaudeLine("req_1", "msg_1", "claude-opus-5", "2026-08-28T09:00:02.000Z", 1, 2, 3, 4));

        Assert.AreEqual(1, ScanClaude().Count);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-005 [USE-002] A second scan reads only what was appended")]
    public void ReadsOnlyAppendedBytes()
    {
        var source = new ClaudeTokenUsageSource(_root);
        WriteSession(
            "a.jsonl",
            ClaudeLine("req_1", "msg_1", "claude-opus-5", "2026-08-28T09:00:00.000Z", 1, 1, 0, 0));
        Assert.AreEqual(1, source.Scan(DateTimeOffset.MinValue, CancellationToken.None).Count);

        // Nothing changed, so nothing is re-read - otherwise every tick would re-parse the
        // whole history and de-duplication would be doing the reader's job for it.
        Assert.AreEqual(0, source.Scan(DateTimeOffset.MinValue, CancellationToken.None).Count);

        AppendSession(
            "a.jsonl",
            ClaudeLine("req_2", "msg_2", "claude-opus-5", "2026-08-28T09:00:01.000Z", 2, 2, 0, 0));
        IReadOnlyList<TranscriptUsageRecord> appended =
            source.Scan(DateTimeOffset.MinValue, CancellationToken.None);
        Assert.AreEqual(1, appended.Count);
        Assert.AreEqual("msg_2", appended[0].MessageId);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-006 [USE-002] A line still being written is left for the next scan")]
    public void DoesNotParseAPartiallyWrittenLine()
    {
        var source = new ClaudeTokenUsageSource(_root);
        string complete = ClaudeLine(
            "req_1", "msg_1", "claude-opus-5", "2026-08-28T09:00:00.000Z", 1, 1, 0, 0);
        string partial = ClaudeLine(
            "req_2", "msg_2", "claude-opus-5", "2026-08-28T09:00:01.000Z", 2, 2, 0, 0);

        // No trailing newline on the second line: the writer is mid-flush.
        WriteRaw("a.jsonl", complete + "\n" + partial[..40]);
        Assert.AreEqual(1, source.Scan(DateTimeOffset.MinValue, CancellationToken.None).Count);

        // Once the writer finishes the line, the whole line is read - not the tail of it.
        WriteRaw("a.jsonl", complete + "\n" + partial + "\n");
        IReadOnlyList<TranscriptUsageRecord> second =
            source.Scan(DateTimeOffset.MinValue, CancellationToken.None);
        Assert.AreEqual(1, second.Count);
        Assert.AreEqual("msg_2", second[0].MessageId);
        Assert.AreEqual(0L, source.SkippedLineCount);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-007 [USE-002] A truncated file is re-read from the beginning")]
    public void RereadsATruncatedFile()
    {
        var source = new ClaudeTokenUsageSource(_root);
        WriteSession(
            "a.jsonl",
            ClaudeLine("req_1", "msg_1", "claude-opus-5", "2026-08-28T09:00:00.000Z", 1, 1, 0, 0),
            ClaudeLine("req_2", "msg_2", "claude-opus-5", "2026-08-28T09:00:01.000Z", 1, 1, 0, 0));
        Assert.AreEqual(2, source.Scan(DateTimeOffset.MinValue, CancellationToken.None).Count);

        // Rewritten shorter: the watermark now points past the end, so resuming from it would
        // read nothing ever again.
        WriteSession(
            "a.jsonl",
            ClaudeLine("req_3", "msg_3", "claude-opus-5", "2026-08-28T10:00:00.000Z", 1, 1, 0, 0));

        IReadOnlyList<TranscriptUsageRecord> records =
            source.Scan(DateTimeOffset.MinValue, CancellationToken.None);
        Assert.AreEqual(1, records.Count);
        Assert.AreEqual("msg_3", records[0].MessageId);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-008 [USE-002] A file older than the window is skipped on a first scan")]
    public void SkipsFilesOlderThanTheWindow()
    {
        WriteSession(
            "old.jsonl",
            ClaudeLine("req_1", "msg_1", "claude-opus-5", "2020-01-01T00:00:00.000Z", 1, 1, 0, 0));
        File.SetLastWriteTimeUtc(
            SessionPath("old.jsonl"),
            new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var source = new ClaudeTokenUsageSource(_root);
        Assert.AreEqual(
            0,
            source.Scan(DateTimeOffset.UtcNow.AddHours(-26), CancellationToken.None).Count);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-009 [USE-002] A malformed line is counted and skipped, not fatal")]
    public void SkipsMalformedLines()
    {
        var source = new ClaudeTokenUsageSource(_root);
        WriteSession(
            "a.jsonl",
            "{not json at all",
            ClaudeLine("req_1", "msg_1", "claude-opus-5", "2026-08-28T09:00:00.000Z", 1, 1, 0, 0));

        Assert.AreEqual(1, source.Scan(DateTimeOffset.MinValue, CancellationToken.None).Count);
        Assert.AreEqual(1L, source.SkippedLineCount);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-010 [USE-002] A missing session root is an empty reading, not a failure")]
    public void MissingRootYieldsNoRecords()
    {
        var source = new ClaudeTokenUsageSource(Path.Combine(_root, "does-not-exist"));

        Assert.IsFalse(source.IsAvailable);
        Assert.AreEqual(0, source.Scan(DateTimeOffset.MinValue, CancellationToken.None).Count);
    }

    // --- Codex ----------------------------------------------------------------------------

    [TestMethod(DisplayName =
        "UT-TOKUSE-011 [USE-011] Codex usage is the difference between running totals")]
    public void CodexDerivesIncrementsFromRunningTotals()
    {
        // Codex re-reports the same turn across later events. Summing the per-turn figure came
        // out 1.4x the session total for input and 2.1x for output on the reference machine;
        // the running total is the authoritative number.
        WriteSession(
            "codex.jsonl",
            CodexTurnContext("gpt-5.6-sol", "2026-08-28T09:00:00.000Z"),
            CodexTokenCount("2026-08-28T09:00:01.000Z", input: 100, output: 10, cacheRead: 0),
            CodexTokenCount("2026-08-28T09:00:02.000Z", input: 100, output: 10, cacheRead: 0),
            CodexTokenCount("2026-08-28T09:00:03.000Z", input: 250, output: 40, cacheRead: 0));

        IReadOnlyList<TranscriptUsageRecord> records = ScanCodex();

        Assert.AreEqual(2, records.Count);
        Assert.AreEqual(TokenUsageContract.CodexVendorId, records[0].VendorId);
        Assert.AreEqual("gpt-5.6-sol", records[0].Model);
        Assert.AreEqual(110L, records[0].BilledTokens);
        // The repeat contributed nothing; the third event is the delta from the first.
        Assert.AreEqual(180L, records[1].BilledTokens);
        Assert.AreEqual(290L, records.Sum(record => record.BilledTokens));
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-012 [USE-011] Codex cached input is kept out of the billed figure")]
    public void CodexSeparatesCachedInput()
    {
        WriteSession(
            "codex.jsonl",
            CodexTurnContext("gpt-5.6-sol", "2026-08-28T09:00:00.000Z"),
            CodexTokenCount("2026-08-28T09:00:01.000Z", input: 1000, output: 50, cacheRead: 900));

        TranscriptUsageRecord record = ScanCodex().Single();

        // Codex reports cached input inside input_tokens; the record keeps them apart so the
        // billed figure means the same thing for both vendors.
        Assert.AreEqual(100L, record.InputTokens);
        Assert.AreEqual(900L, record.CacheReadTokens);
        Assert.AreEqual(150L, record.BilledTokens);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-013 [USE-011] A compacted session restarts the baseline, never goes negative")]
    public void CodexTreatsATotalDropAsANewBaseline()
    {
        WriteSession(
            "codex.jsonl",
            CodexTurnContext("gpt-5.6-sol", "2026-08-28T09:00:00.000Z"),
            CodexTokenCount("2026-08-28T09:00:01.000Z", input: 5000, output: 500, cacheRead: 0),
            // The vendor compacted the session: the running total drops.
            CodexTokenCount("2026-08-28T09:00:02.000Z", input: 200, output: 20, cacheRead: 0));

        IReadOnlyList<TranscriptUsageRecord> records = ScanCodex();

        Assert.AreEqual(2, records.Count);
        Assert.IsTrue(records.All(record => record.BilledTokens > 0));
        Assert.AreEqual(220L, records[1].BilledTokens);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-014 [USE-011] Codex quota is read from the records, never estimated")]
    public void CodexReadsQuotaFromRecords()
    {
        var source = new CodexTokenUsageSource(_root);
        WriteSession(
            "codex.jsonl",
            CodexTurnContext("gpt-5.6-sol", "2026-08-28T09:00:00.000Z"),
            CodexTokenCountWithQuota("2026-08-28T09:00:01.000Z", usedPercent: 30, windowMinutes: 300));

        source.Scan(DateTimeOffset.MinValue, CancellationToken.None);

        VendorQuotaSnapshot quota = source.Quota!;
        Assert.AreEqual(1, quota.Windows.Count);
        Assert.AreEqual("primary", quota.Windows[0].WindowId);
        Assert.AreEqual(30d, quota.Windows[0].UsedPercent, 0.001d);
        Assert.AreEqual(300, quota.Windows[0].WindowMinutes);
        // Read from a record rather than queried, so it is exactly as old as that record.
        Assert.AreEqual(
            DateTimeOffset.Parse("2026-08-28T09:00:01.000Z", CultureInfo.InvariantCulture)
                .ToUniversalTime(),
            quota.ObservedAtUtc);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-015 [USE-011] Claude publishes no quota and must not invent one")]
    public void ClaudeReportsNoQuota()
    {
        var source = new ClaudeTokenUsageSource(_root);
        WriteSession(
            "a.jsonl",
            ClaudeLine("req_1", "msg_1", "claude-opus-5", "2026-08-28T09:00:00.000Z", 1, 1, 0, 0));
        source.Scan(DateTimeOffset.MinValue, CancellationToken.None);

        Assert.IsNull(source.Quota);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-016 [USE-011] Re-reading a truncated Codex file does not double-count")]
    public void CodexRereadIsAbsorbedByDeduplication()
    {
        var source = new CodexTokenUsageSource(_root);
        var aggregator = new TokenUsageAggregator(TimeZoneInfo.Utc);
        DateTimeOffset now = new(2026, 8, 28, 10, 0, 0, TimeSpan.Zero);

        WriteSession(
            "codex.jsonl",
            CodexTurnContext("gpt-5.6-sol", "2026-08-28T09:00:00.000Z"),
            CodexTokenCount("2026-08-28T09:00:01.000Z", input: 100, output: 10, cacheRead: 0));
        aggregator.Ingest(source.Scan(DateTimeOffset.MinValue, CancellationToken.None), now);
        long afterFirst = aggregator.Compute(
            now,
            [TokenUsageContract.CodexVendorId]).Overview.TodayBilledTokens;

        // A full re-read replays the same increments; the identity is reproducible, so
        // de-duplication absorbs them.
        source.Reset();
        aggregator.Ingest(source.Scan(DateTimeOffset.MinValue, CancellationToken.None), now);

        Assert.AreEqual(
            afterFirst,
            aggregator.Compute(
                now,
                [TokenUsageContract.CodexVendorId]).Overview.TodayBilledTokens);
    }

    private IReadOnlyList<TranscriptUsageRecord> ScanClaude() =>
        new ClaudeTokenUsageSource(_root).Scan(DateTimeOffset.MinValue, CancellationToken.None);

    private IReadOnlyList<TranscriptUsageRecord> ScanCodex() =>
        new CodexTokenUsageSource(_root).Scan(DateTimeOffset.MinValue, CancellationToken.None);

    private string SessionPath(string fileName) =>
        Path.Combine(_root, "project-a", fileName);

    private void WriteRaw(string fileName, string content) =>
        File.WriteAllText(
            SessionPath(fileName),
            content,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private void WriteSession(string fileName, params string[] lines) =>
        WriteRaw(fileName, string.Concat(lines.Select(line => line + "\n")));

    private void AppendSession(string fileName, params string[] lines) =>
        File.AppendAllText(
            SessionPath(fileName),
            string.Concat(lines.Select(line => line + "\n")),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    internal static string ClaudeLine(
        string requestId,
        string messageId,
        string model,
        string timestamp,
        long input,
        long output,
        long cacheCreation,
        long cacheRead) =>
        string.Format(
            CultureInfo.InvariantCulture,
            """{{"requestId":"{0}","timestamp":"{1}","type":"assistant","message":{{"id":"{2}","model":"{3}","role":"assistant","content":[],"usage":{{"input_tokens":{4},"output_tokens":{5},"cache_creation_input_tokens":{6},"cache_read_input_tokens":{7}}}}}}}""",
            requestId,
            timestamp,
            messageId,
            model,
            input,
            output,
            cacheCreation,
            cacheRead);

    internal static string CodexTurnContext(string model, string timestamp) =>
        string.Format(
            CultureInfo.InvariantCulture,
            """{{"timestamp":"{0}","type":"turn_context","payload":{{"model":"{1}","cwd":"D:\\x"}}}}""",
            timestamp,
            model);

    internal static string CodexTokenCount(
        string timestamp,
        long input,
        long output,
        long cacheRead) =>
        string.Format(
            CultureInfo.InvariantCulture,
            """{{"timestamp":"{0}","type":"event_msg","payload":{{"type":"token_count","info":{{"total_token_usage":{{"input_tokens":{1},"cached_input_tokens":{3},"cache_write_input_tokens":0,"output_tokens":{2},"reasoning_output_tokens":0,"total_tokens":{4}}},"last_token_usage":{{"input_tokens":{1},"output_tokens":{2}}}}}}}}}""",
            timestamp,
            input,
            output,
            cacheRead,
            input + output);

    internal static string CodexTokenCountWithQuota(
        string timestamp,
        double usedPercent,
        int windowMinutes) =>
        string.Format(
            CultureInfo.InvariantCulture,
            """{{"timestamp":"{0}","type":"event_msg","payload":{{"type":"token_count","info":{{"total_token_usage":{{"input_tokens":100,"cached_input_tokens":0,"cache_write_input_tokens":0,"output_tokens":10,"total_tokens":110}}}},"rate_limits":{{"primary":{{"used_percent":{1},"window_minutes":{2},"resets_at":4102444800}},"credits":{{"has_credits":true,"balance":"2927.9641200000"}}}}}}}}""",
            timestamp,
            usedPercent,
            windowMinutes);
}
