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

public sealed record RestOrderIdempotencyExecution<T>(
    RestOrderIdempotencyExecutionKind Kind,
    RestOrderIdempotencyBindingSnapshot? Binding,
    T? Value);

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
        var scopedKeyDigest = ScopedKeyDigest(identity);
        var semaphore = _locks.GetOrAdd(scopedKeyDigest, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_byScopedKey.TryGetValue(scopedKeyDigest, out var existing))
                {
                    return CryptographicOperations.FixedTimeEquals(
                            Convert.FromHexString(existing.CanonicalRequestSha256),
                            Convert.FromHexString(canonicalRequestSha256))
                        ? new(RestOrderIdempotencyExecutionKind.Replayed, existing, default)
                        : new(RestOrderIdempotencyExecutionKind.Conflict, existing, default);
                }
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
            if (_byMutation.TryGetValue(binding.MutationId, out var mutationExisting)
                && mutationExisting != binding)
                throw new InvalidOperationException("Mutation already has a different REST idempotency binding.");
            _byScopedKey.Add(binding.ScopedKeyDigest, binding);
            _byMutation[binding.MutationId] = binding;
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
        var candidate = _protector.CreateStableEndClientRef(
            firmId,
            $"rest-owner-v1\n{endClientId}\n{principalId}\n{operation}");
        return FixedTimeHexEquals(binding.OwnerScopeRef, candidate);
    }

    public IReadOnlyList<RestOrderIdempotencyBindingSnapshot> CaptureSnapshot()
    {
        lock (_gate)
            return _byMutation.Values.OrderBy(x => x.BoundAtUtc).ToArray();
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

    private string ScopedKeyDigest(RestOrderIdempotencyIdentity identity) =>
        _protector.CreateStableEndClientRef(
            identity.FirmId,
            $"rest-idempotency-v1\n{identity.EndClientId}\n{identity.PrincipalId}\n{identity.Operation}\n{identity.Key}");

    private string OwnerScopeRef(RestOrderIdempotencyIdentity identity) =>
        _protector.CreateStableEndClientRef(
            identity.FirmId,
            $"rest-owner-v1\n{identity.EndClientId}\n{identity.PrincipalId}\n{identity.Operation}");

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
