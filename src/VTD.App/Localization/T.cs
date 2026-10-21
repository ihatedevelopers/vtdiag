using System.Windows.Data;
using System.Windows.Markup;

namespace VTD.App.Localization;

/// <summary>
/// <c>{l:T Some.Key}</c>: a one-way binding to <see cref="Loc.Instance"/>'s indexer, so the text follows runtime language
/// switches.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class T : MarkupExtension
{
    /// <summary>Creates the extension.</summary>
    public T()
    {
    }

    /// <summary>Creates the extension.</summary>
    /// <param name="key">String key.</param>
    public T(string key)
    {
        Key = key;
    }

    /// <summary>String key.</summary>
    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    /// <inheritdoc />
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding("[" + Key + "]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}
