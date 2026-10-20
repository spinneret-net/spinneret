using System.Reflection;

namespace Spinneret.View;

/// <summary>Configuration for AddMvvm.</summary>
public sealed class MvvmOptions
{
    /// <summary>Assemblies scanned for views and view models; the entry assembly when empty.</summary>
    public IList<Assembly> Assemblies { get; } = [];

    /// <summary>Registers every concrete <c>IViewModel</c> in the scanned assemblies as transient. Default: true.</summary>
    public bool AutoRegisterViewModels { get; set; } = true;
}
