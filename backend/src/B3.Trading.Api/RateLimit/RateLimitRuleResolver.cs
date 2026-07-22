using Microsoft.AspNetCore.Http;

namespace B3.Trading.Api.RateLimit;

/// <summary>
/// Q4.4 (#304). Resolves the effective <see cref="TokenBucketRule"/> a
/// given <see cref="HttpContext"/> belongs to.
/// </summary>
/// <remarks>
/// Resolution priority (deterministic, evaluated in order):
/// <list type="number">
///   <item><description>
///     Non-<c>IsGenericFallback</c> rules always outrank the catch-all
///     read/write defaults — an explicit pattern wins even if it is
///     method-less and the fallback is method-restricted.
///   </description></item>
///   <item><description>
///     Longer <c>PathPattern</c> outranks shorter (longest-prefix-wins
///     so <c>/api/algo/twap</c> beats <c>/api/algo/</c>).
///   </description></item>
///   <item><description>
///     Method-aware rules outrank method-less ones at equal length so
///     a future POST-only <c>/foo</c> would win against a method-less
///     <c>/foo</c>.
///   </description></item>
/// </list>
/// <para>
/// Operator-supplied rules from <c>Trading:RateLimit:Rules</c> override
/// any code default with the same <c>(PathPattern, Methods)</c>; new
/// patterns are appended to the merged set.
/// </para>
/// </remarks>
public sealed class RateLimitRuleResolver
{
    private readonly TokenBucketRule[] _ordered;

    public RateLimitRuleResolver(TokenBucketRateLimitOptions options)
    {
        var merged = MergeWithDefaults(options.Rules);
        // Pre-sort so the per-request match is a linear scan with
        // first-match semantics. The comparator implements the priority
        // documented in the class remarks.
        _ordered = merged
            // Explicit rules ALWAYS outrank generic fallbacks, even when
            // the fallback is method-restricted — otherwise the generic
            // POST fallback (length 1, POST-restricted) would beat the
            // method-less /api/auth/login (length 11).
            .OrderBy(r => r.IsGenericFallback)
            .ThenByDescending(r => r.PathPattern.Length)
            .ThenByDescending(r => r.Methods.Count > 0)
            .ToArray();
    }

    /// <summary>
    /// Returns the matching rule for <paramref name="ctx"/>, or
    /// <c>null</c> when no rule (including the generic fallbacks)
    /// matched — which the middleware treats as "no limit applied".
    /// </summary>
    public TokenBucketRule? Resolve(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "/";
        var method = ctx.Request.Method;

        foreach (var rule in _ordered)
        {
            if (rule.Methods.Count > 0
                && !rule.Methods.Contains(method, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (PathMatches(path, rule.PathPattern))
            {
                return rule;
            }
        }
        return null;
    }

    private static bool PathMatches(string path, string pattern)
    {
        if (pattern == "/") return true; // generic fallback
        // Prefix match: "/api/orders" matches "/api/orders" and "/api/orders/123"
        // but NOT "/api/orders-archive". The trailing-slash test is what
        // keeps "/api/algo" from accidentally swallowing "/algorithm".
        if (!path.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
            return false;
        if (path.Length == pattern.Length) return true;
        // pattern ends in '/'? then any continuation is a child path.
        if (pattern.EndsWith('/')) return true;
        // otherwise the character after the prefix must be a path
        // separator for it to count as a child route.
        return path[pattern.Length] == '/';
    }

    private static List<TokenBucketRule> MergeWithDefaults(IList<TokenBucketRule> overrides)
    {
        var merged = TokenBucketRateLimitOptions.Defaults();
        foreach (var ov in overrides)
        {
            // Match defaults by (pattern, methods set). Different
            // methods-sets are different rules so operators can,
            // for instance, override the GET fallback without touching
            // the write fallback.
            var existing = merged.FindIndex(d =>
                string.Equals(d.PathPattern, ov.PathPattern, StringComparison.OrdinalIgnoreCase)
                && SameMethodSet(d.Methods, ov.Methods));
            if (existing >= 0)
            {
                merged[existing] = ov;
            }
            else
            {
                merged.Add(ov);
            }
        }
        return merged;
    }

    private static bool SameMethodSet(List<string> a, List<string> b)
    {
        if (a.Count != b.Count) return false;
        var setA = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
        return b.All(m => setA.Contains(m));
    }
}
