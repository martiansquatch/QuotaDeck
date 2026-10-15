namespace QuotaDeck.Core.Readers;

using System.Reflection;
using QuotaDeck.Core.Settings;

/// <summary>
/// Finds the provider modules compiled into an assembly.
///
/// The plugin has no registration file: a module is a public, non-abstract class implementing
/// <see cref="IProviderModule"/> with a parameterless constructor, and the scan is how the set of
/// providers grows without editing a list. The stored order is a preference to reconcile, never an
/// authority — <see cref="ProviderOrder.Arrange"/> drops ids no module claims and appends modules
/// the order has never seen, alphabetically.
/// </summary>
public static class ProviderModules
{
    public static List<IProviderModule> Discover(Assembly assembly, IReadOnlyList<string>? order = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var modules = assembly.GetTypes()
            .Where(IsModule)
            .Select(type => (IProviderModule)Activator.CreateInstance(type)!)
            .OrderBy(module => module.ProviderId, StringComparer.Ordinal)
            .ToList();

        return order is { Count: > 0 }
            ? ProviderOrder.Arrange(modules, order, module => module.ProviderId)
            : modules;
    }

    private static bool IsModule(Type type) =>
        type is { IsClass: true, IsAbstract: false, IsVisible: true }
        && typeof(IProviderModule).IsAssignableFrom(type)
        && type.GetConstructor(Type.EmptyTypes) is not null;
}
