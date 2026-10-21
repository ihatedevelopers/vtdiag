using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace VTD.App.Mvvm;

/// <summary>Minimal INotifyPropertyChanged base.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Sets a field and raises <see cref="PropertyChanged"/> when it changed.</summary>
    /// <typeparam name="T">Field type.</typeparam>
    /// <param name="field">Field.</param>
    /// <param name="value">New value.</param>
    /// <param name="name">Property name.</param>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        return true;
    }

    /// <summary>Raises <see cref="PropertyChanged"/>.</summary>
    /// <param name="name">Property name (null or empty = all).</param>
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A synchronous command.</summary>
/// <param name="execute">Action.</param>
/// <param name="canExecute">Guard.</param>
public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    /// <summary>Creates a parameterless command.</summary>
    /// <param name="execute">Action.</param>
    /// <param name="canExecute">Guard.</param>
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    /// <inheritdoc />
    public void Execute(object? parameter) => execute(parameter);

    /// <summary>Re-evaluates <see cref="CanExecute"/>.</summary>
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// An async command that disables itself while running and routes failures to <see cref="Failed"/> (the shell shows a
/// calm error line instead of crashing).
/// </summary>
public sealed class AsyncCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private bool _running;

    /// <summary>Creates the command.</summary>
    /// <param name="execute">Action.</param>
    /// <param name="canExecute">Guard.</param>
    public AsyncCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <summary>Creates a parameterless command.</summary>
    /// <param name="execute">Action.</param>
    /// <param name="canExecute">Guard.</param>
    public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    /// <summary>Raised when a run throws (global sink set by the shell).</summary>
    public static event EventHandler<Exception>? Failed;

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <summary>True while running.</summary>
    public bool IsRunning => _running;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke(parameter) ?? true);

    /// <inheritdoc />
    public async void Execute(object? parameter) => await ExecuteAsync(parameter).ConfigureAwait(true);

    /// <summary>Runs the command and awaits it (tests use this).</summary>
    /// <param name="parameter">Parameter.</param>
    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _running = true;
        Refresh();
        try
        {
            await _execute(parameter).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // A UI command must never crash the app; the failure is shown to the player.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Failed?.Invoke(this, ex);
        }
        finally
        {
            _running = false;
            Refresh();
        }
    }

    /// <summary>Re-evaluates <see cref="CanExecute"/>.</summary>
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
