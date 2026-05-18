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
    /// Per-process salt mixed into the SHA-256 hash that opacifies
    /// every <c>EndClientId</c> before it lands in the exported XML
    /// (LGPD §11 — pseudonymisation of personal data). The salt is
    /// further mixed with <c>{firmId}|{reportDate}</c> at hashing
    /// time so the same end-client maps to the same opaqued id
    /// within a single firm-day report but to a different id across
    /// firms or days — which prevents trivial cross-report
    /// correlation by an adversary that obtained two reports.
    ///
    /// <para>Defaults to a build-time constant. Operators MAY (and in
    /// production SHOULD) override via configuration with a long,
    /// random secret to harden the opacification — the default is
    /// adequate for tests and is documented here so the failure
    /// mode is obvious (a leaked default reveals nothing the
    /// regulator-facing XML didn't already expose, but rotating it
    /// invalidates cross-day owner linkability).</para>
    /// </summary>
    public string OwnerHashSalt { get; set; } = "b3-trading-platform/cvm-35-505/v1";
}
