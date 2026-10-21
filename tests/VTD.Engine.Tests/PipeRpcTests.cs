using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using StreamJsonRpc;
using VTD.Contracts;
using VTD.Contracts.Fixes;
using VTD.Contracts.Ipc;
using VTD.Contracts.Reporting;
using VTD.Contracts.Sessions;
using VTD.Engine.Ipc;
using VTD.Session;

namespace VTD.Engine.Tests;

/// <summary>
/// The RPC contract against the REAL server: a named pipe created with the production flags and DACL, clients identified
/// from the pipe by the production policy (this test process stands in for the UI or the CLI image), StreamJsonRpc with
/// the strict VTDiag JSON contract, and the real session machine behind it.
/// </summary>
public sealed class PipeRpcTests
{
    [Fact]
    public async Task Ui_and_cli_over_the_real_pipe_follow_the_contract_and_the_command_policy()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var engine = await TestEngine.StartAsync(playerSid: PipeFixture.OwnSid);
        await using var uiServer = PipeFixture.Start(engine.Core, EventOrigin.Ui);
        await using var cliServer = PipeFixture.Start(engine.Core, EventOrigin.Cli);
        var (ui, uiRpc, uiPipe) = await uiServer.ConnectAsync(ct);
        var (cli, cliRpc, cliPipe) = await cliServer.ConnectAsync(ct);
        using (uiRpc)
        using (cliRpc)
        await using (uiPipe)
        await using (cliPipe)
        {
            var uiNotes = new List<EngineNotification>();
            var cliNotes = new List<EngineNotification>();
            ui.Notification += (_, n) => { lock (uiNotes) { uiNotes.Add(n); } };
            cli.Notification += (_, n) => { lock (cliNotes) { cliNotes.Add(n); } };

            // The origin is what the pipe says, whatever the hello claims.
            var uiHello = await ui.HelloAsync(TestEngine.Hello(ClientKind.Cli), ct);
            Assert.True(uiHello.ConsentAllowed);
            Assert.Equal(engine.Core.Runtime.Services.Kb.Version.Version, uiHello.KbVersion);
            var cliHello = await cli.HelloAsync(TestEngine.Hello(ClientKind.Ui, "Claude Code (CLI)"), ct);
            Assert.False(cliHello.ConsentAllowed);
            Assert.Null(cliHello.Session!.PlayerSid);
            Assert.NotNull(uiHello.Session!.PlayerSid);
            Assert.Contains("fixes", cliHello.Capabilities);
            Assert.Equal(EventOrigin.Ui, Assert.Single(uiServer.Decisions).Origin);
            Assert.Equal(EventOrigin.Cli, Assert.Single(cliServer.Decisions).Origin);

            // An agent may scan and read share-level data.
            var scanned = await cli.SendAsync(new ScanRequested { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow() }, ct);
            Assert.Equal(Phase.Analyzed, scanned.Phase);
            var shared = await cli.GetSnapshotAsync(ct);
            Assert.Equal(RedactionLevel.Share, shared!.Meta.Redaction.Level);
            var local = await ui.GetSnapshotAsync(ct);
            Assert.NotEqual(RedactionLevel.Share, local!.Meta.Redaction.Level);
            var proposal = (await cli.GetAnalysisAsync(ct))!.Proposals[0];

            // ...propose a fix: the UI gets the consent request, labelled with the agent...
            var proposed = await cli.SendAsync(new FixSelected { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow(), FixId = proposal.FixId, Binding = proposal.Binding }, ct);
            Assert.Equal(Phase.AwaitingConsent, proposed.Phase);
            Assert.Equal(EventOrigin.Cli, proposed.CurrentProposal!.Origin);
            Assert.Equal("Claude Code (CLI)", proposed.CurrentProposal.Label);
            var previewId = proposed.CurrentPreviewId!;
            Assert.Null((await cli.GetPreviewAsync(previewId, ct))!.ConsentNonce);

            // ...but never consent, restart, undo or answer for the player.
            var forged = new ConsentToken { TokenId = engine.Ids.NewId(), SessionId = proposed.SessionId, PreviewId = previewId, FixId = proposal.FixId, BindingHash = proposal.Binding.ComputeHash(), PreviewHash = "x", Nonce = "guess", IssuedUtc = engine.Clock.GetUtcNow() };
            await AssertRefusedAsync("command.not_allowed", () => cli.SendAsync(new ConsentGiven { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow(), Token = forged }, ct));
            await AssertRefusedAsync("command.not_allowed", () => cli.SendAsync(new RestartNowClicked { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow(), Token = new RestartToken { TokenId = engine.Ids.NewId(), SessionId = proposed.SessionId, Kind = RestartKind.Normal, Nonce = "n", IssuedUtc = engine.Clock.GetUtcNow() } }, ct));
            await AssertRefusedAsync("command.not_allowed", () => cli.SendAsync(new UndoAllRequested { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow() }, ct));
            await AssertRefusedAsync("command.not_allowed", () => cli.PrepareRestartAsync(RestartKind.Normal, ct));
            await AssertRefusedAsync(SessionCodes.ConsentPending, () => cli.SendAsync(new FixSelected { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow(), FixId = "fix.svc.disable_conflict_service", Binding = FixBinding.Empty }, ct));
            Assert.Equal(Phase.AwaitingConsent, (await ui.GetSessionAsync(ct)).Phase);

            // The player clicks in the UI (with the nonce only the UI sees); the agent sees the outcome.
            var preview = (await ui.GetPreviewAsync(previewId, ct))!;
            Assert.NotNull(preview.ConsentNonce);
            var waiting = cli.WaitForOutcomeAsync(previewId, 30, ct);
            var token = new ConsentToken { TokenId = engine.Ids.NewId(), SessionId = proposed.SessionId, PreviewId = previewId, FixId = preview.FixId, BindingHash = preview.BindingHash, PreviewHash = preview.PreviewHash, Nonce = preview.ConsentNonce!, IssuedUtc = engine.Clock.GetUtcNow() };
            var applied = await ui.SendAsync(new ConsentGiven { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow(), Token = token }, ct);
            Assert.Equal(Phase.Verifying, applied.Phase); // the adapter fix has a deadman
            var outcome = await waiting;
            Assert.Equal(AttemptStatus.Verified, outcome!.Status);

            // A replayed token is refused and changes nothing.
            await AssertRefusedAsync(SessionCodes.InvalidPhase, () => ui.SendAsync(new ConsentGiven { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow(), Token = token }, ct));
            var kept = await ui.SendAsync(new DeadmanAnswered { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow(), AttemptId = applied.CurrentAttemptId!, Keep = true }, ct);
            Assert.Equal(Phase.AwaitingOutcome, kept.Phase);

            // Notifications reached both clients; the agent never saw a second-consent nonce or the player's SID.
            await WaitForAsync(() => { lock (uiNotes) { return uiNotes.OfType<ConsentRequestedNotification>().Any(n => n.ProposedBy.Origin == EventOrigin.Cli); } });
            await WaitForAsync(() => { lock (cliNotes) { return cliNotes.OfType<SessionChangedNotification>().Any(n => n.Session.Phase == Phase.AwaitingOutcome); } });
            lock (cliNotes)
            {
                Assert.All(cliNotes.OfType<SessionChangedNotification>(), n => Assert.Null(n.Session.PlayerSid));
                Assert.All(cliNotes.OfType<ConsentRequestedNotification>(), n => Assert.Null(n.SecondConsentNonce));
            }

            // Share-level export through the pipe.
            var export = await cli.ExportAsync(new ExportRequest { Kind = ExportKind.ReportMarkdown }, ct);
            Assert.Equal(ExportStatus.Ok, export.Status);
            Assert.DoesNotContain(PipeFixture.OwnSid, export.TextContent!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Commands_before_hello_and_wrong_protocol_are_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var engine = await TestEngine.StartAsync(playerSid: PipeFixture.OwnSid);
        await using var server = PipeFixture.Start(engine.Core, EventOrigin.Ui);
        var (proxy, rpc, pipe) = await server.ConnectAsync(ct);
        using (rpc)
        await using (pipe)
        {
            await AssertRefusedAsync("hello.required", () => proxy.GetSessionAsync(ct));
            await AssertRefusedAsync("protocol.mismatch", () => proxy.HelloAsync(TestEngine.Hello() with { ProtocolVersion = 99 }, ct));
            await proxy.HelloAsync(TestEngine.Hello(), ct);
            Assert.Equal(Phase.Intake, (await proxy.GetSessionAsync(ct)).Phase);
        }
    }

    [Fact]
    public async Task A_ui_image_running_as_another_user_is_refused_before_any_rpc()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var engine = await TestEngine.StartAsync();
        await using var server = PipeFixture.Start(engine.Core, EventOrigin.Ui, playerSid: "S-1-5-21-1-2-3-1001");
        // Unelevated, the pipe DACL (only the player SID, Administrators, SYSTEM) already refuses the connect; elevated
        // (Administrators are allowed) the identity check refuses before any RPC.
        try
        {
            var (proxy, rpc, pipe) = await server.ConnectAsync(ct);
            using (rpc)
            await using (pipe)
            {
                await Assert.ThrowsAnyAsync<Exception>(() => proxy.HelloAsync(TestEngine.Hello(), ct));
                Assert.Equal("client.not_player", Assert.Single(server.Decisions).RefusalCode);
            }
        }
        catch (UnauthorizedAccessException)
        {
            Assert.Empty(server.Decisions);
        }
    }

    [Fact]
    public async Task An_image_outside_the_install_directory_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var engine = await TestEngine.StartAsync(playerSid: PipeFixture.OwnSid);
        await using var server = PipeFixture.Start(engine.Core, EventOrigin.Cli, installDirectory: Path.Combine(Path.GetTempPath(), "not-vtdiag"));
        var (proxy, rpc, pipe) = await server.ConnectAsync(ct);
        using (rpc)
        await using (pipe)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => proxy.HelloAsync(TestEngine.Hello(), ct));
            Assert.Equal("client.wrong_location", Assert.Single(server.Decisions).RefusalCode);
        }
    }

    [Fact]
    public async Task The_pipe_name_cannot_be_squatted_or_created_twice()
    {
        var name = EngineProtocol.PipeNamePrefix + "squat." + Guid.NewGuid().ToString("N");
        var security = PipeServer.CreateSecurity(PipeFixture.OwnSid, limitedEngine: false);

        // Somebody created the name first: FILE_FLAG_FIRST_PIPE_INSTANCE makes our create fail instead of joining it.
        await using (var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        {
            await using var server = new PipeServer(name, security, _ => throw new InvalidOperationException(), _ => throw new InvalidOperationException());
            var ex = Assert.Throws<IOException>(server.Start);
            Assert.Equal("pipe.create_failed", ex.Message);
        }

        // Our own second first-instance fails too.
        await using var first = new PipeServer(name, security, _ => throw new InvalidOperationException(), _ => throw new InvalidOperationException());
        first.Start();
        await using var second = new PipeServer(name, security, _ => throw new InvalidOperationException(), _ => throw new InvalidOperationException());
        Assert.Throws<IOException>(second.Start);
    }

    [Fact]
    public async Task The_pipe_dacl_grants_only_system_admins_and_the_player()
    {
        var name = EngineProtocol.PipeNamePrefix + "dacl." + Guid.NewGuid().ToString("N");
        var player = PipeFixture.OwnSid;
        await using var server = new PipeServer(name, PipeServer.CreateSecurity(player, limitedEngine: false), _ => throw new InvalidOperationException(), _ => throw new InvalidOperationException());
        await using var instance = server.CreateInstance(first: true);
        var rules = instance.GetAccessControl().GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        var allowed = rules.Where(r => r.AccessControlType == AccessControlType.Allow).Select(r => ((SecurityIdentifier)r.IdentityReference).Value).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "S-1-5-18", "S-1-5-32-544", player }.Order(StringComparer.Ordinal), allowed);
        var mine = rules.Single(r => string.Equals(((SecurityIdentifier)r.IdentityReference).Value, player, StringComparison.Ordinal) && r.AccessControlType == AccessControlType.Allow);
        Assert.Equal(0, (int)(mine.PipeAccessRights & PipeAccessRights.CreateNewInstance));
        Assert.Equal(0, (int)(mine.PipeAccessRights & (PipeAccessRights.ChangePermissions | PipeAccessRights.TakeOwnership)));
        Assert.Contains(rules, r => r.AccessControlType == AccessControlType.Deny && ((SecurityIdentifier)r.IdentityReference).IsWellKnown(WellKnownSidType.NetworkSid));
        Assert.True(instance.GetAccessControl().AreAccessRulesProtected);
    }

    [Fact]
    public async Task The_server_identifies_this_process_from_the_pipe()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var engine = await TestEngine.StartAsync(playerSid: PipeFixture.OwnSid);
        await using var server = PipeFixture.Start(engine.Core, EventOrigin.Mcp);
        var (proxy, rpc, pipe) = await server.ConnectAsync(ct);
        using (rpc)
        await using (pipe)
        {
            await proxy.HelloAsync(TestEngine.Hello(ClientKind.Mcp), ct);
            var decision = Assert.Single(server.Decisions);
            Assert.Equal(Environment.ProcessId, decision.ProcessId);
            Assert.Equal(Path.GetFullPath(Environment.ProcessPath!), Path.GetFullPath(decision.ImagePath!), ignoreCase: true);
            Assert.Equal(PipeFixture.OwnSid, decision.UserSid);
            Assert.Equal(EventOrigin.Mcp, decision.Origin);
            Assert.Equal(Environment.ProcessId, PipeClientVerifier.ServerProcessId(pipe.SafePipeHandle));
        }
    }

    internal static async Task AssertRefusedAsync(string code, Func<Task> call)
    {
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(call);
        Assert.Equal(code, ex.Message);
        Assert.Equal(RpcSurface.RefusedErrorCode, ex.ErrorCode);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "Condition not met within 5 s.");
    }
}
