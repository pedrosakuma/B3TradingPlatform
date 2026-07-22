using B3.Trading.Application;
using B3.Trading.Application.MarketData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace B3.Trading.Api;

/// <summary>
/// FE-OPT-2 (#498). Exposes the <see cref="SecurityDefinitionRegistry"/>
/// for option chain lookup. The registry is populated by SDK events
/// (see <c>SdkMarketDataSubscriber</c>); if the SDK hasn't projected any
/// instruments yet, responses will be empty arrays.
/// </summary>
public static class InstrumentsEndpoints
{
    public static IEndpointRouteBuilder MapInstruments(this IEndpointRouteBuilder app)
    {
        // GET /api/instruments?underlying=PETR4 — returns options for that underlying
        // GET /api/instruments?type=option — returns all options
        // GET /api/instruments — returns all known instruments (options + equities)
        app.MapGet("/api/instruments", [Authorize] (
            SecurityDefinitionRegistry registry,
            SymbolDirectory directory,
            string? underlying,
            string? type) =>
        {
            var optionsOnly = string.Equals(type, "option", StringComparison.OrdinalIgnoreCase);

            // When underlying is specified, always filter to options
            if (!string.IsNullOrWhiteSpace(underlying))
                optionsOnly = true;

            var results = registry.Enumerate(underlying, optionsOnly)
                .Select(x => new InstrumentDto(
                    x.Symbol,
                    x.SecurityId,
                    x.Spec.SecurityType.ToString(),
                    x.Spec.TickSize,
                    x.Spec.LotSize,
                    x.Spec.Option?.StrikePrice,
                    x.Spec.Option?.ExpirationDate.ToString("yyyy-MM-dd"),
                    x.Spec.Option?.PutOrCall.ToString(),
                    x.Spec.Option?.UnderlyingSymbol,
                    x.Spec.Option?.ContractMultiplier))
                .OrderBy(x => x.UnderlyingSymbol)
                .ThenBy(x => x.ExpirationDate)
                .ThenBy(x => x.StrikePrice)
                .ThenBy(x => x.PutOrCall)
                .ToArray();

            return Results.Ok(results);
        })
        .WithName("GetInstruments")
        .WithTags("Instruments");

        return app;
    }
}

/// <summary>
/// FE-OPT-2 (#498). Wire shape for instrument metadata. Option-specific
/// fields are null for equities.
/// </summary>
public sealed record InstrumentDto(
    string Symbol,
    ulong SecurityId,
    string SecurityType,
    decimal? TickSize,
    long? LotSize,
    decimal? StrikePrice,
    string? ExpirationDate,
    string? PutOrCall,
    string? UnderlyingSymbol,
    decimal? ContractMultiplier);
