using System.Text;
using VTD.Contracts.Ipc;
using VTD.Engine.Cli;

namespace VTD.Engine;

/// <summary>
/// <c>VTDiag.Engine.exe</c>: the only writer (headless; elevated, or <c>--limited</c>), serving the App over a named pipe;
/// <c>--resume &lt;id&gt;</c> from the logon task; <c>--uninstall</c> for the uninstaller; and the CLI
/// (<c>--cli scan|analyze|export|status|fix preview|fix propose|undo propose</c>), a pure client of the same surface.
/// Refuses to run elevated unless its own image is under <c>%ProgramFiles%</c>.
/// </summary>
public static class Program
{
    /// <summary>Entry point.</summary>
    /// <param name="args">Arguments.</param>
    public static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        await using var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };
        await using var stderr = new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true };

        if (EngineHost.IsElevated() && !EngineHost.IsUnderProgramFiles(Environment.ProcessPath))
        {
            await stderr.WriteLineAsync("engine.not_in_program_files: VTDiag.Engine refuses to run elevated outside Program Files.").ConfigureAwait(false);
            return EngineHost.ExitRefused;
        }

        if (args.Length > 0 && string.Equals(args[0], "--cli", StringComparison.Ordinal))
        {
            using var cancel = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cancel.Cancel();
            };
            try
            {
                return await CliApp.RunAsync(args[1..], stdout, stderr, cancellationToken: cancel.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return (int)CliExitCode.Error;
            }
        }

        var parsed = EngineArguments.Parse(args, out var error);
        if (parsed is null)
        {
            await stderr.WriteLineAsync((error ?? "args.invalid") + "\n" + CliApp.Usage).ConfigureAwait(false);
            return (int)CliExitCode.Usage;
        }

        return await EngineHost.RunAsync(parsed, stderr).ConfigureAwait(false);
    }
}
