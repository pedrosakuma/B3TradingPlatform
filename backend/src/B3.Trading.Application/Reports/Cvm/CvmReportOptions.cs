namespace B3.Trading.Application.Reports.Cvm;

/// <summary>
/// Q4.8 (#308). Tunables for the CVM 35/505 transaction-reporting
/// export pipeline. Bound from <c>Trading:Reports:Cvm</c>. The export
/// is rebuilt from the WAL on every request (CVM regulator-side
/// retention is satisfied implicitly by WAL segment retention — we
/// do NOT persist generated XML to disk), so no in-memory cap is
/// needed; the options here govern the LGPD opacification seed +
/// future segment-level scan tuning.
/// </summary>
public sealed class CvmReportOptions
{
    public const string SectionName = "Trading:Reports:Cvm";

    /// <summary>
    /// Sentinel value used by tests / dev. <see cref="Validate"/>
    /// rejects this in non-Test environments so a missing operator
    /// configuration fails fast at startup rather than shipping an
    /// effectively-unsalted hash to the regulator-facing XML.
    /// </summary>
    public const string TestOnlySalt = "test-only-cvm-salt-DO-NOT-USE-IN-PRODUCTION";

    /// <summary>
    /// Per-process salt mixed into the SHA-256 hash that opacifies
    /// every <c>EndClientId</c> before it lands in the exported XML
    /// (LGPD §11 — pseudonymisation of personal data). The salt is
    /// further mixed with <c>{firmId}|{reportDate}</c> at hashing
    /// time so the same end-client maps to the same opaqued id
    /// within a single firm-day report but to a different id across
    /// firms or days — which prevents trivial cross-report
    /// correlation by an adversary that obtained two reports.
    ///
    /// <para>Pass-1 review (#325) P1. No default — operators MUST set
    /// a long, random secret via <c>Trading:Reports:Cvm:OwnerHashSalt</c>
    /// (file/env/secret-store). <see cref="Validate"/> is invoked at
    /// startup outside Test environments and fails the host if this
    /// is missing, empty, or equal to <see cref="TestOnlySalt"/>.
    /// Tests bind <see cref="TestOnlySalt"/> explicitly so the unit
    /// path stays deterministic without weakening production.</para>
    /// </summary>
    public string OwnerHashSalt { get; set; } = string.Empty;

    /// <summary>
    /// Validates the bound options. Call from composition once the
    /// host environment is known. Mirrors <c>AuthSigningKeyValidator</c>:
    /// the salt is required in every environment (empty fails fast,
    /// even in Development, so the failure mode is obvious during the
    /// dev loop), and the <see cref="TestOnlySalt"/> sentinel is only
    /// accepted in Development (where the integration test host
    /// boots). Throws <see cref="InvalidOperationException"/> with a
    /// clear remediation message on either failure.
    /// </summary>
    public void Validate(string environmentName)
    {
        if (string.IsNullOrWhiteSpace(OwnerHashSalt))
        {
            throw new InvalidOperationException(
                "Trading:Reports:Cvm:OwnerHashSalt is required. Configure a long, random secret " +
                "via environment/secret-store (e.g. Trading__Reports__Cvm__OwnerHashSalt). " +
                "Tests may use CvmReportOptions.TestOnlySalt explicitly.");
        }
        var isDev = string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);
        if (!isDev && string.Equals(OwnerHashSalt, TestOnlySalt, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Trading:Reports:Cvm:OwnerHashSalt is set to the TestOnlySalt sentinel outside Development. " +
                "Configure a real secret before starting the host.");
        }
    }
}
