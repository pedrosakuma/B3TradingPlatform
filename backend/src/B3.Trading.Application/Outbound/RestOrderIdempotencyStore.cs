using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace B3.Trading.Application.Outbound;

public sealed record RestOrderIdempotencyBindingSnapshot
{
    public required OutboundMutationId MutationId { get; init; }
    public required ulong ClOrdId { get; init; }
    public required string ScopedKeyDigest { get; init; }
    public required string OwnerScopeRef { get; init; }
    public required string CanonicalRequestSha256 { get; init; }
    public required string Operation { get; init; }
    public required DateTimeOffset BoundAtUtc { get; init; }
    public string StableReferenceKeyId { get; init; } = string.Empty;
    public int StableReferenceKeyVersion { get; init; }
}

public sealed record RestOrderIdempotencyIdentity(
    string FirmId,
    string EndClientId,
    string PrincipalId,
    string Operation,
    string Key);

public sealed record RestOrderIdempotencyContext(
    RestOrderIdempotencyBindingSnapshot Binding);

public enum RestOrderIdempotencyExecutionKind
{
    Created,
    Replayed,
    Conflict,
}

public enum RestOrderIdempotencyResolutionKind
{
    Missing,
    Replayed,
    Conflict,
}

public sealed record RestOrderIdempotencyExecution<T>(
    RestOrderIdempotencyExecutionKind Kind,
    RestOrderIdempotencyBindingSnapshot? Binding,
    T? Value);

public sealed record RestOrderIdempotencyResolution(
    RestOrderIdempotencyResolutionKind Kind,
    RestOrderIdempotencyBindingSnapshot? Binding);

public sealed class RestOrderIdempotencyStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RestOrderIdempotencyBindingSnapshot> _byScopedKey =
        new(StringComparer.Ordinal);
    private readonly Dictionary<OutboundMutationId, RestOrderIdempotencyBindingSnapshot> _byMutation =
        new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks =
        new(StringComparer.Ordinal);
    private readonly IOutboundCommandProtector _protector;
    private readonly TimeProvider _clock;

    public RestOrderIdempotencyStore(
        IOutboundCommandProtector protector,
        TimeProvider? clock = null)
    {
        _protector = protector;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<RestOrderIdempotencyExecution<T>> ExecuteAsync<T>(
        RestOrderIdempotencyIdentity identity,
        string canonicalRequestSha256,
        Func<RestOrderIdempotencyContext, Task<T>> create)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(create);
        ValidateIdentity(identity);
        ValidateDigest(canonicalRequestSha256, 64, nameof(canonicalRequestSha256));
        var activeKey = _protector.ActiveStableReferenceKey;
        var scopedKeyDigest = ScopedKeyDigest(identity, activeKey);
        var semaphore = _locks.GetOrAdd(scopedKeyDigest, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var resolution = Resolve(identity, canonicalRequestSha256);
            if (resolution.Kind != RestOrderIdempotencyResolutionKind.Missing)
            {
                return resolution.Kind == RestOrderIdempotencyResolutionKind.Replayed
                    ? new(RestOrderIdempotencyExecutionKind.Replayed, resolution.Binding, default)
                    : new(RestOrderIdempotencyExecutionKind.Conflict, resolution.Binding, default);
            }

            var binding = new RestOrderIdempotencyBindingSnapshot
            {
                MutationId = OutboundMutationId.New(),
                ClOrdId = 0,
                ScopedKeyDigest = scopedKeyDigest,
                OwnerScopeRef = OwnerScopeRef(identity),
                CanonicalRequestSha256 = canonicalRequestSha256,
                Operation = identity.Operation,
                BoundAtUtc = _clock.GetUtcNow(),
                StableReferenceKeyId = activeKey.KeyId,
                StableReferenceKeyVersion = activeKey.KeyVersion,
            };
            var value = await create(new RestOrderIdempotencyContext(binding))
                .ConfigureAwait(false);
            lock (_gate)
            {
                return _byScopedKey.TryGetValue(scopedKeyDigest, out var applied)
                    ? new(RestOrderIdempotencyExecutionKind.Created, applied, value)
                    : new(RestOrderIdempotencyExecutionKind.Created, null, value);
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    public RestOrderIdempotencyResolution Resolve(
        RestOrderIdempotencyIdentity identity,
        string canonicalRequestSha256)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ValidateIdentity(identity);
        ValidateDigest(canonicalRequestSha256, 64, nameof(canonicalRequestSha256));
        lock (_gate)
        {
            var keyIdentities = _byScopedKey.Values
                .Select(static binding => new OutboundStableReferenceKey(
                    binding.StableReferenceKeyId,
                    binding.StableReferenceKeyVersion))
                .Append(_protector.ActiveStableReferenceKey)
                .Distinct()
                .ToArray();
            var historicalKeyUnavailable = false;
            foreach (var keyIdentity in keyIdentities)
            {
                string digest;
                try
                {
                    digest = ScopedKeyDigest(identity, keyIdentity);
                }
                catch (OutboundCommandEnvelopeException ex)
                    when (ex.Availability == OutboundSensitivePayloadAvailability.MissingHistoricalKey)
                {
                    historicalKeyUnavailable = true;
                    continue;
                }

                if (!_byScopedKey.TryGetValue(digest, out var existing)
                    || existing.StableReferenceKeyId != keyIdentity.KeyId
                    || existing.StableReferenceKeyVersion != keyIdentity.KeyVersion)
                    continue;
                return FixedTimeHexEquals(
                        existing.CanonicalRequestSha256,
                        canonicalRequestSha256)
                    ? new(RestOrderIdempotencyResolutionKind.Replayed, existing)
                    : new(RestOrderIdempotencyResolutionKind.Conflict, existing);
            }

            if (historicalKeyUnavailable)
                throw new RestOrderIdempotencyUnavailableException(
                    "A historical idempotency reference key is unavailable.");
            return new(RestOrderIdempotencyResolutionKind.Missing, null);
        }
    }

    public async Task<RestOrderIdempotencyResolution> ResolveAsync(
        RestOrderIdempotencyIdentity identity,
        string canonicalRequestSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ValidateIdentity(identity);
        ValidateDigest(canonicalRequestSha256, 64, nameof(canonicalRequestSha256));
        var digest = ScopedKeyDigest(identity, _protector.ActiveStableReferenceKey);
        var semaphore = _locks.GetOrAdd(digest, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Resolve(identity, canonicalRequestSha256);
        }
        finally
        {
            semaphore.Release();
        }
    }

    public void Apply(RestOrderIdempotencyBindingSnapshot binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ValidateDigest(binding.ScopedKeyDigest, 32, nameof(binding));
        ValidateDigest(binding.OwnerScopeRef, 32, nameof(binding));
        ValidateDigest(binding.CanonicalRequestSha256, 64, nameof(binding));
        if (binding.MutationId.Value == Guid.Empty
            || binding.ClOrdId == 0
            || string.IsNullOrWhiteSpace(binding.Operation))
            throw new InvalidOperationException("REST idempotency binding identity is invalid.");
        lock (_gate)
        {
            if (_byScopedKey.TryGetValue(binding.ScopedKeyDigest, out var existing))
            {
                if (existing == binding)
                    return;
                throw new InvalidOperationException("Conflicting REST idempotency binding.");
            }
            _byScopedKey.Add(binding.ScopedKeyDigest, binding);
            _byMutation.TryAdd(binding.MutationId, binding);
        }
    }

    public bool TryGetByMutation(
        OutboundMutationId mutationId,
        out RestOrderIdempotencyBindingSnapshot? binding)
    {
        lock (_gate)
        {
            if (_byMutation.TryGetValue(mutationId, out var found))
            {
                binding = found;
                return true;
            }
            binding = null;
            return false;
        }
    }

    public bool IsOwnedBy(
        RestOrderIdempotencyBindingSnapshot binding,
        string firmId,
        string endClientId,
        string principalId,
        string operation)
    {
        if (string.IsNullOrWhiteSpace(binding.StableReferenceKeyId)
            || binding.StableReferenceKeyVersion <= 0)
            throw new RestOrderIdempotencyUnavailableException(
                "The idempotency reference key identity is unavailable.");
        string candidate;
        try
        {
            candidate = _protector.CreateStableReference(
                new OutboundStableReferenceKey(
                    binding.StableReferenceKeyId,
                    binding.StableReferenceKeyVersion),
                $"{firmId}\nrest-owner-v1\n{endClientId}\n{principalId}\n{operation}");
        }
        catch (OutboundCommandEnvelopeException ex)
            when (ex.Availability == OutboundSensitivePayloadAvailability.MissingHistoricalKey)
        {
            throw new RestOrderIdempotencyUnavailableException(
                "A historical idempotency reference key is unavailable.");
        }
        return FixedTimeHexEquals(binding.OwnerScopeRef, candidate);
    }

    public IReadOnlyList<RestOrderIdempotencyBindingSnapshot> CaptureSnapshot()
    {
        lock (_gate)
            return _byScopedKey.Values.OrderBy(x => x.BoundAtUtc).ToArray();
    }

    public void Restore(IEnumerable<RestOrderIdempotencyBindingSnapshot> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        lock (_gate)
        {
            _byScopedKey.Clear();
            _byMutation.Clear();
            foreach (var record in records)
                Apply(record);
        }
    }

    private string ScopedKeyDigest(
        RestOrderIdempotencyIdentity identity,
        OutboundStableReferenceKey keyIdentity) =>
        _protector.CreateStableReference(
            keyIdentity,
            $"{identity.FirmId}\nrest-idempotency-v1\n{identity.EndClientId}\n{identity.PrincipalId}\n{identity.Operation}\n{identity.Key}");

    private string OwnerScopeRef(RestOrderIdempotencyIdentity identity) =>
        _protector.CreateStableReference(
            _protector.ActiveStableReferenceKey,
            $"{identity.FirmId}\nrest-owner-v1\n{identity.EndClientId}\n{identity.PrincipalId}\n{identity.Operation}");

    private static bool FixedTimeHexEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),
            Convert.FromHexString(right));

    private static void ValidateIdentity(RestOrderIdempotencyIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.FirmId)
            || string.IsNullOrWhiteSpace(identity.EndClientId)
            || string.IsNullOrWhiteSpace(identity.PrincipalId)
            || string.IsNullOrWhiteSpace(identity.Operation)
            || string.IsNullOrWhiteSpace(identity.Key))
            throw new ArgumentException("REST idempotency identity is incomplete.");
        if (identity.Key.Length > 256)
            throw new ArgumentException("Idempotency-Key exceeds 256 characters.");
    }

    private static void ValidateDigest(string digest, int length, string parameterName)
    {
        if (digest.Length != length || digest.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("Stable reference digest is invalid.", parameterName);
    }
}

public sealed class RestOrderIdempotencyUnavailableException : InvalidOperationException
{
    public RestOrderIdempotencyUnavailableException(string message) : base(message) { }
}
