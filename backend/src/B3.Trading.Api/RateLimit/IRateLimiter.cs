namespace B3.Trading.Api.RateLimit;

/// <summary>
/// Q4.4 (#304). Service abstraction over the token-bucket rate limiter
/// so unit tests can swap a deterministic fake in for the production
/// implementation.
/// </summary>
public interface IRateLimiter
{
    /// <summary>
    /// Attempts to acquire a single token for the
    /// <c>(userKey, endpointKey)</c> bucket.
    /// </summary>
    /// <param name="userKey">
    /// Identity-or-IP partition key. See
    /// <c>TokenBucketRateLimitOptions</c> remarks for the resolution
    /// rules (sub-claim ?? remote IP ?? "anonymous").
    /// </param>
    /// <param name="endpointKey">
    /// Bucket-grouping key — typically the matched rule's
    /// <c>PathPattern</c>, NOT the raw request path. Keeping this low
    /// cardinality is what makes the per-user × endpoint metric tag
    /// safe.
    /// </param>
    /// <param name="burst">Bucket capacity.</param>
    /// <param name="refillPerSecond">Refill rate (tokens / second).</param>
    /// <param name="retryAfterSeconds">
    /// On a denied acquire, the number of seconds the caller must wait
    /// before at least one token becomes available. Always 0 when the
    /// method returns true.
    /// </param>
    /// <returns>True if a token was deducted; false if the bucket is empty.</returns>
    bool TryAcquire(
        string userKey,
        string endpointKey,
        int burst,
        double refillPerSecond,
        out double retryAfterSeconds);
}
