namespace QuotaDeck.Core.Tests;

using QuotaDeck.Core.Readers;
using QuotaDeck.Core.Settings;

/// <summary>
/// Mirrors Codenotch <c>Tests/ProviderOrderTests.swift</c> through the discovery scan: every valid
/// module is found, the stored order is a preference to reconcile, unknown ids are ignored and
/// modules the order predates are still shown.
/// </summary>
public class ProviderModulesTests
{
    [Fact]
    public void DiscoversEveryValidModuleAlphabetically()
    {
        var modules = ProviderModules.Discover(typeof(ProviderModulesTests).Assembly, order: null);

        Assert.Equal(new[] { "alpha", "mid", "zed" }, modules.Select(module => module.ProviderId));

        // The abstract base and the stub that needs constructor arguments never appear.
        Assert.DoesNotContain(modules, module => module.ProviderId is "abstract" or "needs-args");
    }

    [Fact]
    public void TheStoredOrderWinsAndUnknownIdsAreDroppedAndNewModulesAppended()
    {
        var modules = ProviderModules.Discover(typeof(ProviderModulesTests).Assembly, order: ["zed", "ghost"]);

        Assert.Equal(new[] { "zed", "alpha", "mid" }, modules.Select(module => module.ProviderId));
    }

    [Fact]
    public void AnEmptyOrderKeepsTheAlphabeticalOrder()
    {
        var modules = ProviderModules.Discover(typeof(ProviderModulesTests).Assembly, order: []);

        Assert.Equal(new[] { "alpha", "mid", "zed" }, modules.Select(module => module.ProviderId));
    }

    private static ProviderPalette Palette => new(1, 2, 3);

    public sealed class AlphaModule : IProviderModule
    {
        public string ProviderId => "alpha";

        public string DisplayName => "Alpha";

        public ProviderPalette Palette => ProviderModulesTests.Palette;

        public IUsageReader CreateReader(ReaderContext context) => throw new NotSupportedException();

        public bool IsPresent(ReaderContext context) => false;
    }

    public sealed class MidModule : IProviderModule
    {
        public string ProviderId => "mid";

        public string DisplayName => "Mid";

        public ProviderPalette Palette => ProviderModulesTests.Palette;

        public IUsageReader CreateReader(ReaderContext context) => throw new NotSupportedException();

        public bool IsPresent(ReaderContext context) => false;
    }

    public sealed class ZedModule : IProviderModule
    {
        public string ProviderId => "zed";

        public string DisplayName => "Zed";

        public ProviderPalette Palette => ProviderModulesTests.Palette;

        public IUsageReader CreateReader(ReaderContext context) => throw new NotSupportedException();

        public bool IsPresent(ReaderContext context) => false;
    }

    /// <summary>Abstract modules are not providers.</summary>
    public abstract class AbstractModule : IProviderModule
    {
        public abstract string ProviderId { get; }

        public abstract string DisplayName { get; }

        public abstract ProviderPalette Palette { get; }

        public IUsageReader CreateReader(ReaderContext context) => throw new NotSupportedException();

        public bool IsPresent(ReaderContext context) => false;
    }

    /// <summary>No parameterless constructor: nothing to instantiate at discovery time.</summary>
    public sealed class NeedsArgumentsModule : IProviderModule
    {
        public NeedsArgumentsModule(string argument) => this.Argument = argument;

        public string Argument { get; }

        public string ProviderId => "needs-args";

        public string DisplayName => "Needs args";

        public ProviderPalette Palette => ProviderModulesTests.Palette;

        public IUsageReader CreateReader(ReaderContext context) => throw new NotSupportedException();

        public bool IsPresent(ReaderContext context) => false;
    }
}
