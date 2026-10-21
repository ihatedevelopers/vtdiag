using System.ComponentModel;
using System.Windows.Controls;
using VTD.App.ViewModels;

namespace VTD.App.Views;

/// <summary>The shell: route rail, banners, the current page.</summary>
public partial class ShellView : UserControl
{
    /// <summary>Creates the view.</summary>
    public ShellView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged old)
            {
                old.PropertyChanged -= OnShellChanged;
            }

            if (e.NewValue is INotifyPropertyChanged next)
            {
                next.PropertyChanged += OnShellChanged;
            }
        };
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Page))
        {
            Scroller.ScrollToTop();
        }
    }
}
