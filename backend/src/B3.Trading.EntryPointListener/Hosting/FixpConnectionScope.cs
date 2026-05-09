using B3.Trading.Application.UserBots;

namespace B3.Trading.EntryPointListener.Hosting;

/// <summary>
/// Per-connection scope established once a FIXP <c>Negotiate</c> has
/// authenticated. Bundles the resolved <see cref="BotSessionPrincipal"/>
/// with the connection identifier used for single-active-session claims
/// and the <see cref="ConnectionId"/> threaded through the structured
/// auth log lines.
///
/// <para>This is the in-process equivalent of the JWT-issued
/// <c>ClaimsPrincipal</c> attached to REST/WS requests — it is what the
/// future order pipeline (sub-issue E) reads to enforce per-user
/// isolation on FIXP-originated <c>NewOrderSingle</c>s.</para>
/// </summary>
internal sealed record FixpConnectionScope(
    string ConnectionId,
    BotSessionPrincipal Principal,
    BotSessionState SessionState);
