using System.Text.Json.Nodes;
using Flow4Tech.Identidade.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;

namespace Flow4Tech.Identidade.Application;

/// <summary>
/// The person changes THEIR OWN password (spec 5.2.1: nobody sets another person's password). Always needs the current password (for a temporary credential, the
/// credential itself), so a stolen "must change" session cannot be used without it. The caller (ticket 04, the session) supplies the account of the session, never
/// a value from the request body.
/// </summary>
public sealed class TrocaDeSenha(IUnitOfWork unitOfWork, IContaStore contas, IPasswordHasher hasher, ICompromisedPasswordCheck compromised, IAuditLog audit, IClock clock, IEventosDeSeguranca eventos)
{
    public async Task<ResultadoTrocaDeSenha> TrocarAsync(Guid organizationId, Guid contaId, string senhaAtual, string novaSenha, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(senhaAtual);
        ArgumentNullException.ThrowIfNull(novaSenha);

        // Absurd input is refused before any lookup, normalization or hashing (NFKC + Argon2 on a huge string is a cheap denial of service). Same bound as the login.
        if (senhaAtual.Length > PoliticaDeSenha.LimiteDeEntrada)
        {
            return new ResultadoTrocaDeSenha(StatusTrocaDeSenha.SenhaAtualInvalida, []);
        }

        if (novaSenha.Length > PoliticaDeSenha.LimiteDeEntrada)
        {
            return new ResultadoTrocaDeSenha(StatusTrocaDeSenha.SenhaInvalida, [ProblemaDeSenha.Longa]);
        }

        var context = OrganizationContext.WithoutUnit(organizationId);
        var (conta, credencial) = await unitOfWork.ExecuteAsync(
            context,
            async ct => (await contas.ObterAsync(organizationId, contaId, ct), await contas.ObterComCredencialAsync(organizationId, contaId, ct)),
            cancellationToken);

        var atual = PoliticaDeSenha.Normalizar(senhaAtual);
        if (conta is null || credencial is null)
        {
            await hasher.VerifyDummyAsync(atual, cancellationToken); // same work as a real check
            return new ResultadoTrocaDeSenha(StatusTrocaDeSenha.SenhaAtualInvalida, []);
        }

        if (!conta.Ativa)
        {
            return new ResultadoTrocaDeSenha(StatusTrocaDeSenha.ContaInativa, []);
        }

        if (!await hasher.VerifyAsync(atual, credencial.SenhaHash, cancellationToken))
        {
            await audit.RecordDetachedAsync(context, Entry(contaId, AuditResult.Negado, "senha-atual-invalida"), cancellationToken);
            return new ResultadoTrocaDeSenha(StatusTrocaDeSenha.SenhaAtualInvalida, []);
        }

        // A temporary credential past its deadline no longer proves anything (same answer as a wrong current password, after the same hashing work).
        if (credencial.DeveTrocarSenha && credencial.TemporariaExpiraEm is { } deadline && deadline <= clock.UtcNow)
        {
            await audit.RecordDetachedAsync(context, Entry(contaId, AuditResult.Negado, "credencial-temporaria-vencida"), cancellationToken);
            return new ResultadoTrocaDeSenha(StatusTrocaDeSenha.SenhaAtualInvalida, []);
        }

        var problemas = PoliticaDeSenha.Avaliar(novaSenha, conta.Email, conta.Usuario).ToList();
        var nova = PoliticaDeSenha.Normalizar(novaSenha);
        if (string.Equals(nova, atual, StringComparison.Ordinal))
        {
            problemas.Add(ProblemaDeSenha.IgualASenhaAtual); // a temporary credential was seen by whoever created the account: the person must pick their own
        }

        if (problemas.Count > 0)
        {
            return new ResultadoTrocaDeSenha(StatusTrocaDeSenha.SenhaInvalida, problemas);
        }

        try
        {
            if (await compromised.IsCompromisedAsync(nova, cancellationToken))
            {
                return new ResultadoTrocaDeSenha(StatusTrocaDeSenha.SenhaVazada, []);
            }
        }
        catch (VerificacaoDeSenhaIndisponivelException)
        {
            return new ResultadoTrocaDeSenha(StatusTrocaDeSenha.VerificacaoIndisponivel, []);
        }

        var novoHash = await hasher.HashAsync(nova, cancellationToken);
        await unitOfWork.ExecuteAsync(
            context,
            async ct =>
            {
                await contas.TrocarSenhaAsync(organizationId, contaId, novoHash, ct);
                await audit.RecordAsync(context, Entry(contaId, AuditResult.Sucesso, null), ct);
                // Security event of spec 5.5.6, in the same unit of work: the person is told by e-mail (consumer) that the password changed.
                await eventos.PublicarAsync(context, new SenhaTrocada(EntityId.New().Value, organizationId, contaId, clock.UtcNow), ct);
            },
            cancellationToken);
        return new ResultadoTrocaDeSenha(StatusTrocaDeSenha.Trocada, []);
    }

    private static AuditEntry Entry(Guid contaId, AuditResult result, string? motivo) => new()
    {
        Action = IdentidadeAuditActions.ContaTrocarSenha,
        Result = result,
        UserId = contaId, // the person acts on their own account
        ObjectType = "conta",
        ObjectId = contaId.ToString("D"),
        After = motivo is null ? null : new JsonObject { ["motivo"] = motivo },
    };
}
