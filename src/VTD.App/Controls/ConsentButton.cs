using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace VTD.App.Controls;

/// <summary>
/// The button that mints consent ("Fix it", "Yes, do it anyway", "Restart now", "Yes, keep it"). Residual-risk hardening
/// from the ConsentToken contract: a same-user process could try to drive the UI, so this button
/// <list type="bullet">
/// <item>arms only after <see cref="ArmDelay"/> of being visible (no click-through from a dialog that just popped up);</item>
/// <item>ignores UI Automation (its automation peer exposes no Invoke pattern) and programmatic clicks: the command runs
/// only after a real pointer press-and-release on it, or a real Space/Enter key press while it has keyboard focus;</item>
/// <item>never takes the default/cancel role (Enter on the window can't consent).</item>
/// </list>
/// Synthesized hardware input (SendInput) can't be told apart without a low-level hook, which the UI layer must not use.
/// </summary>
public sealed class ConsentButton : Button
{
    /// <summary>Arm delay.</summary>
    public static readonly DependencyProperty ArmDelayProperty = DependencyProperty.Register(
        nameof(ArmDelay), typeof(TimeSpan), typeof(ConsentButton), new PropertyMetadata(TimeSpan.FromMilliseconds(700)));

    /// <summary>True once armed.</summary>
    public static readonly DependencyProperty IsArmedProperty = DependencyProperty.Register(
        nameof(IsArmed), typeof(bool), typeof(ConsentButton), new PropertyMetadata(false));

    private readonly DispatcherTimer _armTimer;
    private bool _pointerDownHere;
    private bool _realInput;

    static ConsentButton()
    {
        IsDefaultProperty.OverrideMetadata(typeof(ConsentButton), new FrameworkPropertyMetadata(false, null, (_, _) => false));
        IsCancelProperty.OverrideMetadata(typeof(ConsentButton), new FrameworkPropertyMetadata(false, null, (_, _) => false));
    }

    /// <summary>Creates the button.</summary>
    public ConsentButton()
    {
        _armTimer = new DispatcherTimer { Interval = ArmDelay };
        _armTimer.Tick += (_, _) =>
        {
            _armTimer.Stop();
            IsArmed = true;
        };
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                IsArmed = false;
                _armTimer.Interval = ArmDelay;
                _armTimer.Start();
            }
            else
            {
                _armTimer.Stop();
                IsArmed = false;
            }
        };
    }

    /// <summary>How long the button must be visible before it accepts a click.</summary>
    public TimeSpan ArmDelay
    {
        get => (TimeSpan)GetValue(ArmDelayProperty);
        set => SetValue(ArmDelayProperty, value);
    }

    /// <summary>Armed (styles dim the button until then).</summary>
    public bool IsArmed
    {
        get => (bool)GetValue(IsArmedProperty);
        private set => SetValue(IsArmedProperty, value);
    }

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new ConsentButtonAutomationPeer(this);

    /// <inheritdoc />
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        _pointerDownHere = e.Device is MouseDevice && IsArmed;
        base.OnPreviewMouseLeftButtonDown(e);
    }

    /// <inheritdoc />
    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        _realInput = _pointerDownHere && IsMouseOver && IsArmed;
        _pointerDownHere = false;
        base.OnPreviewMouseLeftButtonUp(e);
    }

    /// <inheritdoc />
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Key is Key.Space or Key.Enter && e.KeyboardDevice is not null && IsKeyboardFocused && IsArmed && !e.IsRepeat)
        {
            _realInput = true;
        }

        base.OnPreviewKeyDown(e);
    }

    /// <inheritdoc />
    protected override void OnClick()
    {
        var real = _realInput;
        _realInput = false;
        if (!real || !IsArmed)
        {
            return;
        }

        base.OnClick();
    }

    private sealed class ConsentButtonAutomationPeer(ConsentButton owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

        protected override string GetClassNameCore() => nameof(ConsentButton);

        protected override string GetNameCore() => (Owner as ContentControl)?.Content as string ?? base.GetNameCore();

        // No Invoke pattern: screen readers still read it, but automation can't press it.
        public override object? GetPattern(PatternInterface patternInterface) => null;
    }
}
