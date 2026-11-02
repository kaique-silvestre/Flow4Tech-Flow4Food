namespace Flow4Tech.Identidade.Domain;

public enum RecusaDeProtecao
{
    ContaInexistente,
    ContaDesativada,
    AtorDesativado,
    DonoNaoPodeSerDesativadoPorOutro,
    UltimoDono,
    AutoAlteracaoDePermissao,
    ApenasDonoAlteraDono,
    DonoNaoPodeSerRebaixado,
    SemMudanca,
    DonoPrecisaDeEmail,
    AtorDeveTrocarSenha,
}

/// <summary>
/// Protection rules of spec 5.2.7, pure; <c>null</c> means allowed. The model is deliberately strict: an owner is never deactivated or demoted by someone
/// else (not even another owner), nobody edits their own permissions, and an owner can leave only if another owner remains. A transfer of ownership
/// is a future, explicit operation; profiles and the rest of the permission model arrive with ticket 06.
/// </summary>
public static class ProtecaoDeConta
{
    public static RecusaDeProtecao? PodeDesativar(Conta? ator, Conta? alvo, int donosAtivos)
    {
        if (ator is null || alvo is null)
        {
            return RecusaDeProtecao.ContaInexistente;
        }

        if (!ator.Ativa)
        {
            return RecusaDeProtecao.AtorDesativado;
        }

        if (!alvo.Ativa)
        {
            return RecusaDeProtecao.ContaDesativada;
        }

        if (alvo.Dono)
        {
            if (alvo.Id != ator.Id)
            {
                return RecusaDeProtecao.DonoNaoPodeSerDesativadoPorOutro;
            }

            if (donosAtivos <= 1)
            {
                return RecusaDeProtecao.UltimoDono;
            }
        }

        return null;
    }

    public static RecusaDeProtecao? PodeDefinirDono(Conta? ator, Conta? alvo, bool tornarDono, int donosAtivos)
    {
        if (ator is null || alvo is null)
        {
            return RecusaDeProtecao.ContaInexistente;
        }

        if (!ator.Ativa)
        {
            return RecusaDeProtecao.AtorDesativado;
        }

        if (!alvo.Ativa)
        {
            return RecusaDeProtecao.ContaDesativada;
        }

        if (alvo.Id == ator.Id)
        {
            return RecusaDeProtecao.AutoAlteracaoDePermissao;
        }

        if (!ator.Dono)
        {
            return RecusaDeProtecao.ApenasDonoAlteraDono;
        }

        if (alvo.Dono == tornarDono && tornarDono)
        {
            return RecusaDeProtecao.SemMudanca;
        }

        if (!tornarDono)
        {
            return alvo.Dono ? RecusaDeProtecao.DonoNaoPodeSerRebaixado : RecusaDeProtecao.SemMudanca;
        }

        return alvo.Tipo == TipoConta.Email ? null : RecusaDeProtecao.DonoPrecisaDeEmail;
    }
}
