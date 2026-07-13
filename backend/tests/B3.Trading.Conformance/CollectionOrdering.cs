using Xunit;
using Xunit.Abstractions;

[assembly: TestCollectionOrderer(
    "B3.Trading.Conformance.CollectionOrdering+RunRecoveryCollectionsLastOrderer",
    "B3.Trading.Conformance")]

namespace B3.Trading.Conformance;

public static class CollectionOrdering
{
    public sealed class RunRecoveryCollectionsLastOrderer : ITestCollectionOrderer
    {
        public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections)
        {
            var collections = testCollections.ToList();
            return collections
                .Where(static collection => !IsRecoveryCollection(collection))
                .Concat(collections.Where(static collection => IsRecoveryCollection(collection)));
        }

        private static bool IsRecoveryCollection(ITestCollection collection) =>
            collection.DisplayName.Contains("TradingHostCrashRestartSpecTests", StringComparison.Ordinal);
    }
}
