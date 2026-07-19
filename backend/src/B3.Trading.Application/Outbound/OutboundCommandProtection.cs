using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Outbound;

public sealed class OutboundCommandProtectionOptions
{
    public const string SectionName = "Trading:OutboundCommandProtection";

    public string ActiveKeyId { get; set; } = string.Empty;
    public int ActiveKeyVersion { get; set; } = 1;
    public string StableReferenceKeyId { get; set; } = string.Empty;
    public int StableReferenceKeyVersion { get; set; } = 1;
    public List<OutboundCommandProtectionKeyOptions> Keys { get; set; } = new();
}

public sealed class OutboundCommandProtectionKeyOptions
{
    public string KeyId { get; set; } = string.Empty;
    public int Version { get; set; } = 1;
    public string KeyBase64 { get; set; } = string.Empty;
}

public interface IOutboundNonceSource
{
    void Fill(Span<byte> nonce);
}

public sealed class CryptographicOutboundNonceSource : IOutboundNonceSource
{
    public void Fill(Span<byte> nonce) => RandomNumberGenerator.Fill(nonce);
}

public interface IOutboundCommandProtector
{
    EncryptedOutboundCommandEnvelope Encrypt(
        OutboundMutationId mutationId,
        string firmId,
        OutboundCanonicalCommand command,
        IReadOnlyList<OutboundSensitiveFieldRef> sensitiveFieldRefs,
        SensitiveOutboundCommand sensitiveCommand);

    SensitiveOutboundCommand Decrypt(
        OutboundMutationId mutationId,
        string firmId,
        OutboundCanonicalCommand command,
        IReadOnlyList<OutboundSensitiveFieldRef> sensitiveFieldRefs,
        EncryptedOutboundCommandEnvelope envelope);

    string CreateStableEndClientRef(string firmId, string endClientId);

    OutboundStableReferenceKey ActiveStableReferenceKey { get; }

    string CreateStableReference(
        OutboundStableReferenceKey keyIdentity,
        string canonicalValue);
}

public readonly record struct OutboundStableReferenceKey(string KeyId, int KeyVersion);

public sealed class AeadOutboundCommandProtector : IOutboundCommandProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IReadOnlyDictionary<(string Id, int Version), byte[]> _keys;
    private readonly (string Id, int Version) _active;
    private readonly (string Id, int Version) _stableReference;
    private readonly IOutboundNonceSource _nonces;

    public AeadOutboundCommandProtector(
        IOptions<OutboundCommandProtectionOptions> options,
        IOutboundNonceSource? nonces = null)
        : this(options?.Value ?? throw new ArgumentNullException(nameof(options)), nonces)
    {
    }

    public AeadOutboundCommandProtector(
        OutboundCommandProtectionOptions options,
        IOutboundNonceSource? nonces = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _active = (options.ActiveKeyId, options.ActiveKeyVersion);
        _stableReference = string.IsNullOrWhiteSpace(options.StableReferenceKeyId)
            ? _active
            : (options.StableReferenceKeyId, options.StableReferenceKeyVersion);
        _nonces = nonces ?? new CryptographicOutboundNonceSource();
        var keys = new Dictionary<(string, int), byte[]>();
        foreach (var configured in options.Keys)
        {
            if (string.IsNullOrWhiteSpace(configured.KeyId) || configured.Version <= 0)
                throw new ArgumentException("Outbound encryption key identity is invalid.", nameof(options));
            byte[] key;
            try
            {
                key = Convert.FromBase64String(configured.KeyBase64);
            }
            catch (FormatException ex)
            {
                throw new ArgumentException("Outbound encryption key material is not valid base64.", nameof(options), ex);
            }
            if (key.Length != KeySize)
                throw new ArgumentException("Outbound encryption keys must contain 256 bits.", nameof(options));
            if (!keys.TryAdd((configured.KeyId, configured.Version), key))
                throw new ArgumentException("Outbound encryption key identities must be unique.", nameof(options));
        }
        _keys = keys;
    }

    public EncryptedOutboundCommandEnvelope Encrypt(
        OutboundMutationId mutationId,
        string firmId,
        OutboundCanonicalCommand command,
        IReadOnlyList<OutboundSensitiveFieldRef> sensitiveFieldRefs,
        SensitiveOutboundCommand sensitiveCommand)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(sensitiveFieldRefs);
        ArgumentNullException.ThrowIfNull(sensitiveCommand);
        if (!_keys.TryGetValue(_active, out var key))
            throw new OutboundCommandEnvelopeException(
                OutboundSensitivePayloadAvailability.MissingHistoricalKey,
                "The active outbound command encryption key is unavailable.");

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(sensitiveCommand, JsonOptions);
        var nonce = new byte[NonceSize];
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        try
        {
            _nonces.Fill(nonce);
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, BuildAssociatedData(
                mutationId, firmId, command, sensitiveFieldRefs,
                _active.Id, _active.Version));
            return new EncryptedOutboundCommandEnvelope
            {
                KeyId = _active.Id,
                KeyVersion = _active.Version,
                AlgorithmVersion = EncryptedOutboundCommandEnvelope.CurrentAlgorithmVersion,
                NonceBase64 = Convert.ToBase64String(nonce),
                CiphertextBase64 = Convert.ToBase64String(ciphertext),
                AuthenticationTagBase64 = Convert.ToBase64String(tag),
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public SensitiveOutboundCommand Decrypt(
        OutboundMutationId mutationId,
        string firmId,
        OutboundCanonicalCommand command,
        IReadOnlyList<OutboundSensitiveFieldRef> sensitiveFieldRefs,
        EncryptedOutboundCommandEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(sensitiveFieldRefs);
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.AlgorithmVersion != EncryptedOutboundCommandEnvelope.CurrentAlgorithmVersion)
        {
            throw new OutboundCommandEnvelopeException(
                OutboundSensitivePayloadAvailability.UnsupportedVersion,
                "The outbound command encryption algorithm version is unsupported.");
        }
        if (!_keys.TryGetValue((envelope.KeyId, envelope.KeyVersion), out var key))
        {
            throw new OutboundCommandEnvelopeException(
                OutboundSensitivePayloadAvailability.MissingHistoricalKey,
                "A historical outbound command encryption key is unavailable.");
        }

        byte[] nonce;
        byte[] ciphertext;
        byte[] tag;
        try
        {
            nonce = Convert.FromBase64String(envelope.NonceBase64);
            ciphertext = Convert.FromBase64String(envelope.CiphertextBase64);
            tag = Convert.FromBase64String(envelope.AuthenticationTagBase64);
        }
        catch (FormatException)
        {
            throw new OutboundCommandEnvelopeException(
                OutboundSensitivePayloadAvailability.AuthenticationFailed,
                "The outbound command envelope encoding is invalid.");
        }
        if (nonce.Length != NonceSize || tag.Length != TagSize)
        {
            throw new OutboundCommandEnvelopeException(
                OutboundSensitivePayloadAvailability.AuthenticationFailed,
                "The outbound command envelope dimensions are invalid.");
        }

        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, BuildAssociatedData(
                mutationId, firmId, command, sensitiveFieldRefs,
                envelope.KeyId, envelope.KeyVersion));
            return JsonSerializer.Deserialize<SensitiveOutboundCommand>(plaintext, JsonOptions)
                ?? throw new OutboundCommandEnvelopeException(
                    OutboundSensitivePayloadAvailability.AuthenticationFailed,
                    "The outbound command envelope payload is invalid.");
        }
        catch (CryptographicException)
        {
            throw new OutboundCommandEnvelopeException(
                OutboundSensitivePayloadAvailability.AuthenticationFailed,
                "The outbound command envelope failed authentication.");
        }
        catch (JsonException)
        {
            throw new OutboundCommandEnvelopeException(
                OutboundSensitivePayloadAvailability.AuthenticationFailed,
                "The outbound command envelope payload is invalid.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public string CreateStableEndClientRef(string firmId, string endClientId)
        => CreateStableReference(
            ActiveStableReferenceKey,
            $"{firmId}\n{endClientId}");

    public OutboundStableReferenceKey ActiveStableReferenceKey =>
        new(_stableReference.Id, _stableReference.Version);

    public string CreateStableReference(
        OutboundStableReferenceKey keyIdentity,
        string canonicalValue)
    {
        if (string.IsNullOrWhiteSpace(keyIdentity.KeyId) || keyIdentity.KeyVersion <= 0)
            throw new OutboundCommandEnvelopeException(
                OutboundSensitivePayloadAvailability.MissingHistoricalKey,
                "The stable outbound reference key identity is unavailable.");
        if (!_keys.TryGetValue((keyIdentity.KeyId, keyIdentity.KeyVersion), out var key))
            throw new OutboundCommandEnvelopeException(
                OutboundSensitivePayloadAvailability.MissingHistoricalKey,
                "A historical stable outbound reference key is unavailable.");
        using var derivation = new HMACSHA256(key);
        var referenceKey = derivation.ComputeHash(
            Encoding.ASCII.GetBytes("b3-outbound-stable-reference-v1"));
        try
        {
            using var hmac = new HMACSHA256(referenceKey);
            var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(canonicalValue));
            return Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(referenceKey);
        }
    }

    internal static string ComputeIntegritySha256(
        OutboundCanonicalCommand command,
        IReadOnlyList<OutboundSensitiveFieldRef> sensitiveFieldRefs,
        EncryptedOutboundCommandEnvelope envelope)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(
            new IntegrityPayload(command, sensitiveFieldRefs, envelope),
            JsonOptions);
        try
        {
            return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }
    }

    internal static bool IntegrityMatches(OutboundApprovalSnapshot approval)
    {
        var expected = ComputeIntegritySha256(
            approval.CanonicalCommandNonSensitive,
            approval.SensitiveFieldRefs,
            approval.SensitiveCommandEnvelope);
        var actualBytes = Encoding.ASCII.GetBytes(approval.StoredCommandIntegritySha256);
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        return actualBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }

    private static byte[] BuildAssociatedData(
        OutboundMutationId mutationId,
        string firmId,
        OutboundCanonicalCommand command,
        IReadOnlyList<OutboundSensitiveFieldRef> sensitiveFieldRefs,
        string keyId,
        int keyVersion)
    {
        var metadata = JsonSerializer.SerializeToUtf8Bytes(
            new AssociatedDataPayload(command, sensitiveFieldRefs),
            JsonOptions);
        try
        {
            var metadataHash = Convert.ToHexString(SHA256.HashData(metadata));
            return Encoding.UTF8.GetBytes(
                $"b3-outbound-v1\n{mutationId.Value:D}\n{firmId}\n{metadataHash}\n{keyId}\n{keyVersion}");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(metadata);
        }
    }

    private sealed record IntegrityPayload(
        OutboundCanonicalCommand Command,
        IReadOnlyList<OutboundSensitiveFieldRef> SensitiveFieldRefs,
        EncryptedOutboundCommandEnvelope Envelope);

    private sealed record AssociatedDataPayload(
        OutboundCanonicalCommand Command,
        IReadOnlyList<OutboundSensitiveFieldRef> SensitiveFieldRefs);
}

public static class OutboundApprovalFactory
{
    public static OutboundApprovalSnapshot Create(
        OutboundMutationId mutationId,
        string firmId,
        OutboundCanonicalCommand command,
        SensitiveOutboundCommand sensitiveCommand,
        IReadOnlyList<OutboundSensitiveFieldRef> sensitiveFieldRefs,
        IOutboundCommandProtector protector,
        DateTimeOffset approvedAtUtc,
        string? riskDecisionRef = null,
        string? riskPolicyVersion = null,
        string? marginReservationRef = null,
        decimal? marginAmount = null,
        string? marginBasis = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(sensitiveCommand);
        ArgumentNullException.ThrowIfNull(sensitiveFieldRefs);
        ArgumentNullException.ThrowIfNull(protector);
        var envelope = protector.Encrypt(
            mutationId, firmId, command, sensitiveFieldRefs, sensitiveCommand);
        return new OutboundApprovalSnapshot
        {
            ApprovalVersion = 1,
            ApprovedAtUtc = approvedAtUtc,
            RiskDecisionRef = riskDecisionRef,
            RiskPolicyVersion = riskPolicyVersion,
            MarginReservationRef = marginReservationRef,
            MarginAmount = marginAmount,
            MarginBasis = marginBasis,
            CanonicalCommandNonSensitive = command,
            SensitiveFieldRefs = sensitiveFieldRefs.ToArray(),
            SensitiveCommandEnvelope = envelope,
            StoredCommandIntegritySha256 =
                AeadOutboundCommandProtector.ComputeIntegritySha256(
                    command, sensitiveFieldRefs, envelope),
        };
    }
}
