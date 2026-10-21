using VTD.App.Localization;
using VTD.App.Mvvm;
using VTD.App.Services;

namespace VTD.App.ViewModels;

/// <summary>Base of every screen.</summary>
/// <param name="shell">Shell.</param>
public abstract class PageViewModel(ShellViewModel shell) : ObservableObject
{
    /// <summary>Shell.</summary>
    public ShellViewModel Shell { get; } = shell ?? throw new ArgumentNullException(nameof(shell));

    /// <summary>Services.</summary>
    public AppServices S => Shell.Services;

    /// <summary>UI strings.</summary>
    protected static Loc L => Loc.Instance;

    /// <summary>Where this screen sits in the flow.</summary>
    public virtual FlowStep Step => FlowStep.None;

    /// <summary>Called when the page becomes visible.</summary>
    public virtual Task OnShownAsync()
    {
        Refresh();
        return Task.CompletedTask;
    }

    /// <summary>Rebuilds computed text (after a language switch or new engine data).</summary>
    public virtual void Refresh() => Raise(string.Empty);
}
