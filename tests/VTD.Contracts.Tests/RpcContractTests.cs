using StreamJsonRpc;
using VTD.Contracts.Fixes;
using VTD.Contracts.Ipc;
using VTD.Contracts.Serialization;
using VTD.Contracts.Sessions;
using VTD.Testing.Fakes;

namespace VTD.Contracts.Tests;

/// <summary>
/// <see cref="IEngineRpc"/> works over StreamJsonRpc with <see cref="SystemTextJsonFormatter"/> and the strict VTDiag JSON
/// contract (polymorphic commands, notifications, cancellation tokens). This is the wire A4 (server) and A6 (client) share.
/// </summary>
public sealed class RpcContractTests
{
    [Fact]
    public async Task Engine_rpc_round_trips_over_streamjsonrpc()
    {
        var ct = TestContext.Current.CancellationToken;
        var (serverStream, clientStream) = Nerdbank.Streams.FullDuplexStream.CreatePair();
        var engine = new FakeEngineRpc(Fixtures.RealtekStutter);

        using var server = new JsonRpc(new HeaderDelimitedMessageHandler(serverStream, serverStream, new SystemTextJsonFormatter { JsonSerializerOptions = VtdJson.CreateRpcOptions() }));
        server.AddLocalRpcTarget<IEngineRpc>(engine, null);
        server.StartListening();

        using var client = new JsonRpc(new HeaderDelimitedMessageHandler(clientStream, clientStream, new SystemTextJsonFormatter { JsonSerializerOptions = VtdJson.CreateRpcOptions() }));
        var proxy = client.Attach<IEngineRpc>();
        var notifications = new List<EngineNotification>();
        proxy.Notification += (_, n) =>
        {
            lock (notifications)
            {
                notifications.Add(n);
            }
        };
        client.StartListening();

        var hello = await proxy.HelloAsync(new ClientHello { ProtocolVersion = EngineProtocol.Version, AppVersion = "0.1.0", Language = Languages.Turkish }, ct);
        Assert.Equal(PrivilegeMode.Elevated, hello.Privilege);
        Assert.True(hello.ConsentAllowed);
        Assert.Equal(Phase.Intake, hello.Session!.Phase);

        var scanned = await proxy.SendAsync(new ScanRequested { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow() }, ct);
        Assert.Equal(Phase.Analyzed, scanned.Phase);

        var snapshot = await proxy.GetSnapshotAsync(ct);
        Assert.Equal(FixtureSnapshots.RealtekId, snapshot!.Meta.SnapshotId);

        var analysis = await proxy.GetAnalysisAsync(ct);
        var proposal = Assert.Single(analysis!.Proposals);
        Assert.Equal("fix.net.disable_unused_adapter", proposal.FixId);

        var preview = await proxy.PreviewFixAsync(proposal.FixId, proposal.Binding, ct);
        Assert.Equal(FixtureSnapshots.RealtekAdapterInstanceId, preview.Steps[0].Args["instanceId"]);
        Assert.Equal(proposal.Binding.ComputeHash(), preview.BindingHash);
        Assert.True(preview.CanApply);

        await WaitForAsync(() =>
        {
            lock (notifications)
            {
                return notifications.OfType<ScanProgressNotification>().Any() && notifications.OfType<SessionChangedNotification>().Any(n => n.Session.Phase == Phase.Analyzed);
            }
        });

        ((IDisposable)proxy).Dispose();
    }

    [Fact]
    public async Task An_agent_can_propose_but_never_consent()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = new FakeEngineRpc(Fixtures.RealtekStutter) { ClientOrigin = EventOrigin.Mcp };
        var requests = new List<ConsentRequestedNotification>();
        engine.Notification += (_, n) =>
        {
            if (n is ConsentRequestedNotification c)
            {
                requests.Add(c);
            }
        };

        await engine.HelloAsync(new ClientHello { ProtocolVersion = EngineProtocol.Version, AppVersion = "0.1.0", Language = Languages.English, Kind = ClientKind.Mcp, Label = "Claude Code (MCP)" }, ct);
        await engine.SendAsync(new ScanRequested { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow() }, ct);
        var proposal = (await engine.GetAnalysisAsync(ct))!.Proposals[0];

        // Propose: allowed, pops the consent dialog in the UI, previews carry no nonce for the agent.
        var state = await engine.SendAsync(new FixSelected { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow(), FixId = proposal.FixId, Binding = proposal.Binding }, ct);
        Assert.Equal(Phase.AwaitingConsent, state.Phase);
        Assert.Equal(EventOrigin.Mcp, state.CurrentProposal!.Origin);
        var request = Assert.Single(requests);
        Assert.Equal("Claude Code (MCP)", request.ProposedBy.Label);
        var agentView = await engine.PreviewFixAsync(proposal.FixId, proposal.Binding, ct);
        Assert.Null(agentView.ConsentNonce);

        // Self-consent, deadman keep, undo and restart are refused for the agent.
        var forged = new ConsentToken { TokenId = engine.Ids.NewId(), SessionId = engine.FixRunner.SessionId, PreviewId = state.CurrentPreviewId!, FixId = proposal.FixId, BindingHash = agentView.BindingHash, PreviewHash = agentView.PreviewHash, Nonce = "guess", IssuedUtc = engine.Clock.GetUtcNow() };
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SendAsync(new ConsentGiven { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow(), Token = forged }, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SendAsync(new UndoAllRequested { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow() }, ct));
        Assert.Null(await engine.WaitForOutcomeAsync(state.CurrentPreviewId!, 30, ct));

        // The human clicks in the UI: the agent then sees the outcome.
        engine.ClientOrigin = EventOrigin.Ui;
        var uiPreview = await engine.FixRunner.PreviewAsync(proposal.FixId, proposal.Binding, new Proposer { Origin = EventOrigin.Mcp }, ct);
        await engine.SendAsync(new ConsentGiven { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow(), Token = Token(engine, uiPreview) }, ct);
        var outcome = await engine.WaitForOutcomeAsync(uiPreview.PreviewId, 30, ct);
        Assert.Equal(AttemptStatus.Verified, outcome!.Status);
    }

    [Fact]
    public void Command_policy_keeps_writes_ui_only()
    {
        SessionCommand consent = new ConsentGiven { EventId = "e", Utc = FakeClock.DefaultStart, Token = new ConsentToken { TokenId = "t", SessionId = "s", PreviewId = "p", FixId = "f", BindingHash = "b", PreviewHash = "h", Nonce = "n", IssuedUtc = FakeClock.DefaultStart } };
        SessionCommand propose = new FixSelected { EventId = "e", Utc = FakeClock.DefaultStart, FixId = "f", Binding = FixBinding.Empty };
        Assert.True(CommandPolicy.IsAllowed(consent, EventOrigin.Ui));
        Assert.False(CommandPolicy.IsAllowed(consent, EventOrigin.Cli));
        Assert.False(CommandPolicy.IsAllowed(consent, EventOrigin.Mcp));
        Assert.False(CommandPolicy.IsAllowed(consent, EventOrigin.Unspecified));
        Assert.True(CommandPolicy.IsAllowed(propose, EventOrigin.Mcp));
        Assert.False(CommandPolicy.IsAllowed(propose, EventOrigin.Unspecified));
    }

    internal static ConsentToken Token(FakeEngineRpc engine, FixPreview preview, string? secondNonce = null) => new()
    {
        TokenId = engine.Ids.NewId(),
        SessionId = engine.FixRunner.SessionId,
        PreviewId = preview.PreviewId,
        FixId = preview.FixId,
        BindingHash = preview.BindingHash,
        PreviewHash = preview.PreviewHash,
        Nonce = preview.ConsentNonce!,
        IssuedUtc = engine.Clock.GetUtcNow(),
        SecondConsentNonce = secondNonce,
    };

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "Condition not met within 5 s.");
    }
}
