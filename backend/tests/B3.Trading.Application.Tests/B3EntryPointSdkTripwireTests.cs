using System.Linq;
using System.Reflection;
using Up = B3.EntryPoint.Client.Models;

namespace B3.Trading.Application.Tests;

/// <summary>
/// #459 (spin-off de #441 / #436). Tripwire defensivo: a auditoria
/// de compliance B3 (24/05/2026) identificou campos FIX/B3 que a
/// plataforma deveria emitir/honrar no wire mas que o
/// <c>B3.EntryPoint.Client 0.16.1</c> ainda NÃO expõe no shape público
/// de <see cref="Up.NewOrderRequest"/> e <see cref="Up.ReplaceOrderRequest"/>:
///
/// <list type="bullet">
///   <item><b>ExecInst</b> — tag FIX 18, flags importantes
///   (STP mode, do-not-aggregate, work-up, AON, …).</item>
///   <item><b>DisplayResetPolicy</b> / <b>RefreshPolicy</b> —
///   refresh policy do iceberg nativo (Always / OnPartialFill /
///   Never). Hoje o REST + risk forçam <c>Always</c> via
///   <see cref="B3.Trading.Application.OrderSubmissionService"/>
///   (#297) para que a omissão no wire seja faithful — quando o
///   SDK expor o campo, esse guard pode cair (fecha #298).</item>
/// </list>
///
/// <para>
/// <b>Como o tripwire funciona.</b> Cada teste afirma "a propriedade
/// X NÃO existe no SDK". Quando o mantenedor do SDK adicionar a
/// propriedade (bump de versão), o teste fica vermelho — o que é
/// exatamente o sinal para plumbar o campo end-to-end no
/// <see cref="B3.Trading.Infrastructure.B3EntryPointClientGateway"/>,
/// no Domain / WAL / REST, e ENTÃO remover este tripwire (junto
/// com o REST guard correspondente quando aplicável).
/// </para>
///
/// <para>
/// O lookup é case-insensitive porque o SDK pode escolher
/// capitalização ligeiramente diferente (e.g. <c>ExecInst</c> vs
/// <c>ExecutionInstruction</c>); usar <see cref="StringComparison.OrdinalIgnoreCase"/>
/// + a substring evita um falso negativo onde só a capitalização
/// muda. Caso o SDK exponha um nome inteiramente diferente
/// (improvável dado o padrão FIX), o tripwire continuará verde e
/// o gap será pego pela próxima auditoria — risco aceito.
/// </para>
/// </summary>
public class B3EntryPointSdkTripwireTests
{
    private static readonly string[] ExecInstAliases = ["ExecInst", "ExecutionInstruction", "ExecutionInstructions"];
    private static readonly string[] DisplayPolicyAliases = ["DisplayResetPolicy", "RefreshPolicy", "DisplayRefreshPolicy"];

    [Fact]
    public void NewOrderRequest_StillLacks_ExecInst_AsOf_SdkVersion()
    {
        AssertSdkPropertyMissing(typeof(Up.NewOrderRequest), ExecInstAliases,
            issueRef: "#441 (ExecInst flags)");
    }

    [Fact]
    public void NewOrderRequest_StillLacks_DisplayResetPolicy_AsOf_SdkVersion()
    {
        AssertSdkPropertyMissing(typeof(Up.NewOrderRequest), DisplayPolicyAliases,
            issueRef: "#298 / #436. When this trips: wire order.DisplayResetPolicy in B3EntryPointClientGateway.BuildNewOrderRequest AND drop the REST/risk guard in OrdersEndpoints + OrderSubmissionService that today restricts policy to Always.");
    }

    [Fact]
    public void ReplaceOrderRequest_StillLacks_ExecInst_AsOf_SdkVersion()
    {
        AssertSdkPropertyMissing(typeof(Up.ReplaceOrderRequest), ExecInstAliases,
            issueRef: "#441");
    }

    [Fact]
    public void ReplaceOrderRequest_StillLacks_DisplayResetPolicy_AsOf_SdkVersion()
    {
        AssertSdkPropertyMissing(typeof(Up.ReplaceOrderRequest), DisplayPolicyAliases,
            issueRef: "#298 / #436");
    }

    private static void AssertSdkPropertyMissing(Type sdkType, string[] aliases, string issueRef)
    {
        var props = sdkType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var found = aliases.FirstOrDefault(a => props.Contains(a));
        Assert.True(found is null,
            $"TRIPWIRE: B3.EntryPoint.Client agora expõe '{found}' em {sdkType.FullName}. " +
            $"Ação esperada: plumbar o campo end-to-end (Domain → WAL → REST → BuildRequest), " +
            $"adicionar wire-pinning test em B3EntryPointClientGatewayMapTests / TranslationTests, " +
            $"e remover este tripwire. Tracking: {issueRef}.");
    }
}
