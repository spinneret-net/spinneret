using System.Reflection;

namespace Spinneret.View;

/// <summary>
/// Configuration for AddMvvm. New knobs are added as optional properties so existing
/// registrations keep compiling.
/// </summary>
public sealed class MvvmOptions
{
    /// <summary>
    /// Assemblies scanned for views (to build the view-model→view map) and, when
    /// <see cref="AutoRegisterViewModels"/> is on, for view models. Defaults to the entry
    /// assembly when left empty.
    /// </summary>
    public IList<Assembly> Assemblies { get; } = [];

    /// <summary>
    /// Registers every concrete <c>IViewModel</c> implementation in the scanned assemblies
    /// as a transient service. Default: true.
    /// </summary>
    public bool AutoRegisterViewModels { get; set; } = true;
}
