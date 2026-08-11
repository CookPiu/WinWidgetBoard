using System.Text.Json;
using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class CardSettingsDraftTests
{
    private static readonly int[] OrderedAscending = [1, 2];
    private static readonly int[] OrderedDescending = [2, 1];

    [TestMethod(DisplayName = "UT-CARD-032 [SET-003] Schema rejects invalid IDs, kinds and versions")]
    public void SchemaRejectsInvalidDefinitions()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new CardSettingDefinition(
            "",
            JsonValueKind.String,
            CardSettingWritePolicy.PreviewSafe));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new CardSettingDefinition(
                "invalid-kind",
                JsonValueKind.Undefined,
                CardSettingWritePolicy.PreviewSafe));

        CardSettingDefinition duplicate = Definition(
            "duplicate",
            JsonValueKind.String,
            CardSettingWritePolicy.PreviewSafe);
        Assert.ThrowsExactly<ArgumentException>(() => new CardSettingsSchema(
            [duplicate, Definition(
                "duplicate",
                JsonValueKind.Number,
                CardSettingWritePolicy.CommitOnly)]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new CardSettingsSchema([], schemaVersion: 0));
    }

    [TestMethod(DisplayName = "UT-CARD-033 [SET-003] Snapshot deep owns settings and preserves unknown fields")]
    public void SnapshotOwnsSettingsAndUnknownFields()
    {
        var source = new Dictionary<string, object?>
        {
            ["known"] = "before",
            ["unknown"] = new Dictionary<string, object?>
            {
                ["nested"] = 7,
            },
        };
        JsonElement input = JsonSerializer.SerializeToElement(source);
        CardSettingsSnapshot snapshot = Snapshot(input);
        source["known"] = "changed";

        CardSettingsDraft draft = new(
            snapshot,
            new CardSettingsSchema(
                [Definition("known", JsonValueKind.String, CardSettingWritePolicy.PreviewSafe)]),
            "session-test");
        draft.Set("known", JsonSerializer.SerializeToElement("after"));
        CardSettingsCommitRequest request = draft.BuildCommitRequest();

        Assert.AreEqual(
            7,
            request.Settings.GetProperty("unknown").GetProperty("nested").GetInt32());
        Assert.AreEqual(
            "before",
            snapshot.Settings.GetProperty("known").GetString());
        Assert.AreEqual(
            "changed",
            source["known"] as string);
    }

    [TestMethod(DisplayName = "UT-CARD-034 [SET-003] Preview-safe updates emit immutable set and remove changes")]
    public void PreviewSafeSetAndRemoveEmitChanges()
    {
        var changes = new List<CardSettingsPreviewChange>();
        CardSettingsDraft draft = CreateDraft(previewSink: changes.Add);

        CardSettingsPreviewChange? set = draft.Set(
            "theme",
            JsonSerializer.SerializeToElement("light"));
        CardSettingsPreviewChange? remove = draft.Remove("theme");

        Assert.IsNotNull(set);
        Assert.IsNotNull(remove);
        Assert.AreEqual(CardSettingsPreviewOperation.Set, set!.Operation);
        Assert.AreEqual(CardSettingsPreviewOperation.Remove, remove!.Operation);
        Assert.AreEqual(2, changes.Count);
        Assert.AreEqual("light", changes[0].Value!.Value.GetString());
        Assert.IsFalse(changes[1].ValuePresent);
        Assert.AreEqual("dark", draft.OriginalSnapshot.Settings.GetProperty("theme").GetString());
    }

    [TestMethod(DisplayName = "UT-CARD-035 [SET-003] Commit-only updates never emit previews")]
    public void CommitOnlyUpdatesNeverPreview()
    {
        var changes = new List<CardSettingsPreviewChange>();
        CardSettingsDraft draft = CreateDraft(previewSink: changes.Add);

        draft.Set("accountUnlink", JsonSerializer.SerializeToElement(true));
        draft.Remove("accountUnlink");

        Assert.AreEqual(0, changes.Count);
        Assert.AreEqual(2, draft.Version);
    }

    [TestMethod(DisplayName = "UT-CARD-036 [SET-003] Same-value updates are no-ops")]
    public void SameValueUpdateIsNoOp()
    {
        var changes = new List<CardSettingsPreviewChange>();
        CardSettingsDraft draft = CreateDraft(previewSink: changes.Add);
        JsonElement same = JsonSerializer.SerializeToElement("dark");

        Assert.IsNull(draft.Set("theme", same));
        Assert.AreEqual(0, draft.Version);
        Assert.AreEqual(0, changes.Count);
        Assert.IsNull(draft.Remove("missingKnown"));
    }

    [TestMethod(DisplayName = "UT-CARD-037 [SET-003] Invalid updates fail atomically")]
    public void InvalidUpdateFailsAtomically()
    {
        CardSettingsDraft draft = CreateDraft();
        JsonElement before = draft.Settings;

        Assert.ThrowsExactly<ArgumentException>(() => draft.Set(
            "theme",
            JsonSerializer.SerializeToElement(42)));
        Assert.ThrowsExactly<ArgumentException>(() => draft.Set(
            "unknown",
            JsonSerializer.SerializeToElement("value")));
        Assert.ThrowsExactly<ArgumentException>(() => draft.Remove("unknown"));

        Assert.AreEqual(0, draft.Version);
        Assert.IsTrue(JsonElement.DeepEquals(before, draft.Settings));
    }

    [TestMethod(DisplayName = "UT-CARD-038 [SET-003] Cancel restores preview values in reverse order and aggregates errors")]
    public void CancelRestoresInReverseAndAggregatesErrors()
    {
        var changes = new List<CardSettingsPreviewChange>();
        CardSettingsDraft? draft = null;
        draft = CreateDraft(previewSink: change =>
        {
            changes.Add(change);
            if (change.Operation == CardSettingsPreviewOperation.Restore &&
                change.SettingId == "second")
            {
                throw new InvalidOperationException("restore failed");
            }
        });
        draft.Set("first", JsonSerializer.SerializeToElement("new-first"));
        draft.Set("second", JsonSerializer.SerializeToElement("new-second"));

        CardSettingsCancelReport report = draft.Cancel();

        Assert.AreEqual(CardSettingsDraftState.Cancelled, draft.State);
        Assert.AreEqual(2, report.RestoreChanges.Count);
        Assert.AreEqual("second", report.RestoreChanges[0].SettingId);
        Assert.AreEqual("first", report.RestoreChanges[1].SettingId);
        Assert.AreEqual(1, report.Failures.Count);
        Assert.AreEqual("second", report.Failures[0].SettingId);
        Assert.AreEqual("second", report.Failures[0].RestoreChange.SettingId);
        Assert.IsFalse(report.Succeeded);
        Assert.AreEqual(CardSettingsPreviewOperation.Restore, changes[2].Operation);
        Assert.AreEqual(CardSettingsPreviewOperation.Restore, changes[3].Operation);
    }

    [TestMethod(DisplayName = "UT-CARD-039 [SET-003] Commit request carries expected revision and detached settings")]
    public void CommitRequestCarriesExpectedRevisionAndOwnsSettings()
    {
        CardSettingsDraft draft = CreateDraft();
        draft.Set("theme", JsonSerializer.SerializeToElement("light"));

        CardSettingsCommitRequest request = draft.BuildCommitRequest();
        Assert.AreEqual("session-test", request.SessionId);
        Assert.AreEqual(1, request.DraftVersion);
        Assert.AreEqual(4, request.ExpectedRevision);
        Assert.AreEqual("light", request.Settings.GetProperty("theme").GetString());

        draft.Set("theme", JsonSerializer.SerializeToElement("system"));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            draft.AcceptCommit(request, 5));
    }

    [TestMethod(DisplayName = "UT-CARD-040 [SET-003] Editing invalidates an earlier commit request")]
    public void EditingInvalidatesEarlierRequest()
    {
        CardSettingsDraft draft = CreateDraft();
        CardSettingsCommitRequest request = draft.BuildCommitRequest();
        draft.Set("theme", JsonSerializer.SerializeToElement("light"));

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            draft.AcceptCommit(request, 5));
        Assert.AreEqual(CardSettingsDraftState.Active, draft.State);
    }

    [TestMethod(DisplayName = "UT-CARD-041 [SET-003] Invalid request/session/revision is rejected while active")]
    public void InvalidCommitRequestIsRejectedWhileActive()
    {
        CardSettingsDraft draft = CreateDraft();
        CardSettingsCommitRequest request = draft.BuildCommitRequest();
        CardSettingsCommitRequest wrongSession = ReflectionCommitRequestFactory.Create(
            request,
            "other-session");

        Assert.ThrowsExactly<InvalidOperationException>(() => draft.AcceptCommit(wrongSession, 5));
        Assert.ThrowsExactly<InvalidOperationException>(() => draft.AcceptCommit(request, 4));
        Assert.AreEqual(CardSettingsDraftState.Active, draft.State);
    }

    [TestMethod(DisplayName = "UT-CARD-042 [SET-003] Successful accept returns new snapshot and commits draft")]
    public void SuccessfulAcceptReturnsSnapshotAndCommitsDraft()
    {
        CardSettingsDraft draft = CreateDraft();
        draft.Set("theme", JsonSerializer.SerializeToElement("light"));
        CardSettingsCommitRequest request = draft.BuildCommitRequest();

        CardSettingsSnapshot committed = draft.AcceptCommit(request, 5);

        Assert.AreEqual(CardSettingsDraftState.Committed, draft.State);
        Assert.AreEqual(5, committed.Revision);
        Assert.AreEqual("light", committed.Settings.GetProperty("theme").GetString());
        Assert.AreEqual("unknown-preserved", committed.Settings.GetProperty("unknown").GetString());
    }

    [TestMethod(DisplayName = "UT-CARD-043 [SET-003] Ended drafts reject edit, build and accept operations")]
    public void EndedDraftRejectsFurtherOperations()
    {
        CardSettingsDraft cancelled = CreateDraft();
        cancelled.Cancel();
        Assert.ThrowsExactly<InvalidOperationException>(() => cancelled.Set(
            "theme",
            JsonSerializer.SerializeToElement("light")));
        Assert.ThrowsExactly<InvalidOperationException>(() => cancelled.BuildCommitRequest());
        Assert.ThrowsExactly<InvalidOperationException>(() => cancelled.Cancel());

        CardSettingsDraft committed = CreateDraft();
        CardSettingsCommitRequest request = committed.BuildCommitRequest();
        committed.AcceptCommit(request, 5);
        Assert.ThrowsExactly<InvalidOperationException>(() => committed.Remove("theme"));
        Assert.ThrowsExactly<InvalidOperationException>(() => committed.BuildCommitRequest());
        Assert.ThrowsExactly<InvalidOperationException>(() => committed.AcceptCommit(request, 6));
    }

    [TestMethod(DisplayName = "UT-CARD-044 [SET-003] Concurrent set and cancel leave draft in one terminal state")]
    public async Task ConcurrentSetAndCancelPreserveStateSafety()
    {
        CardSettingsDraft draft = CreateDraft();
        Task set = Task.Run(() =>
        {
            try
            {
                draft.Set("theme", JsonSerializer.SerializeToElement("light"));
            }
            catch (InvalidOperationException)
            {
            }
        });
        Task cancel = Task.Run(() =>
        {
            try
            {
                draft.Cancel();
            }
            catch (InvalidOperationException)
            {
            }
        });

        await Task.WhenAll(set, cancel);
        Assert.AreNotEqual(CardSettingsDraftState.Active, draft.State);
        Assert.IsTrue(
            draft.State is CardSettingsDraftState.Cancelled or CardSettingsDraftState.Committed);
    }

    [TestMethod(DisplayName = "UT-CARD-045 [SET-003] Cancel waits for an in-flight preview and restores after it")]
    public async Task CancelWaitsForInFlightPreviewDeterministically()
    {
        var previewStarted = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreview = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelInvoked = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var changes = new List<CardSettingsPreviewChange>();
        CardSettingsDraft draft = CreateDraft(previewSink: change =>
        {
            changes.Add(change);
            if (change.Operation == CardSettingsPreviewOperation.Set)
            {
                previewStarted.SetResult(null);
                releasePreview.Task.GetAwaiter().GetResult();
            }
        });

        Task setTask = Task.Run(() => draft.Set(
            "theme",
            JsonSerializer.SerializeToElement("light")));
        await previewStarted.Task;
        Task cancelTask = Task.Run(() =>
        {
            cancelInvoked.SetResult(null);
            return draft.Cancel();
        });
        await cancelInvoked.Task;
        Assert.IsFalse(cancelTask.IsCompleted);

        releasePreview.SetResult(null);
        await Task.WhenAll(setTask, cancelTask);

        Assert.AreEqual(2, changes.Count);
        Assert.AreEqual(CardSettingsPreviewOperation.Set, changes[0].Operation);
        Assert.AreEqual(CardSettingsPreviewOperation.Restore, changes[1].Operation);
        Assert.AreEqual(CardSettingsDraftState.Cancelled, draft.State);
    }

    [TestMethod(DisplayName = "UT-CARD-046 [SET-003] Serialized preview gate preserves concurrent set order")]
    public async Task ConcurrentSetsPublishInStateOrder()
    {
        var firstStarted = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondInvoked = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var changes = new List<CardSettingsPreviewChange>();
        CardSettingsDraft draft = CreateDraft(previewSink: change =>
        {
            lock (changes)
            {
                changes.Add(change);
            }

            if (change.SettingId == "first")
            {
                firstStarted.SetResult(null);
                releaseFirst.Task.GetAwaiter().GetResult();
            }
        });

        Task first = Task.Run(() => draft.Set(
            "first",
            JsonSerializer.SerializeToElement("one")));
        await firstStarted.Task;
        Task second = Task.Run(() =>
        {
            secondInvoked.SetResult(null);
            return draft.Set(
                "second",
                JsonSerializer.SerializeToElement("two"));
        });
        await secondInvoked.Task;
        Assert.IsFalse(second.IsCompleted);

        releaseFirst.SetResult(null);
        await Task.WhenAll(first, second);

        Assert.AreEqual(2, changes.Count);
        Assert.AreEqual("first", changes[0].SettingId);
        Assert.AreEqual("second", changes[1].SettingId);
    }

    [TestMethod(DisplayName = "UT-CARD-047 [SET-003] Preview handler may re-enter draft without observing a held state lock")]
    public void PreviewHandlerCanReenterDraft()
    {
        CardSettingsDraft? draft = null;
        bool reentered = false;
        draft = CreateDraft(previewSink: change =>
        {
            _ = draft!.State;
            _ = draft.Settings;
            if (!reentered && change.SettingId == "theme")
            {
                reentered = true;
                draft.Set("first", JsonSerializer.SerializeToElement("reentered"));
            }
        });

        draft.Set("theme", JsonSerializer.SerializeToElement("light"));

        Assert.IsTrue(reentered);
        Assert.AreEqual(2, draft.Version);
        Assert.AreEqual("reentered", draft.Settings.GetProperty("first").GetString());
    }

    [TestMethod(DisplayName = "UT-CARD-048 [SET-003] Commit request rejects cross-instance and cross-schema requests")]
    public void CommitRequestRejectsCrossIdentityRequests()
    {
        CardSettingsDraft draft = CreateDraft();
        CardSettingsCommitRequest otherInstanceRequest = new CardSettingsDraft(
            new CardSettingsSnapshot(
                "other-instance",
                "test.card",
                1,
                4,
                JsonSerializer.SerializeToElement(new { theme = "dark" })),
            new CardSettingsSchema(
            [Definition("theme", JsonValueKind.String, CardSettingWritePolicy.PreviewSafe)]),
            "session-test").BuildCommitRequest();

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            draft.AcceptCommit(otherInstanceRequest, 5));

        CardSettingsSnapshot schemaTwoSnapshot = new(
            "instance-test",
            "test.card",
            2,
            4,
            JsonSerializer.SerializeToElement(new { theme = "dark" }));
        CardSettingsCommitRequest otherSchemaRequest = new CardSettingsDraft(
            schemaTwoSnapshot,
            new CardSettingsSchema(
                [Definition("theme", JsonValueKind.String, CardSettingWritePolicy.PreviewSafe)],
                schemaVersion: 2),
            "session-test").BuildCommitRequest();
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            draft.AcceptCommit(otherSchemaRequest, 5));
    }

    [TestMethod(DisplayName = "UT-CARD-049 [SET-003] Settings candidate over 64 KiB is rejected atomically")]
    public void OversizedSettingsAreRejectedAtomically()
    {
        CardSettingsDraft draft = CreateDraft();
        JsonElement before = draft.Settings;
        string oversized = new('x', CardSettingsSnapshot.MaxSettingsJsonBytes);

        Assert.ThrowsExactly<ArgumentException>(() => draft.Set(
            "theme",
            JsonSerializer.SerializeToElement(oversized)));
        Assert.AreEqual(0, draft.Version);
        Assert.IsTrue(JsonElement.DeepEquals(before, draft.Settings));
        Assert.ThrowsExactly<ArgumentException>(() => new CardSettingsSnapshot(
            "instance-test",
            "test.card",
            1,
            0,
            JsonSerializer.SerializeToElement(new { oversized })));
    }

    [TestMethod(DisplayName = "UT-CARD-050 [SET-003] Structured equality ignores object order and preserves array order")]
    public void StructuredEqualityUsesJsonSemantics()
    {
        CardSettingsDraft nestedDraft = new(
            Snapshot(JsonSerializer.SerializeToElement(
                new
                {
                    nested = new Dictionary<string, int>
                    {
                        ["a"] = 1,
                        ["b"] = 2,
                    },
                    ordered = OrderedAscending,
                })),
            new CardSettingsSchema(
            [
                Definition("nested", JsonValueKind.Object, CardSettingWritePolicy.PreviewSafe),
                Definition("ordered", JsonValueKind.Array, CardSettingWritePolicy.PreviewSafe),
            ]),
            "session-test");
        JsonElement reordered = JsonSerializer.SerializeToElement(
            new Dictionary<string, int>
            {
                ["b"] = 2,
                ["a"] = 1,
            });

        Assert.IsNull(nestedDraft.Set("nested", reordered));
        Assert.IsNotNull(nestedDraft.Set(
            "ordered",
            JsonSerializer.SerializeToElement(OrderedDescending)));
    }

    [TestMethod(DisplayName = "UT-CARD-051 [SET-003] Snapshot clones values before source document disposal")]
    public void SnapshotDetachesDisposedDocument()
    {
        CardSettingsSnapshot snapshot;
        using (JsonDocument document = JsonDocument.Parse("{\"theme\":\"dark\"}"))
        {
            snapshot = Snapshot(document.RootElement);
        }

        Assert.AreEqual("dark", snapshot.Settings.GetProperty("theme").GetString());
    }

    [TestMethod(DisplayName = "UT-CARD-052 [SET-003] Cancel restores only preview-safe settings")]
    public void CancelDoesNotRestoreCommitOnly()
    {
        var changes = new List<CardSettingsPreviewChange>();
        CardSettingsDraft draft = CreateDraft(previewSink: changes.Add);
        draft.Set("accountUnlink", JsonSerializer.SerializeToElement(true));
        draft.Set("theme", JsonSerializer.SerializeToElement("light"));

        CardSettingsCancelReport report = draft.Cancel();

        Assert.AreEqual(1, report.RestoreChanges.Count);
        Assert.AreEqual("theme", report.RestoreChanges[0].SettingId);
        Assert.IsFalse(changes.Any(change =>
            change.Operation == CardSettingsPreviewOperation.Restore &&
            change.SettingId == "accountUnlink"));
    }

    [TestMethod(DisplayName = "UT-CARD-053 [SET-003] Failed revision acceptance leaves active draft retryable")]
    public void FailedAcceptLeavesDraftActiveForRetry()
    {
        CardSettingsDraft draft = CreateDraft();
        CardSettingsCommitRequest request = draft.BuildCommitRequest();
        Assert.ThrowsExactly<InvalidOperationException>(() => draft.AcceptCommit(request, 6));
        Assert.AreEqual(CardSettingsDraftState.Active, draft.State);

        CardSettingsSnapshot committed = draft.AcceptCommit(request, 5);
        Assert.AreEqual(5, committed.Revision);
        Assert.AreEqual(CardSettingsDraftState.Committed, draft.State);
    }

    [TestMethod(DisplayName = "UT-CARD-054 [SET-003] Custom validators run outside state lock and may re-enter")]
    public void ValidatorRunsOutsideStateLock()
    {
        CardSettingsDraft? draft = null;
        bool validatorCalled = false;
        var schema = new CardSettingsSchema(
        [
            Definition("theme", JsonValueKind.String, CardSettingWritePolicy.PreviewSafe),
            new CardSettingDefinition(
                "validated",
                JsonValueKind.String,
                CardSettingWritePolicy.PreviewSafe,
                value =>
                {
                    validatorCalled = true;
                    _ = draft!.State;
                    draft.Set("theme", JsonSerializer.SerializeToElement("from-validator"));
                    return value.GetString() == "accepted";
                }),
        ]);
        draft = new CardSettingsDraft(
            Snapshot(JsonSerializer.SerializeToElement(new { theme = "dark" })),
            schema,
            "session-test");

        draft.Set("validated", JsonSerializer.SerializeToElement("accepted"));

        Assert.IsTrue(validatorCalled);
        Assert.AreEqual("from-validator", draft.Settings.GetProperty("theme").GetString());
        Assert.AreEqual("accepted", draft.Settings.GetProperty("validated").GetString());
    }

    [TestMethod(DisplayName = "UT-CARD-055 [SET-003] Declared snapshot values are validated while unknown fields remain opaque")]
    public void ConstructorValidatesDeclaredSnapshotValues()
    {
        CardSettingsSnapshot invalid = Snapshot(JsonSerializer.SerializeToElement(
            new
            {
                theme = 42,
                unknown = "preserved",
            }));
        CardSettingsSchema schema = new(
            [Definition("theme", JsonValueKind.String, CardSettingWritePolicy.PreviewSafe)]);

        Assert.ThrowsExactly<ArgumentException>(() => new CardSettingsDraft(
            invalid,
            schema,
            "session-test"));

        CardSettingsDraft valid = new(
            Snapshot(JsonSerializer.SerializeToElement(
                new
                {
                    unknown = "preserved",
                })),
            schema,
            "session-test");
        Assert.AreEqual(
            "preserved",
            valid.BuildCommitRequest().Settings.GetProperty("unknown").GetString());
    }

    [TestMethod(DisplayName = "UT-CARD-056 [SET-003] Cancel restores each preview-safe key once from the session baseline")]
    public void CancelRestoresEachPreviewKeyOnceFromBaseline()
    {
        var changes = new List<CardSettingsPreviewChange>();
        CardSettingsDraft draft = new(
            Snapshot(JsonSerializer.SerializeToElement(new { theme = "dark" })),
            new CardSettingsSchema(
            [
                Definition("theme", JsonValueKind.String, CardSettingWritePolicy.PreviewSafe),
                Definition("transient", JsonValueKind.String, CardSettingWritePolicy.PreviewSafe),
            ]),
            "session-test",
            changes.Add);

        draft.Set("theme", JsonSerializer.SerializeToElement("light"));
        draft.Set("theme", JsonSerializer.SerializeToElement("blue"));
        draft.Remove("theme");
        draft.Set("theme", JsonSerializer.SerializeToElement("red"));
        draft.Set("transient", JsonSerializer.SerializeToElement("temporary"));
        draft.Remove("transient");

        CardSettingsCancelReport report = draft.Cancel();

        Assert.AreEqual(2, report.RestoreChanges.Count);
        Assert.AreEqual("transient", report.RestoreChanges[0].SettingId);
        Assert.IsFalse(report.RestoreChanges[0].ValuePresent);
        Assert.AreEqual("theme", report.RestoreChanges[1].SettingId);
        Assert.IsTrue(report.RestoreChanges[1].ValuePresent);
        Assert.AreEqual(
            "dark",
            report.RestoreChanges[1].Value!.Value.GetString());
        Assert.AreEqual(2, changes.Count(change =>
            change.Operation == CardSettingsPreviewOperation.Restore));
    }

    [TestMethod(DisplayName = "UT-CARD-057 [SET-003] Preview sink failure keeps accepted draft active and cancelable")]
    public void PreviewSinkFailureKeepsDraftActiveAndCancelable()
    {
        var changes = new List<CardSettingsPreviewChange>();
        CardSettingsDraft draft = CreateDraft(previewSink: change =>
        {
            changes.Add(change);
            if (change.Operation == CardSettingsPreviewOperation.Set)
            {
                throw new InvalidOperationException("preview sink failed");
            }
        });

        Assert.ThrowsExactly<InvalidOperationException>(() => draft.Set(
            "theme",
            JsonSerializer.SerializeToElement("light")));
        Assert.AreEqual(CardSettingsDraftState.Active, draft.State);
        Assert.AreEqual(1, draft.Version);
        Assert.AreEqual("light", draft.Settings.GetProperty("theme").GetString());

        CardSettingsCancelReport report = draft.Cancel();

        Assert.IsTrue(report.Succeeded);
        Assert.AreEqual(CardSettingsDraftState.Cancelled, draft.State);
        Assert.AreEqual(2, changes.Count);
        Assert.AreEqual(CardSettingsPreviewOperation.Set, changes[0].Operation);
        Assert.AreEqual(CardSettingsPreviewOperation.Restore, changes[1].Operation);
        Assert.AreEqual("dark", changes[1].Value!.Value.GetString());
    }

    private static CardSettingsDraft CreateDraft(
        CardSettingsSnapshot? snapshot = null,
        Action<CardSettingsPreviewChange>? previewSink = null) =>
        new(
            snapshot ?? Snapshot(JsonSerializer.SerializeToElement(
                new Dictionary<string, object?>
                {
                    ["theme"] = "dark",
                    ["first"] = "old-first",
                    ["second"] = "old-second",
                    ["accountUnlink"] = false,
                    ["unknown"] = "unknown-preserved",
                })),
            new CardSettingsSchema(
            [
                Definition("theme", JsonValueKind.String, CardSettingWritePolicy.PreviewSafe),
                Definition("first", JsonValueKind.String, CardSettingWritePolicy.PreviewSafe),
                Definition("second", JsonValueKind.String, CardSettingWritePolicy.PreviewSafe),
                new CardSettingDefinition(
                    "accountUnlink",
                    [JsonValueKind.True, JsonValueKind.False],
                    CardSettingWritePolicy.CommitOnly),
                Definition("missingKnown", JsonValueKind.String, CardSettingWritePolicy.PreviewSafe),
            ]),
            "session-test",
            previewSink);

    private static CardSettingsSnapshot Snapshot(JsonElement settings) =>
        new("instance-test", "test.card", 1, 4, settings);

    private static CardSettingDefinition Definition(
        string id,
        JsonValueKind kind,
        CardSettingWritePolicy policy) =>
        new(id, kind, policy);

    private sealed class ReflectionCommitRequestFactory
    {
        public static CardSettingsCommitRequest Create(
            CardSettingsCommitRequest request,
            string sessionId)
        {
            // The production type intentionally exposes no mutators. This
            // test uses a fresh draft to produce a request with a different
            // session while preserving the same public contract.
            CardSettingsSnapshot snapshot = new(
                request.InstanceId,
                request.CardTypeId,
                request.SchemaVersion,
                request.ExpectedRevision,
                request.Settings);
            CardSettingsSchema schema = new(
                [Definition("theme", JsonValueKind.String, CardSettingWritePolicy.PreviewSafe)]);
            return new CardSettingsDraft(snapshot, schema, sessionId)
                .BuildCommitRequest();
        }
    }
}
