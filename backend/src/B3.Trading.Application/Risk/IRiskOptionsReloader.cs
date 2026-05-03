namespace B3.Trading.Application.Risk;

/// <summary>
/// Optional seam that lets non-appsettings configuration providers
/// (file/DB adapters from the persistence spike) be told to refresh.
/// The default ASP.NET Core appsettings provider already watches the
/// file and pushes <see cref="Microsoft.Extensions.Options.IOptionsMonitor{T}"/>
/// notifications, so it does not need to register an implementation
/// — the <c>POST /admin/risk/reload</c> endpoint becomes a no-op
/// returning 204.
/// </summary>
public interface IRiskOptionsReloader
{
    void Reload();
}
