using System.IO.Pipes;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Client;
using WinWidgetBoard.CoreBroker.Commands;
using WinWidgetBoard.CoreBroker.Hosting;
using WinWidgetBoard.CoreBroker.Ipc;
using WinWidgetBoard.WorkspacePanel.Ipc;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class CoreBrokerPipeTests
{
    private static readonly string[] SessionPingCapabilities = ["session.ping"];

    [TestMethod(DisplayName = "IT-PIPE-001 [G-006] Current-user pipe completes session hello and ping")]
    public async Task CurrentUserPipeCompletesHandshakeAndPing()
    {
        string pipeName = CoreBrokerPipeNames.CreateTestName();
        string token = "test-session-token";
        using var cancellation = new CancellationTokenSource();
        var server = new CoreBrokerPipeServer(pipeName, token, "0.1.0");
        Task serverTask = server.RunAsync(cancellation.Token);

        try
        {
            await using var client = new CoreBrokerPipeClient(pipeName);
            Envelope hello = await client.HandshakeAsync(
                SessionHelloContract.WorkspacePanelClientType,
                token,
                CancellationToken.None);

            Assert.IsNull(hello.Error);
            Assert.IsNotNull(hello.CorrelationId);
            Assert.AreEqual(EnvelopeMessageType.Response, hello.MessageType);
            Assert.AreEqual(ProtocolConstants.CurrentVersion, hello.Payload.GetProperty("acceptedProtocolVersion").GetString());

            Envelope ping = await client.SendAsync(
                new Envelope
                {
                    ProtocolVersion = ProtocolConstants.CurrentVersion,
                    MessageType = EnvelopeMessageType.Request,
                    MessageId = Guid.NewGuid(),
                    CorrelationId = null,
                    SentAtUtc = DateTimeOffset.UtcNow,
                    Method = "session.ping",
                    Payload = System.Text.Json.JsonSerializer.SerializeToElement(new { }, ContractJson.Options),
                    Error = null,
                },
                CancellationToken.None);

            Assert.IsNull(ping.Error);
            Assert.IsNotNull(ping.CorrelationId);
        }
        finally
        {
            cancellation.Cancel();
            await serverTask;
        }
    }

    [TestMethod(DisplayName = "IT-PIPE-002 [G-006] Invalid session token is rejected without exposing token data")]
    public async Task InvalidTokenIsRejected()
    {
        string pipeName = CoreBrokerPipeNames.CreateTestName();
        using var cancellation = new CancellationTokenSource();
        var server = new CoreBrokerPipeServer(pipeName, "expected-token", "0.1.0");
        Task serverTask = server.RunAsync(cancellation.Token);

        try
        {
            await using var client = new CoreBrokerPipeClient(pipeName);
            Envelope response = await client.HandshakeAsync(
                SessionHelloContract.WorkspacePanelClientType,
                "wrong-token",
                CancellationToken.None);

            Assert.IsNotNull(response.Error);
            Assert.AreEqual("session.unauthorized", response.Error!.Code);
            Assert.IsFalse(response.Error.DeveloperMessage?.Contains("wrong-token") == true);
        }
        finally
        {
            cancellation.Cancel();
            await serverTask;
        }
    }

    [TestMethod(DisplayName = "IT-PIPE-003 [SYS-001] CoreBroker instance lock allows only one owner")]
    public void InstanceLockAllowsOnlyOneOwner()
    {
        string mutexName = $"Local\\WinWidgetBoard.CoreBroker.test.{Guid.NewGuid():N}";
        using var first = new CoreBrokerInstanceLock(mutexName);
        using var second = new CoreBrokerInstanceLock(mutexName);

        Assert.IsTrue(first.IsAcquired);
        Assert.IsFalse(second.IsAcquired);
    }

    [TestMethod(DisplayName = "IT-PIPE-004 [NFR-REL-001] Client reconnects after CoreBroker restart")]
    public async Task ClientReconnectsAfterBrokerRestart()
    {
        string pipeName = CoreBrokerPipeNames.CreateTestName();
        string token = "restart-session-token";
        using var firstCancellation = new CancellationTokenSource();
        var firstServer = new CoreBrokerPipeServer(pipeName, token, "0.1.0");
        Task firstServerTask = firstServer.RunAsync(firstCancellation.Token);
        CancellationTokenSource? secondCancellation = null;
        Task? secondServerTask = null;

        try
        {
            await using var client = new CoreBrokerPipeClient(pipeName);
            Envelope hello = await client.HandshakeAsync(
                SessionHelloContract.WorkspacePanelClientType,
                token,
                CancellationToken.None);
            Assert.IsNull(hello.Error);

            firstCancellation.Cancel();
            await firstServerTask;

            secondCancellation = new CancellationTokenSource();
            var secondServer = new CoreBrokerPipeServer(pipeName, token, "0.1.0");
            secondServerTask = secondServer.RunAsync(secondCancellation.Token);

            Envelope ping = await client.SendWithReconnectAsync(
                CreatePingRequest(),
                CancellationToken.None);

            Assert.IsNull(ping.Error);
            Assert.IsTrue(client.IsConnected);
        }
        finally
        {
            firstCancellation.Cancel();
            await firstServerTask;
            if (secondCancellation is not null)
            {
                secondCancellation.Cancel();
            }

            if (secondServerTask is not null)
            {
                await secondServerTask;
            }

            secondCancellation?.Dispose();
        }
    }

    [TestMethod(DisplayName = "IT-PIPE-005 [G-003] Client heartbeat loop records successful ping")]
    public async Task ClientHeartbeatLoopRecordsSuccessfulPing()
    {
        string pipeName = CoreBrokerPipeNames.CreateTestName();
        string token = "heartbeat-session-token";
        using var serverCancellation = new CancellationTokenSource();
        var server = new CoreBrokerPipeServer(pipeName, token, "0.1.0");
        Task serverTask = server.RunAsync(serverCancellation.Token);

        try
        {
            await using var client = new CoreBrokerPipeClient(pipeName);
            Envelope hello = await client.HandshakeAsync(
                SessionHelloContract.WorkspacePanelClientType,
                token,
                CancellationToken.None);
            Assert.IsNull(hello.Error);

            using var heartbeatCancellation = new CancellationTokenSource();
            Task heartbeatTask = client.RunHeartbeatAsync(
                TimeSpan.FromMilliseconds(25),
                heartbeatCancellation.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(150));
            heartbeatCancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(
                async () => await heartbeatTask);
            Assert.IsNotNull(client.LastHeartbeatUtc);
        }
        finally
        {
            serverCancellation.Cancel();
            await serverTask;
        }
    }

    [TestMethod(DisplayName = "IT-PIPE-006 [NFR-REL-001] Silent request is interrupted by client timeout")]
    public async Task SilentRequestIsInterruptedByClientTimeout()
    {
        string pipeName = CoreBrokerPipeNames.CreateTestName();
        string token = "timeout-session-token";
        using var silentServerCancellation = new CancellationTokenSource();
        Task silentServerTask = RunSilentAfterHandshakeAsync(
            pipeName,
            token,
            silentServerCancellation.Token);

        try
        {
            await using var client = new CoreBrokerPipeClient(
                pipeName,
                requestTimeout: TimeSpan.FromMilliseconds(250));
            Envelope hello = await client.HandshakeAsync(
                SessionHelloContract.WorkspacePanelClientType,
                token,
                CancellationToken.None);
            Assert.IsNull(hello.Error);

            bool timedOut = false;
            try
            {
                await client.SendAsync(CreatePingRequest(), CancellationToken.None);
            }
            catch (TimeoutException)
            {
                timedOut = true;
            }

            Assert.IsTrue(timedOut);
        }
        finally
        {
            silentServerCancellation.Cancel();
            await silentServerTask;
        }
    }

    [TestMethod(DisplayName = "IT-PIPE-007 [G-006] WorkspacePanel session establishes a broker heartbeat owner")]
    [DoNotParallelize]
    public async Task WorkspacePanelSessionEstablishesBrokerHeartbeatOwner()
    {
        string pipeName = CoreBrokerPipeNames.CreateTestName();
        string token = "workspace-panel-session-token";
        string? previousToken = Environment.GetEnvironmentVariable(
            CoreBrokerSession.SessionTokenEnvironmentVariable);
        using var serverCancellation = new CancellationTokenSource();
        var server = new CoreBrokerPipeServer(pipeName, token, "0.1.0");
        Task serverTask = server.RunAsync(serverCancellation.Token);
        await using var session = new CoreBrokerSession(
            new CoreBrokerPipeClient(
                pipeName,
                connectTimeout: TimeSpan.FromSeconds(1),
                requestTimeout: TimeSpan.FromSeconds(1)));

        try
        {
            Environment.SetEnvironmentVariable(
                CoreBrokerSession.SessionTokenEnvironmentVariable,
                token);
            bool started = await session.TryStartAsync(CancellationToken.None);

            Assert.IsTrue(started);
            Assert.IsTrue(session.IsConnected);
        }
        finally
        {
            await session.DisposeAsync();
            Environment.SetEnvironmentVariable(
                CoreBrokerSession.SessionTokenEnvironmentVariable,
                previousToken);
            serverCancellation.Cancel();
            await serverTask;
        }
    }

    [TestMethod(DisplayName = "IT-PIPE-010 [NFR-REL-002] WorkspacePanel retries a Broker that is still starting")]
    [DoNotParallelize]
    public async Task WorkspacePanelRetriesBrokerStartup()
    {
        string pipeName = CoreBrokerPipeNames.CreateTestName();
        string token = "workspace-panel-startup-retry-token";
        string? previousToken = Environment.GetEnvironmentVariable(
            CoreBrokerSession.SessionTokenEnvironmentVariable);
        using var serverCancellation = new CancellationTokenSource();
        await using var session = new CoreBrokerSession(
            new CoreBrokerPipeClient(
                pipeName,
                connectTimeout: TimeSpan.FromMilliseconds(50),
                requestTimeout: TimeSpan.FromMilliseconds(250)));
        Task? serverTask = null;

        try
        {
            Environment.SetEnvironmentVariable(
                CoreBrokerSession.SessionTokenEnvironmentVariable,
                token);
            Task<bool> startTask = session.TryStartAsync(CancellationToken.None);
            await Task.Delay(200);

            var server = new CoreBrokerPipeServer(
                pipeName,
                token,
                "0.1.0");
            serverTask = server.RunAsync(serverCancellation.Token);

            Assert.IsTrue(await startTask);
            Assert.IsTrue(session.IsConnected);
        }
        finally
        {
            await session.DisposeAsync();
            Environment.SetEnvironmentVariable(
                CoreBrokerSession.SessionTokenEnvironmentVariable,
                previousToken);
            serverCancellation.Cancel();
            if (serverTask is not null)
            {
                await serverTask;
            }
        }
    }

    [TestMethod(DisplayName = "IT-PIPE-008 [PNL-004] Panel visibility report is idempotent")]
    public async Task PanelVisibilityReportIsIdempotent()
    {
        string pipeName = CoreBrokerPipeNames.CreateTestName();
        string token = "visibility-session-token";
        var router = new CoreBrokerCommandRouter();
        using var cancellation = new CancellationTokenSource();
        var server = new CoreBrokerPipeServer(pipeName, token, "0.1.0", router);
        Task serverTask = server.RunAsync(cancellation.Token);
        Guid operationId = Guid.NewGuid();

        try
        {
            await using var client = new CoreBrokerPipeClient(pipeName);
            Envelope hello = await client.HandshakeAsync(
                SessionHelloContract.WorkspacePanelClientType,
                token,
                CancellationToken.None);
            Assert.IsNull(hello.Error);

            Envelope first = await client.SendAsync(
                CreatePanelVisibilityReportRequest(true, operationId),
                CancellationToken.None);
            Envelope duplicate = await client.SendAsync(
                CreatePanelVisibilityReportRequest(true, operationId),
                CancellationToken.None);
            Envelope conflict = await client.SendAsync(
                CreatePanelVisibilityReportRequest(false, operationId),
                CancellationToken.None);

            Assert.IsNull(first.Error);
            Assert.IsNull(duplicate.Error);
            Assert.IsNotNull(conflict.Error);
            Assert.AreEqual("validation.invalid-argument", conflict.Error!.Code);
            PanelVisibilityReportResponse firstPayload = first.Payload.Deserialize<PanelVisibilityReportResponse>(
                ContractJson.Options)!;
            PanelVisibilityReportResponse duplicatePayload = duplicate.Payload.Deserialize<PanelVisibilityReportResponse>(
                ContractJson.Options)!;
            Assert.AreEqual(firstPayload.Revision, duplicatePayload.Revision);
            Assert.AreEqual(firstPayload.AcceptedAtUtc, duplicatePayload.AcceptedAtUtc);
            Assert.AreEqual(1, router.VisibilityReportCount);
            Assert.IsTrue(router.PanelVisible);
        }
        finally
        {
            cancellation.Cancel();
            await serverTask;
        }
    }

    [TestMethod(DisplayName = "IT-PIPE-009 [NFR-REL-001] WorkspacePanel re-reports visibility after Broker restart")]
    [DoNotParallelize]
    public async Task WorkspacePanelReReportsVisibilityAfterBrokerRestart()
    {
        string pipeName = CoreBrokerPipeNames.CreateTestName();
        string token = "visibility-restart-token";
        string? previousToken = Environment.GetEnvironmentVariable(
            CoreBrokerSession.SessionTokenEnvironmentVariable);
        using var firstCancellation = new CancellationTokenSource();
        var firstRouter = new CoreBrokerCommandRouter();
        var firstServer = new CoreBrokerPipeServer(
            pipeName,
            token,
            "0.1.0",
            firstRouter);
        Task firstServerTask = firstServer.RunAsync(firstCancellation.Token);
        CancellationTokenSource? secondCancellation = null;
        Task? secondServerTask = null;
        var secondRouter = new CoreBrokerCommandRouter();
        await using var session = new CoreBrokerSession(
            new CoreBrokerPipeClient(
                pipeName,
                connectTimeout: TimeSpan.FromSeconds(1),
                requestTimeout: TimeSpan.FromMilliseconds(250)),
            heartbeatInterval: TimeSpan.FromMilliseconds(25));
        var reconnected = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.Reconnected += (_, _) => reconnected.TrySetResult(null);

        try
        {
            Environment.SetEnvironmentVariable(
                CoreBrokerSession.SessionTokenEnvironmentVariable,
                token);
            Assert.IsTrue(await session.TryStartAsync(CancellationToken.None));
            Assert.IsTrue(await session.ReportVisibilityAsync(true, CancellationToken.None));
            Assert.AreEqual(1, firstRouter.VisibilityReportCount);

            firstCancellation.Cancel();
            await firstServerTask;

            secondCancellation = new CancellationTokenSource();
            var secondServer = new CoreBrokerPipeServer(
                pipeName,
                token,
                "0.1.0",
                secondRouter);
            secondServerTask = secondServer.RunAsync(secondCancellation.Token);

            await WaitUntilAsync(
                () => secondRouter.VisibilityReportCount > 0,
                TimeSpan.FromSeconds(3));
            await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.IsTrue(secondRouter.PanelVisible);
        }
        finally
        {
            await session.DisposeAsync();
            Environment.SetEnvironmentVariable(
                CoreBrokerSession.SessionTokenEnvironmentVariable,
                previousToken);
            firstCancellation.Cancel();
            await firstServerTask;
            if (secondCancellation is not null)
            {
                secondCancellation.Cancel();
            }

            if (secondServerTask is not null)
            {
                await secondServerTask;
            }

            secondCancellation?.Dispose();
        }
    }

    private static Envelope CreatePingRequest() =>
        new()
        {
            ProtocolVersion = ProtocolConstants.CurrentVersion,
            MessageType = EnvelopeMessageType.Request,
            MessageId = Guid.NewGuid(),
            CorrelationId = null,
            SentAtUtc = DateTimeOffset.UtcNow,
            Method = "session.ping",
            Payload = JsonSerializer.SerializeToElement(new { }, ContractJson.Options),
            Error = null,
        };

    private static Envelope CreatePanelVisibilityReportRequest(
        bool panelVisible,
        Guid clientOperationId) =>
        new()
        {
            ProtocolVersion = ProtocolConstants.CurrentVersion,
            MessageType = EnvelopeMessageType.Request,
            MessageId = Guid.NewGuid(),
            CorrelationId = null,
            SentAtUtc = DateTimeOffset.UtcNow,
            Method = PanelVisibilityContract.Method,
            Payload = JsonSerializer.SerializeToElement(
                new PanelVisibilityReportRequest
                {
                    ClientOperationId = clientOperationId,
                    PanelVisible = panelVisible,
                },
                ContractJson.Options),
            Error = null,
        };

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Fail("The expected condition was not reached before the timeout.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }
    }

    private static async Task RunSilentAfterHandshakeAsync(
        string pipeName,
        string expectedToken,
        CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            64 * 1024,
            64 * 1024);

        try
        {
            await pipe.WaitForConnectionAsync(cancellationToken);
            byte[] helloFrame = await LengthPrefixedFrameCodec.ReadAsync(
                pipe,
                cancellationToken);
            if (!EnvelopeCodec.TryDeserialize(
                    helloFrame,
                    out Envelope? hello,
                    out IReadOnlyList<ContractValidationError> helloErrors) ||
                hello is null)
            {
                throw new InvalidOperationException(
                    $"Invalid hello frame: {string.Join(",", helloErrors.Select(error => error.Code))}");
            }

            SessionHelloRequest? helloPayload = hello.Payload.Deserialize<SessionHelloRequest>(
                ContractJson.Options);
            if (!string.Equals(helloPayload?.SessionToken, expectedToken, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The test client sent an unexpected session token.");
            }

            Envelope response = new()
            {
                ProtocolVersion = ProtocolConstants.CurrentVersion,
                MessageType = EnvelopeMessageType.Response,
                MessageId = Guid.NewGuid(),
                CorrelationId = hello.MessageId,
                SentAtUtc = DateTimeOffset.UtcNow,
                Method = SessionHelloContract.Method,
                Payload = JsonSerializer.SerializeToElement(
                    new SessionHelloResponse
                    {
                        AcceptedProtocolVersion = ProtocolConstants.CurrentVersion,
                        ServerVersion = "0.1.0",
                        SessionId = Guid.NewGuid(),
                        Capabilities = SessionPingCapabilities,
                        MaxMessageBytes = ProtocolConstants.MaxMessageBytes,
                    },
                    ContractJson.Options),
                Error = null,
            };

            Assert.IsTrue(EnvelopeCodec.TrySerialize(
                response,
                out byte[] responseJson,
                out IReadOnlyList<ContractValidationError> responseErrors));
            Assert.AreEqual(0, responseErrors.Count);
            await LengthPrefixedFrameCodec.WriteAsync(
                pipe,
                responseJson,
                cancellationToken);

            _ = await LengthPrefixedFrameCodec.ReadAsync(pipe, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ProtocolFrameException)
        {
        }
        catch (IOException)
        {
        }
    }
}
