using System.Globalization;
using System.Text;
using WinWidgetBoard.CoreBroker.Providers;

namespace WinWidgetBoard.UnitTests;

/// <summary>
/// The scanner is the only thing standing between a live, half-written transcript and the
/// card's numbers, so the cases pinned here are the ones that silently corrupt a total rather
/// than throw: a response counted once per content block, a file re-read from the wrong offset,
/// and a line that was still being written when the tick ran.
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

    [TestMethod(DisplayName =
        "UT-TOKUSE-001 [USE-001] One response line yields its four counters and identity")]
    public void ParsesCountersAndIdentity()
    {
        WriteSession(
            "a.jsonl",
            UsageLine("req_1", "msg_1", "claude-opus-5", "2026-08-28T09:00:00.000Z", 11, 22, 33, 44));

        IReadOnlyList<TranscriptUsageRecord> records = Scan();

        Assert.AreEqual(1, records.Count);
        TranscriptUsageRecord record = records[0];
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

        IReadOnlyList<TranscriptUsageRecord> records = Scan();

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
            UsageLine("req_1", "msg_1", "<synthetic>", "2026-08-28T09:00:00.000Z", 9, 9, 9, 9),
            UsageLine("req_2", "msg_2", "claude-opus-5", "2026-08-28T09:00:01.000Z", 1, 1, 1, 1));

        IReadOnlyList<TranscriptUsageRecord> records = Scan();

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
            UsageLine("req_1", "msg_1", "claude-opus-5", "2026-08-28T09:00:02.000Z", 1, 2, 3, 4));

        Assert.AreEqual(1, Scan().Count);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-005 [USE-002] A second scan reads only what was appended")]
    public void ReadsOnlyAppendedBytes()
    {
        var scanner = new TranscriptUsageScanner(_root);
        WriteSession(
            "a.jsonl",
            UsageLine("req_1", "msg_1", "claude-opus-5", "2026-08-28T09:00:00.000Z", 1, 1, 0, 0));
        Assert.AreEqual(1, scanner.Scan(DateTimeOffset.MinValue).Count);

        // Nothing changed, so nothing is re-read - otherwise every tick would re-parse the
        // whole history and the de-duplication would be doing the scanner's job for it.
        Assert.AreEqual(0, scanner.Scan(DateTimeOffset.MinValue).Count);

        AppendSession(
            "a.jsonl",
            UsageLine("req_2", "msg_2", "claude-opus-5", "2026-08-28T09:00:01.000Z", 2, 2, 0, 0));
        IReadOnlyList<TranscriptUsageRecord> appended = scanner.Scan(DateTimeOffset.MinValue);
        Assert.AreEqual(1, appended.Count);
        Assert.AreEqual("msg_2", appended[0].MessageId);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-006 [USE-002] A line still being written is left for the next scan")]
    public void DoesNotParseAPartiallyWrittenLine()
    {
        var scanner = new TranscriptUsageScanner(_root);
        string complete = UsageLine(
            "req_1",
            "msg_1",
            "claude-opus-5",
            "2026-08-28T09:00:00.000Z",
            1,
            1,
            0,
            0);
        string partial = UsageLine(
            "req_2",
            "msg_2",
            "claude-opus-5",
            "2026-08-28T09:00:01.000Z",
            2,
            2,
            0,
            0);

        // No trailing newline on the second line: the writer is mid-flush.
        File.WriteAllText(
            SessionPath("a.jsonl"),
            complete + "\n" + partial[..40],
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Assert.AreEqual(1, scanner.Scan(DateTimeOffset.MinValue).Count);

        // Once the writer finishes the line, the whole line is read - not the tail of it.
        File.WriteAllText(
            SessionPath("a.jsonl"),
            complete + "\n" + partial + "\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        IReadOnlyList<TranscriptUsageRecord> second = scanner.Scan(DateTimeOffset.MinValue);
        Assert.AreEqual(1, second.Count);
        Assert.AreEqual("msg_2", second[0].MessageId);
        Assert.AreEqual(0L, scanner.SkippedLineCount);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-007 [USE-002] A truncated file is re-read from the beginning")]
    public void RereadsATruncatedFile()
    {
        var scanner = new TranscriptUsageScanner(_root);
        WriteSession(
            "a.jsonl",
            UsageLine("req_1", "msg_1", "claude-opus-5", "2026-08-28T09:00:00.000Z", 1, 1, 0, 0),
            UsageLine("req_2", "msg_2", "claude-opus-5", "2026-08-28T09:00:01.000Z", 1, 1, 0, 0));
        Assert.AreEqual(2, scanner.Scan(DateTimeOffset.MinValue).Count);

        // Rewritten shorter: the watermark now points past the end, so resuming from it would
        // read nothing ever again.
        WriteSession(
            "a.jsonl",
            UsageLine("req_3", "msg_3", "claude-opus-5", "2026-08-28T10:00:00.000Z", 1, 1, 0, 0));

        IReadOnlyList<TranscriptUsageRecord> records = scanner.Scan(DateTimeOffset.MinValue);
        Assert.AreEqual(1, records.Count);
        Assert.AreEqual("msg_3", records[0].MessageId);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-008 [USE-002] A file older than the window is skipped on a first scan")]
    public void SkipsFilesOlderThanTheWindow()
    {
        WriteSession(
            "old.jsonl",
            UsageLine("req_1", "msg_1", "claude-opus-5", "2020-01-01T00:00:00.000Z", 1, 1, 0, 0));
        File.SetLastWriteTimeUtc(SessionPath("old.jsonl"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var scanner = new TranscriptUsageScanner(_root);
        Assert.AreEqual(0, scanner.Scan(DateTimeOffset.UtcNow.AddHours(-26)).Count);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-009 [USE-002] A malformed line is counted and skipped, not fatal")]
    public void SkipsMalformedLines()
    {
        var scanner = new TranscriptUsageScanner(_root);
        WriteSession(
            "a.jsonl",
            "{not json at all",
            UsageLine("req_1", "msg_1", "claude-opus-5", "2026-08-28T09:00:00.000Z", 1, 1, 0, 0));

        Assert.AreEqual(1, scanner.Scan(DateTimeOffset.MinValue).Count);
        Assert.AreEqual(1L, scanner.SkippedLineCount);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-010 [USE-002] A missing transcript root is an empty reading, not a failure")]
    public void MissingRootYieldsNoRecords()
    {
        var scanner = new TranscriptUsageScanner(
            Path.Combine(_root, "does-not-exist"));

        Assert.AreEqual(0, scanner.Scan(DateTimeOffset.MinValue).Count);
    }

    private IReadOnlyList<TranscriptUsageRecord> Scan() =>
        new TranscriptUsageScanner(_root).Scan(DateTimeOffset.MinValue);

    private string SessionPath(string fileName) =>
        Path.Combine(_root, "project-a", fileName);

    private void WriteSession(string fileName, params string[] lines) =>
        File.WriteAllText(
            SessionPath(fileName),
            string.Concat(lines.Select(line => line + "\n")),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private void AppendSession(string fileName, params string[] lines) =>
        File.AppendAllText(
            SessionPath(fileName),
            string.Concat(lines.Select(line => line + "\n")),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    internal static string UsageLine(
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
}
