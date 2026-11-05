using System.Globalization;
using System.Text.Json;
using Flow4Tech.Identidade.Application;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;

namespace Flow4Tech.Identidade.Infrastructure;

/// <summary>
/// Turns the security events of the outbox into e-mail alerts (spec 5.5.5, 5.5.6): a new login and a lock go to the person (if the account has an e-mail) AND to the owners of the
/// organization; a password change goes to the person. Operational accounts have no e-mail, so for them only the owners are told (the in-app channel arrives in phase 1B).
/// Each consumer is idempotent on the event id (inbox) and runs inside the tenant transaction the dispatcher opened, so the accounts are read under RLS of the event's organization.
/// The e-mail itself is an effect outside the database: if the provider accepts and the commit then fails, the retry sends it again (at-least-once). No message carries a secret.
/// </summary>
public static class AlertasDeSeguranca
{
    private static readonly JsonSerializerOptions Json = new();

    public static IReadOnlyList<IOutboxConsumer> Consumidores(IProcessedEventStore inbox, IEnviadorDeEmail email) =>
    [
        new IdempotentConsumer("identidade.alerta-login-novo", LoginNovo.Type, inbox, (message, tx, ct) => LoginNovoAsync(email, message, tx, ct)),
        new IdempotentConsumer("identidade.alerta-conta-bloqueada", ContaBloqueada.Type, inbox, (message, tx, ct) => ContaBloqueadaAsync(email, message, tx, ct)),
        new IdempotentConsumer("identidade.alerta-senha-trocada", SenhaTrocada.Type, inbox, (message, tx, ct) => SenhaTrocadaAsync(email, message, tx, ct)),
    ];

    private static async Task LoginNovoAsync(IEnviadorDeEmail email, OutboxMessage message, TenantTransaction tx, CancellationToken ct)
    {
        var evento = JsonSerializer.Deserialize<LoginNovo>(message.Payload, Json) ?? throw new InvalidOperationException("Malformed security event.");
        var (pessoa, donos) = await DestinatariosAsync(tx, evento.ContaId, ct);
        var quando = Quando(evento.DetectadoEm);
        var oQue = (evento.AparelhoNovo ? "um aparelho novo" : "") + (evento.AparelhoNovo && evento.RedeNova ? " e " : "") + (evento.RedeNova ? "uma rede nova" : "");
        var detalhe = $"Quando: {quando}\nRede: {evento.Rede ?? "desconhecida"}\nAparelho: {evento.Aparelho ?? "desconhecido"}";

        foreach (var destino in Unicos(pessoa, donos))
        {
            var propria = pessoa is not null && destino == pessoa.Email;
            var texto = propria
                ? $"Houve um acesso à sua conta Flow4Tech a partir de {oQue}.\n{detalhe}\n\nSe foi você, não precisa fazer nada. Se não foi, troque a senha agora e encerre as outras sessões."
                : $"Houve um acesso à conta de {pessoa?.Nome ?? "um usuário"} a partir de {oQue}.\n{detalhe}\n\nSe não reconhece este acesso, desative a conta ou peça a troca da senha.";
            await email.EnviarAsync(new MensagemDeEmail(destino, "Novo acesso à sua conta Flow4Tech", texto), ct);
        }
    }

    private static async Task ContaBloqueadaAsync(IEnviadorDeEmail email, OutboxMessage message, TenantTransaction tx, CancellationToken ct)
    {
        var evento = JsonSerializer.Deserialize<ContaBloqueada>(message.Payload, Json) ?? throw new InvalidOperationException("Malformed security event.");
        var (pessoa, donos) = await DestinatariosAsync(tx, evento.ContaId, ct);
        var ate = Quando(evento.BloqueadaAte);
        foreach (var destino in Unicos(pessoa, donos))
        {
            var propria = pessoa is not null && destino == pessoa.Email;
            var texto = propria
                ? $"Houve muitas tentativas de acesso à sua conta Flow4Tech e ela foi bloqueada temporariamente, até {ate}.\n\nSe não foi você, alguém pode estar tentando entrar na sua conta: troque a senha assim que puder."
                : $"A conta de {pessoa?.Nome ?? "um usuário"} foi bloqueada temporariamente (até {ate}) por excesso de tentativas de acesso.";
            await email.EnviarAsync(new MensagemDeEmail(destino, "Conta bloqueada temporariamente", texto), ct);
        }
    }

    private static async Task SenhaTrocadaAsync(IEnviadorDeEmail email, OutboxMessage message, TenantTransaction tx, CancellationToken ct)
    {
        var evento = JsonSerializer.Deserialize<SenhaTrocada>(message.Payload, Json) ?? throw new InvalidOperationException("Malformed security event.");
        var (pessoa, _) = await DestinatariosAsync(tx, evento.ContaId, ct);
        if (pessoa is { Email: { } destino })
        {
            await email.EnviarAsync(
                new MensagemDeEmail(destino, "Sua senha foi alterada", $"A senha da sua conta Flow4Tech foi alterada em {Quando(evento.TrocadaEm)} e as outras sessões foram encerradas.\n\nSe não foi você, entre em contato com o responsável da sua organização imediatamente."),
                ct);
        }
    }

    private sealed record Pessoa(string Nome, string? Email);

    /// <summary>The person (with e-mail or not) and the e-mails of the active owners, under RLS of the event's organization. A missing account gives no recipient at all.</summary>
    private static async Task<(Pessoa? Pessoa, List<string> Donos)> DestinatariosAsync(TenantTransaction tx, Guid contaId, CancellationToken ct)
    {
        await using var context = new IdentidadeDbContext(tx);
        var conta = await context.Contas.AsNoTracking().Where(c => c.Id == contaId).Select(c => new { c.Nome, c.Email, c.Ativa }).FirstOrDefaultAsync(ct);
        if (conta is null)
        {
            return (null, []);
        }

        var donos = await context.Contas.AsNoTracking().Where(c => c.Dono && c.Ativa && c.Email != null).OrderBy(c => c.Id).Select(c => c.Email!).ToListAsync(ct);
        return (new Pessoa(conta.Nome, conta.Ativa ? conta.Email : null), donos);
    }

    private static List<string> Unicos(Pessoa? pessoa, List<string> donos)
    {
        var lista = new List<string>();
        if (pessoa?.Email is { } email)
        {
            lista.Add(email);
        }

        lista.AddRange(donos.Where(d => !lista.Contains(d, StringComparer.OrdinalIgnoreCase)));
        return lista;
    }

    private static string Quando(DateTimeOffset instante) => instante.UtcDateTime.ToString("dd/MM/yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
