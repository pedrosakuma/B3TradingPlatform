namespace B3.Trading.Api.Auth.WebAuthn;

public sealed class WebAuthnOptions
{
    public const string SectionName = "Trading:Auth:WebAuthn";

    public string RelyingPartyId { get; set; } = string.Empty;
    public string RelyingPartyName { get; set; } = "B3 Trading Platform";
    public List<string> Origins { get; set; } = new();
    public TimeSpan ChallengeTtl { get; set; } = TimeSpan.FromMinutes(5);
    public uint TimeoutMilliseconds { get; set; } = 60_000;
}
