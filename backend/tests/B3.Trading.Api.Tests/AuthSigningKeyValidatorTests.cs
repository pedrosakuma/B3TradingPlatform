using B3.Trading.Api.Auth;

namespace B3.Trading.Api.Tests;

public class AuthSigningKeyValidatorTests
{
    private const string GoodKey = "this-is-a-perfectly-fine-32-byte-key!!";

    [Fact]
    public void Empty_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            AuthSigningKeyValidator.Validate("Production", string.Empty));
    }

    [Fact]
    public void TooShort_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            AuthSigningKeyValidator.Validate("Production", "short"));
    }

    [Fact]
    public void DevKey_In_Production_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AuthSigningKeyValidator.Validate("Production", AuthSigningKeyValidator.DevOnlyKey));
        Assert.Contains("dev-only", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DevKey_In_Docker_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            AuthSigningKeyValidator.Validate("Docker", AuthSigningKeyValidator.DevOnlyKey));
    }

    [Fact]
    public void DevKey_In_Development_Allowed()
    {
        // Should not throw.
        AuthSigningKeyValidator.Validate("Development", AuthSigningKeyValidator.DevOnlyKey);
    }

    [Fact]
    public void GoodKey_In_Production_Allowed()
    {
        AuthSigningKeyValidator.Validate("Production", GoodKey);
    }

    [Fact]
    public void EnvironmentName_Is_Case_Insensitive()
    {
        AuthSigningKeyValidator.Validate("development", AuthSigningKeyValidator.DevOnlyKey);
    }
}
