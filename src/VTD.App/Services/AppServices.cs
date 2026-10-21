using VTD.App.Localization;
using VTD.Contracts;
using VTD.Contracts.Knowledge;

namespace VTD.App.Services;

/// <summary>Everything a view model needs from the outside world, in one place (tests build it with fakes).</summary>
public sealed class AppServices
{
    /// <summary>Creates the services.</summary>
    /// <param name="kb">KB used for display text (the engine pins the same version for the session).</param>
    /// <param name="connector">Engine connector.</param>
    /// <param name="io">Clipboard, dialogs, links.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="ids">Ids (consent and restart tokens).</param>
    /// <param name="loc">UI strings.</param>
    public AppServices(IKnowledgeBase kb, IEngineConnector connector, IUserIo io, TimeProvider clock, IIdGenerator ids, Loc loc)
    {
        Kb = kb ?? throw new ArgumentNullException(nameof(kb));
        Connector = connector ?? throw new ArgumentNullException(nameof(connector));
        Io = io ?? throw new ArgumentNullException(nameof(io));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Ids = ids ?? throw new ArgumentNullException(nameof(ids));
        Loc = loc ?? throw new ArgumentNullException(nameof(loc));
        Text = new KbText(kb, loc);
    }

    /// <summary>App version shown on About and sent in the hello.</summary>
    public string AppVersion { get; init; } = typeof(AppServices).Assembly.GetName().Version?.ToString(3) ?? "0.0.1";

    /// <summary>KB.</summary>
    public IKnowledgeBase Kb { get; }

    /// <summary>KB text with fallbacks.</summary>
    public KbText Text { get; }

    /// <summary>Engine connector.</summary>
    public IEngineConnector Connector { get; }

    /// <summary>Clipboard, dialogs, links.</summary>
    public IUserIo Io { get; }

    /// <summary>Clock.</summary>
    public TimeProvider Clock { get; }

    /// <summary>Ids.</summary>
    public IIdGenerator Ids { get; }

    /// <summary>UI strings.</summary>
    public Loc Loc { get; }

    /// <summary>The UI log (handed to the engine, which redacts and stores it).</summary>
    public AppLog Log { get; init; } = AppLog.Null;

    /// <summary>Show developer switches (pseudo-localization, demo simulations).</summary>
    public bool DeveloperMode { get; init; }
}
