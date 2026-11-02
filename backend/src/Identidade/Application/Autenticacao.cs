using System.Diagnostics;
using System.Text.Json.Nodes;
using Flow4Tech.Identidade.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;

namespace Flow4Tech.Identidade.Application;

/// <summary>
/// Validates a credential (spec 5.2.1, 5.2.4, 5.2.8). It issues NO token and starts NO session (ticket 04) and has no HTTP route yet. Properties:
/// <list type="bullet">
/// <item>One failure for everything: unknown organization, user or e-mail, wrong password, deactivated account.</item>
/// <item>The same hashing work on every path: with no account, the password is verified against a fake hash (<see cref="IPasswordHasher.VerifyDummyAsync"/>).</item>
/// <item>Every outcome waits for the same response-time floor (<see cref="IResponseTimeFloor"/>), which also hides the cost of the audit write of a known account.</item>
/// <item>A right TEMPORARY credential yields <see cref="DeveTrocarSenha"/>: nothing but <see cref="TrocaDeSenha"/> is allowed.</item>
/// <item>Failures of KNOWN accounts are audited (<c>autenticacao:falhar</c>, <c>negado</c>, no secret); an unknown organization has no audit chain to write to.</item>
/// </list>
/// Throttling and lockout after repeated failures are ticket 08. An exception from the audit store of a known account would surface as an error; accepted, it is an outage.
/// </summary>
public sealed class Autenticacao(
    IPreAuthLookup lookup,
    IPasswordHasher hasher,
    IUnitOfWork unitOfWork,
    IContaStore contas,
    IAuditLog audit,
    IResponseTimeFloor floor)
{
    public Task<ResultadoAutenticacao> AutenticarOperacionalAsync(Guid organizationId, string usuario, string senha, CancellationToken cancellationToken = default) =>
        AutenticarAsync(
            senha,
            ct => NomeDeUsuario.TryNormalize(usuario, out var normalizado) ? lookup.LocalizarOperacionalAsync(organizationId, normalizado, ct) : Task.FromResult<ContaParaLogin?>(null),
            cancellationToken);

    public Task<ResultadoAutenticacao> AutenticarPorEmailAsync(string email, string senha, CancellationToken cancellationToken = default) =>
        AutenticarAsync(
            senha,
            ct => EmailDeConta.TryNormalize(email, out var normalizado) ? lookup.LocalizarPorEmailAsync(normalizado, ct) : Task.FromResult<ContaParaLogin?>(null),
            cancellationToken);

    private async Task<ResultadoAutenticacao> AutenticarAsync(string? senha, Func<CancellationToken, Task<ContaParaLogin?>> find, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var result = await DecideAsync(senha, find, cancellationToken);
        await floor.AguardarAsync(started, cancellationToken);
        return result;
    }

    private async Task<ResultadoAutenticacao> DecideAsync(string? senha, Func<CancellationToken, Task<ContaParaLogin?>> find, CancellationToken cancellationToken)
    {
        if (senha is null || senha.Length > PoliticaDeSenha.MaximoCaracteres * 4)
        {
            return new FalhaDeAutenticacao(); // absurd input: refused before any lookup or hashing, the same for known and unknown accounts
        }

        var conta = await find(cancellationToken);
        var password = PoliticaDeSenha.Normalizar(senha);
        if (conta is null)
        {
            await hasher.VerifyDummyAsync(password, cancellationToken);
            return new FalhaDeAutenticacao();
        }

        var correct = await hasher.VerifyAsync(password, conta.SenhaHash, cancellationToken);
        if (!correct || !conta.Ativa)
        {
            await audit.RecordDetachedAsync(
                OrganizationContext.WithoutUnit(conta.OrganizacaoId),
                new AuditEntry
                {
                    Action = IdentidadeAuditActions.AutenticacaoFalhar,
                    Result = AuditResult.Negado,
                    ObjectType = "conta",
                    ObjectId = conta.ContaId.ToString("D"),
                    After = new JsonObject { ["motivo"] = correct ? "conta-desativada" : "senha-invalida" },
                },
                cancellationToken);
            return new FalhaDeAutenticacao();
        }

        if (hasher.NeedsRehash(conta.SenhaHash))
        {
            var novoHash = await hasher.HashAsync(password, cancellationToken);
            await unitOfWork.ExecuteAsync(
                OrganizationContext.WithoutUnit(conta.OrganizacaoId),
                ct => contas.RehashAsync(conta.OrganizacaoId, conta.ContaId, conta.SenhaHash, novoHash, ct),
                cancellationToken);
        }

        return conta.DeveTrocarSenha
            ? new DeveTrocarSenha(conta.ContaId, conta.OrganizacaoId, conta.Tipo, conta.VersaoCredencial)
            : new Autenticado(conta.ContaId, conta.OrganizacaoId, conta.Tipo, conta.VersaoCredencial);
    }
}
