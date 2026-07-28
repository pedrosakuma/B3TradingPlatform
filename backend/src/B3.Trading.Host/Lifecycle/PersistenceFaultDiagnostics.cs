using B3.Trading.Application.Persistence;
using B3.Trading.Infrastructure.Persistence;

namespace B3.Trading.Host.Lifecycle;

internal sealed record PersistenceFaultDiagnostic(
    string Code,
    string Message,
    string RecommendedAction);

internal static class PersistenceFaultDiagnostics
{
    public static PersistenceFaultDiagnostic? Describe(
        Exception? fault,
        PersistenceOptions options)
    {
        if (fault is null)
            return null;

        return fault switch
        {
            WalLegacyMigrationRequiredException ex => new PersistenceFaultDiagnostic(
                "legacy_wal_migration_required",
                ex.Message,
                "Scale the writer down, then run "
                + $"`dotnet /app/tools/identity-maintenance/B3.Trading.IdentityMaintenance.dll recover-legacy-wal --data-directory {options.DataDirectory} --firm-id {options.FirmId} --operator <operator> --change-ticket <ticket> --reason <reason> --i-understand-this-promotes-a-legacy-wal-without-proving-the-tail-was-durable`."),
            WalRecoveryException ex => new PersistenceFaultDiagnostic(
                "wal_recovery_failed",
                ex.Message,
                "Inspect WAL and snapshot artifacts before restarting; this failure was surfaced by the fail-closed recovery path."),
            IOException ex => new PersistenceFaultDiagnostic(
                "wal_io_fault",
                ex.Message,
                "Investigate the persistence volume and host logs before re-opening readiness."),
            _ => new PersistenceFaultDiagnostic(
                "wal_fault",
                fault.Message,
                "Inspect the persistence startup fault before re-opening readiness."),
        };
    }
}
