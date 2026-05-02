using Microsoft.Extensions.Options;

namespace B3.Trading.Infrastructure;

/// <summary>
/// Eager-fail validation for <see cref="ExchangeOptions"/>. Replaces the
/// previous ad-hoc <c>FirmConfigValidation</c> helper that was only invoked
/// from the Real-mode service factory at first DI resolution — too late to
/// give operators a clean startup error.
///
/// <para>
/// Validation only fires when <see cref="ExchangeOptions.ResolveMode"/>
/// returns <see cref="ExchangeMode.Real"/>; the Stub / Mock / Unavailable
/// modes don't open FIXP sessions so a partially-filled <see cref="FirmConfig"/>
/// is harmless and intentionally tolerated (test fixtures rely on this).
/// </para>
///
/// Wire via:
/// <code>
/// services.AddOptions&lt;ExchangeOptions&gt;()
///         .Bind(config.GetSection(ExchangeOptions.SectionName))
///         .ValidateOnStart();
/// services.AddSingleton&lt;IValidateOptions&lt;ExchangeOptions&gt;, ExchangeOptionsValidator&gt;();
/// </code>
/// </summary>
public sealed class ExchangeOptionsValidator : IValidateOptions<ExchangeOptions>
{
    public ValidateOptionsResult Validate(string? name, ExchangeOptions options)
    {
        if (options is null)
            return ValidateOptionsResult.Fail("ExchangeOptions is null.");

        if (options.ResolveMode() != ExchangeMode.Real)
            return ValidateOptionsResult.Success;

        if (options.Firms.Count == 0)
            return ValidateOptionsResult.Fail(
                "Trading:Exchange:Mode is Real but no Firms[] configured. Set Mode=Unavailable for an honest no-broker host.");

        var failures = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < options.Firms.Count; i++)
        {
            var f = options.Firms[i];
            ValidateFirm(i, f, failures);
            if (!string.IsNullOrWhiteSpace(f.FirmId) && !seen.Add(f.FirmId))
                failures.Add($"Trading:Exchange:Firms[{i}].FirmId='{f.FirmId}' is duplicated; FirmId must be unique across firms.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateFirm(int index, FirmConfig f, List<string> failures)
    {
        var p = $"Trading:Exchange:Firms[{index}]";

        if (string.IsNullOrWhiteSpace(f.FirmId))
            failures.Add($"{p}.FirmId is required.");
        if (string.IsNullOrWhiteSpace(f.Endpoint))
            failures.Add($"{p}.Endpoint is required (host:port).");
        else if (!TryParseEndpointShape(f.Endpoint))
            failures.Add($"{p}.Endpoint='{f.Endpoint}' must be 'host:port' with a numeric port.");
        if (string.IsNullOrEmpty(f.AccessKey))
            failures.Add($"{p}.AccessKey is required.");
        if (f.SessionId == 0)
            failures.Add($"{p}.SessionId must be > 0 (assigned by B3 per firm).");
        if (f.SessionVerId == 0)
            failures.Add($"{p}.SessionVerId must be >= 1; the FIXP gateway requires strict-greater on each Negotiate.");
        if (f.EnteringFirm == 0)
            failures.Add($"{p}.EnteringFirm must be > 0 (assigned by B3).");
        if (string.IsNullOrEmpty(f.SenderLocation) || f.SenderLocation.Length > 10)
            failures.Add($"{p}.SenderLocation must be 1..10 chars.");
        if (string.IsNullOrEmpty(f.EnteringTrader) || f.EnteringTrader.Length > 5)
            failures.Add($"{p}.EnteringTrader must be 1..5 chars.");
        if (f.KeepAliveIntervalMs is < 100u or > 60_000u)
            failures.Add($"{p}.KeepAliveIntervalMs={f.KeepAliveIntervalMs} is out of range; expected 100..60000.");
    }

    /// <summary>
    /// Cheap shape check ("does it look like host:port?"). Real DNS resolution
    /// is deferred to the gateway factory because it requires network and
    /// shouldn't block startup validation.
    /// </summary>
    private static bool TryParseEndpointShape(string endpoint)
    {
        var parts = endpoint.Split(':', 2);
        return parts.Length == 2
            && !string.IsNullOrWhiteSpace(parts[0])
            && int.TryParse(parts[1], out var port)
            && port is > 0 and <= 65535;
    }
}
