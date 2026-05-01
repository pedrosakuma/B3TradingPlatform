using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace B3.Trading.Api;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdmin(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin").RequireAuthorization("admin");

        group.MapGet("/kill", (KillSwitchService svc) => Results.Ok(new
        {
            EndClients = svc.ListKilledEndClients(),
            Firms = svc.ListKilledFirms(),
        }));

        group.MapPost("/kill/end-client/{id}", (string id, KillSwitchService svc) =>
        {
            svc.KillEndClient(new EndClientId(id));
            return Results.NoContent();
        });

        group.MapDelete("/kill/end-client/{id}", (string id, KillSwitchService svc) =>
        {
            svc.ReviveEndClient(new EndClientId(id));
            return Results.NoContent();
        });

        group.MapPost("/kill/firm/{id}", (string id, KillSwitchService svc) =>
        {
            svc.KillFirm(id);
            return Results.NoContent();
        });

        group.MapDelete("/kill/firm/{id}", (string id, KillSwitchService svc) =>
        {
            svc.ReviveFirm(id);
            return Results.NoContent();
        });

        return app;
    }
}
