namespace QuotaDeck.Core.Model;

/// <summary>
/// How much to trust a provider's numbers. The faces never present a derived or manual figure as
/// if a vendor had published it.
///
/// Port of Codenotch <c>Sources/Model/UsageModel.swift</c> (<c>enum Fidelity</c> and its
/// <c>qualifier</c>).
/// </summary>
public enum Fidelity
{
    Official,
    Derived,
    Manual,
}

public static class FidelityExtensions
{
    /// <summary>Prefix shown in front of a percentage that was worked out rather than published.</summary>
    public static string Qualifier(this Fidelity fidelity) => fidelity == Fidelity.Official ? "" : "~";
}
