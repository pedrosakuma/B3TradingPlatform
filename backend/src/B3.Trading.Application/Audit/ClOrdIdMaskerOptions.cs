namespace B3.Trading.Application.Audit;

/// <summary>
/// #435 Part B. Configuration for <see cref="ClOrdIdMasker"/>. Salt
/// MUST be set in non-Development environments — the masker ctor
/// throws on construction otherwise, matching the boot-guard
/// convention used by <c>CvmReportWriter</c> for <c>OwnerHashSalt</c>.
/// Bound from <c>Trading:DropCopy</c>.
/// </summary>
public sealed class ClOrdIdMaskerOptions
{
    /// <summary>
    /// Secret-grade entropy mixed into every mask. Rotate operationally
    /// to retire correlations on a known cadence (the per-UTC-day
    /// rotation done by the masker is independent of this and provides
    /// the daily unlinkability — the salt is the global key).
    /// </summary>
    public string? ClOrdIdMaskSalt { get; set; }

    /// <summary>
    /// Test-only fixed salt — used by the API <c>TestAppFactory</c>
    /// and unit tests so deterministic mask outputs can be asserted.
    /// MUST NOT be used in real environments.
    /// </summary>
    public const string TestOnlySalt = "TEST-ONLY-CLORDID-MASK-SALT-DO-NOT-USE-IN-PROD";
}
