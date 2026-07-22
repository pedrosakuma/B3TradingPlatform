namespace B3.Trading.Api.Auth;

/// <summary>
/// Q4.6 (#306). Canonical role-claim strings emitted by <see cref="JwtIssuer"/>
/// and matched by <c>[Authorize(Roles = ...)]</c> / policy gates. The role
/// claim itself is open-set (any string operators put in
/// <c>Trading:Auth:Users:N:Role</c> ends up in the JWT), but every
/// first-class role recognised by the host is named here so the capture
/// sites + policy registration agree on the wire spelling.
///
/// <para><b>Compliance.</b> Added in Q4.6 to gate the drop-copy WebSocket
/// feed (<c>/ws/dropcopy</c>). A compliance principal sees every order /
/// fill / cancel for its own firm regardless of the originating user; an
/// admin principal is treated symmetrically and may additionally pass
/// <c>?firmId=</c> to view another firm. No /api/admin/* surface was
/// broadened — compliance read-paths land via #306-style endpoints, not
/// by mixing roles into the existing admin policy.</para>
/// </summary>
public static class Roles
{
    /// <summary>Default end-user role for trading clients.</summary>
    public const string User = "user";

    /// <summary>Administrative role; can also exercise compliance surfaces (cross-firm).</summary>
    public const string Admin = "admin";

    /// <summary>Compliance role; firm-scoped read-only oversight (Q4.6 / #306).</summary>
    public const string Compliance = "compliance";
}
