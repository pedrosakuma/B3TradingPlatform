using B3.Trading.Application.Risk;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace B3.Trading.Api;

/// <summary>
/// Read-only surface that exposes effective risk-policy values the
/// trader UI needs to mirror server-side limits client-side. Kept
/// minimal on purpose: only fields the FE has a concrete reason to
/// know about (e.g. ticket validation caps) belong here. The server
/// remains authoritative — this is a hint to keep client-side
/// validation in sync with config, not a substitute for the real
/// pipeline check.
/// </summary>
public static class PolicyEndpoints
{
    public static IEndpointRouteBuilder MapPolicy(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/policy/risk", [Authorize] (IOptionsMonitor<RiskOptions> opts) =>
        {
            // CurrentValue picks up live reload of Trading:Risk
            // config — the FE will see the new cap on the next boot.
            var horizon = opts.CurrentValue.MaxGtdHorizon;
            // Round to whole days; the FE only renders day-granularity.
            // Floor (not round-up) so a 30-day cap never advertises 31.
            var days = (int)Math.Floor(horizon.TotalDays);
            return Results.Ok(new RiskPolicyDto(days));
        });

        return app;
    }
}

public sealed record RiskPolicyDto(int MaxGtdHorizonDays);
