using Flow4Tech.Identidade.Application;
using Flow4Tech.Identidade.Domain;
using Flow4Tech.Kernel;

namespace Flow4Tech.Identidade.Tests;

/// <summary>In-memory accounts for the invitation / recovery / Google tests (no database). Same contract as <see cref="IContaStore"/>.</summary>
internal sealed class FakeContas : IContaStore
{
    public Dictionary<Guid, (Conta Conta, string Hash, bool DeveTrocar, DateTimeOffset? Expira)> Linhas { get; } = [];

    public HashSet<string> EmailsDaPlataforma { get; } = [];

    public List<Guid> SenhasTrocadas { get; } = [];

    public Task<IReadOnlyList<ContaListada>> ListarAsync(Guid organizacaoId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ContaListada>>([.. Linhas.Values.Where(l => l.Conta.OrganizacaoId == organizacaoId).Select(l => new ContaListada(l.Conta.Id, l.Conta.Nome, l.Conta.Usuario, l.Conta.Dono, l.Conta.Ativa, []))]);

    public Conta Adicionar(Guid org, TipoConta tipo, string senha, string? email = null, string? usuario = null, bool ativa = true, bool dono = false, bool deveTrocar = false)
    {
        var conta = new Conta(Guid.NewGuid(), org, tipo, "Pessoa", email, usuario, dono, ativa, 1);
        Linhas[conta.Id] = (conta, "hash:" + senha, deveTrocar, null);
        if (email is not null)
        {
            EmailsDaPlataforma.Add(email);
        }

        return conta;
    }

    public Task<bool> InserirAsync(Guid organizacaoId, NovaConta conta, string senhaHash, bool deveTrocarSenha, DateTimeOffset? temporariaExpiraEm, CancellationToken cancellationToken)
    {
        Linhas[conta.Id] = (new Conta(conta.Id, organizacaoId, conta.Tipo, conta.Nome, conta.Email, conta.Usuario, conta.Dono, true, 1), senhaHash, deveTrocarSenha, temporariaExpiraEm);
        return Task.FromResult(true);
    }

    public Task<bool> RegistrarEmailAsync(Guid organizacaoId, Guid contaId, string emailNormalizado, CancellationToken cancellationToken) => Task.FromResult(EmailsDaPlataforma.Add(emailNormalizado));

    public Task<Conta?> ObterAsync(Guid organizacaoId, Guid contaId, CancellationToken cancellationToken) =>
        Task.FromResult(Linhas.TryGetValue(contaId, out var l) && l.Conta.OrganizacaoId == organizacaoId ? l.Conta : null);

    public Task<ContaParaLogin?> ObterComCredencialAsync(Guid organizacaoId, Guid contaId, CancellationToken cancellationToken) =>
        Task.FromResult(Linhas.TryGetValue(contaId, out var l) && l.Conta.OrganizacaoId == organizacaoId
            ? new ContaParaLogin(l.Conta.Id, organizacaoId, l.Conta.Tipo, l.Hash, l.DeveTrocar, l.Conta.Ativa, l.Conta.VersaoCredencial, l.Expira)
            : null);

    public Task TrocarSenhaAsync(Guid organizacaoId, Guid contaId, string novoHash, CancellationToken cancellationToken)
    {
        var l = Linhas[contaId];
        Linhas[contaId] = (l.Conta with { VersaoCredencial = l.Conta.VersaoCredencial + 1 }, novoHash, false, null);
        SenhasTrocadas.Add(contaId);
        return Task.CompletedTask;
    }

    public Task<bool> RedefinirCredencialTemporariaAsync(Guid organizacaoId, Guid contaId, string novoHash, DateTimeOffset expiraEm, CancellationToken cancellationToken)
    {
        if (!Linhas.TryGetValue(contaId, out var l) || l.Conta.OrganizacaoId != organizacaoId || l.Conta.Tipo != TipoConta.Operacional || !l.Conta.Ativa)
        {
            return Task.FromResult(false);
        }

        Linhas[contaId] = (l.Conta with { VersaoCredencial = l.Conta.VersaoCredencial + 1 }, novoHash, true, expiraEm);
        return Task.FromResult(true);
    }

    public Task<bool> RehashAsync(Guid organizacaoId, Guid contaId, string hashAnterior, string novoHash, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<bool> DesativarAsync(Guid organizacaoId, Guid contaId, DateTimeOffset quando, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<bool> DefinirDonoAsync(Guid organizacaoId, Guid contaId, bool dono, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<int> TravarOrganizacaoEContarContasAsync(Guid organizacaoId, CancellationToken cancellationToken) => Task.FromResult(Linhas.Count);

    public Task<int> TravarDonosAtivosAsync(Guid organizacaoId, CancellationToken cancellationToken) => Task.FromResult(Linhas.Values.Count(l => l.Conta.Dono && l.Conta.Ativa));
}

internal sealed class FakeConvites : IConvitesStore
{
    public List<(Guid Org, NovoConvite Convite, DateTimeOffset? UsadoEm, DateTimeOffset? RevogadoEm)> Linhas { get; } = [];

    public HashSet<string> MembrosDaOrganizacao { get; } = [];

    public bool PerfilInvalido { get; set; }

    public Task<bool> EmailJaEhMembroAsync(Guid organizacaoId, string email, CancellationToken cancellationToken) => Task.FromResult(MembrosDaOrganizacao.Contains(email));

    public Task RevogarPendentesDoEmailAsync(Guid organizacaoId, string email, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        for (var i = 0; i < Linhas.Count; i++)
        {
            var l = Linhas[i];
            if (l.Org == organizacaoId && l.Convite.Email == email && l.UsadoEm is null && l.RevogadoEm is null)
            {
                Linhas[i] = l with { RevogadoEm = agora };
            }
        }

        return Task.CompletedTask;
    }

    public Task InserirAsync(Guid organizacaoId, NovoConvite convite, CancellationToken cancellationToken)
    {
        if (PerfilInvalido)
        {
            throw new ReferenciaInvalidaException();
        }

        Linhas.Add((organizacaoId, convite, null, null));
        return Task.CompletedTask;
    }

    public Task<ConviteEncontrado?> ObterPorHashAsync(Guid organizacaoId, byte[] hash, CancellationToken cancellationToken)
    {
        var l = Linhas.FirstOrDefault(x => x.Org == organizacaoId && x.Convite.TokenHash.Span.SequenceEqual(hash));
        return Task.FromResult<ConviteEncontrado?>(l.Convite is null
            ? null
            : new ConviteEncontrado(l.Convite.Id, l.Convite.Email, l.Convite.Nome, l.Convite.PerfilId, l.Convite.UnidadeId, l.Convite.CriadoPor, l.Convite.ExpiraEm, l.UsadoEm, l.RevogadoEm));
    }

    public Task<bool> MarcarUsadoAsync(Guid organizacaoId, Guid conviteId, Guid contaId, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        var i = Linhas.FindIndex(x => x.Org == organizacaoId && x.Convite.Id == conviteId && x.UsadoEm is null && x.RevogadoEm is null && x.Convite.ExpiraEm > agora);
        if (i < 0)
        {
            return Task.FromResult(false);
        }

        Linhas[i] = Linhas[i] with { UsadoEm = agora };
        return Task.FromResult(true);
    }

    public Task<bool> RevogarAsync(Guid organizacaoId, Guid conviteId, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        var i = Linhas.FindIndex(x => x.Org == organizacaoId && x.Convite.Id == conviteId && x.UsadoEm is null && x.RevogadoEm is null);
        if (i < 0)
        {
            return Task.FromResult(false);
        }

        Linhas[i] = Linhas[i] with { RevogadoEm = agora };
        return Task.FromResult(true);
    }
}

internal sealed class FakeRecuperacoes : IRecuperacoesStore
{
    public List<(Guid Org, Guid Id, Guid ContaId, byte[] Hash, DateTimeOffset ExpiraEm, DateTimeOffset? UsadoEm)> Linhas { get; } = [];

    public Task InvalidarPendentesAsync(Guid organizacaoId, Guid contaId, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        for (var i = 0; i < Linhas.Count; i++)
        {
            if (Linhas[i].Org == organizacaoId && Linhas[i].ContaId == contaId && Linhas[i].UsadoEm is null)
            {
                Linhas[i] = Linhas[i] with { UsadoEm = agora };
            }
        }

        return Task.CompletedTask;
    }

    public Task InserirAsync(Guid organizacaoId, Guid id, Guid contaId, byte[] hash, DateTimeOffset agora, DateTimeOffset expiraEm, CancellationToken cancellationToken)
    {
        Linhas.Add((organizacaoId, id, contaId, hash, expiraEm, null));
        return Task.CompletedTask;
    }

    public Task<RecuperacaoEncontrada?> ObterPorHashAsync(Guid organizacaoId, byte[] hash, CancellationToken cancellationToken)
    {
        var l = Linhas.FirstOrDefault(x => x.Org == organizacaoId && x.Hash.AsSpan().SequenceEqual(hash));
        return Task.FromResult<RecuperacaoEncontrada?>(l.Hash is null ? null : new RecuperacaoEncontrada(l.Id, l.ContaId, l.ExpiraEm, l.UsadoEm));
    }

    public Task<bool> ConsumirAsync(Guid organizacaoId, Guid id, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        var i = Linhas.FindIndex(x => x.Org == organizacaoId && x.Id == id && x.UsadoEm is null && x.ExpiraEm > agora);
        if (i < 0)
        {
            return Task.FromResult(false);
        }

        Linhas[i] = Linhas[i] with { UsadoEm = agora };
        return Task.FromResult(true);
    }
}

internal sealed class FakeRevogacao : IRevogacaoDeSessoes
{
    public List<(Guid Org, Guid Conta, string Motivo)> Revogadas { get; } = [];

    public Task RevogarTodasDaContaAsync(Guid organizacaoId, Guid contaId, string motivo, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        Revogadas.Add((organizacaoId, contaId, motivo));
        return Task.CompletedTask;
    }
}

internal sealed class FakeEmail : IEnviadorDeEmail
{
    public List<MensagemDeEmail> Enviadas { get; } = [];

    public bool Falha { get; set; }

    public Task EnviarAsync(MensagemDeEmail mensagem, CancellationToken cancellationToken)
    {
        if (Falha)
        {
            throw new FalhaNoEnvioDeEmailException();
        }

        Enviadas.Add(mensagem);
        return Task.CompletedTask;
    }
}

internal sealed class FakeCompromised(bool compromised = false) : ICompromisedPasswordCheck
{
    public Task<bool> IsCompromisedAsync(string password, CancellationToken cancellationToken = default) => Task.FromResult(compromised);
}

internal sealed class FakeVinculos : IIdentidadesExternasStore
{
    public Dictionary<(Guid Conta, string Provedor), string> Linhas { get; } = [];

    public Task<string?> ObterSubAsync(Guid organizacaoId, Guid contaId, string provedor, CancellationToken cancellationToken) =>
        Task.FromResult(Linhas.TryGetValue((contaId, provedor), out var sub) ? sub : null);

    public Task<bool> InserirAsync(Guid organizacaoId, Guid contaId, string provedor, string subject, string email, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        if (Linhas.ContainsKey((contaId, provedor)) || Linhas.Any(l => l.Key.Provedor == provedor && l.Value == subject))
        {
            return Task.FromResult(false);
        }

        Linhas[(contaId, provedor)] = subject;
        return Task.FromResult(true);
    }

    public Task<bool> RemoverAsync(Guid organizacaoId, Guid contaId, string provedor, CancellationToken cancellationToken) => Task.FromResult(Linhas.Remove((contaId, provedor)));
}
