import { BottomSheet, Button, Chip, Notice, ToneBadge } from "@flow4tech/design-system";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { usePermissoes } from "../sessao/permissoes";
import { api, contaDaSessao } from "../sessao/sessao";
import {
  acoesPermitidas,
  ehOProprio,
  nomesDasUnidades,
  nomesDosPerfis,
  type Perfil,
  type Pessoa,
  textoDeAcoes,
} from "./logica";
import "./usuarios.css";

interface Dados {
  pessoas: Pessoa[];
  perfis: Perfil[];
  unidades: Map<string, string>;
}

/** A refusal by right is a bare 403: the screen says "sem permissão" and never why. */
class Recusado extends Error {}

async function carregar(): Promise<Dados> {
  const [contas, perfis, unidades] = await Promise.all([api.GET("/contas"), api.GET("/perfis"), api.GET("/unidades")]);
  if (contas.response.status === 403 || perfis.response.status === 403) {
    throw new Recusado();
  }

  if (!contas.data || !perfis.data) {
    throw new Error("pessoas");
  }

  // Unit names are a nicety: without them the rows still list the people.
  return {
    pessoas: contas.data,
    perfis: perfis.data,
    unidades: new Map((unidades.data ?? []).map((u) => [u.id, u.nome])),
  };
}

/** Loading looks like what will load: the title and rows of three lines (name, unit, profile), never a centered spinner. */
function EsqueletoDePessoas() {
  return (
    <section role="status" aria-label="Carregando as pessoas" aria-busy="true">
      <div className="f4-skeleton pessoas-esq-titulo" aria-hidden="true" />
      <div className="f4-skeleton pessoas-esq-lead" aria-hidden="true" />
      {[0, 1, 2, 3, 4].map((i) => (
        <div key={i} className="pessoas-esq-linha" aria-hidden="true">
          <div className="f4-skeleton pessoas-esq-nome" />
          <div className="f4-skeleton pessoas-esq-meta" />
          <div className="f4-skeleton pessoas-esq-selo" />
        </div>
      ))}
    </section>
  );
}

const SEM_PERMISSAO = "Você não tem permissão para isso.";

export function PessoasPage() {
  const queryClient = useQueryClient();
  const permissoes = usePermissoes();
  const [aberta, setAberta] = useState<string | null>(null);
  const [emCurso, setEmCurso] = useState<string | null>(null);
  const [erro, setErro] = useState<string | null>(null);
  const [criando, setCriando] = useState(false);
  const [erroModelos, setErroModelos] = useState<string | null>(null);
  const [criados, setCriados] = useState<string[] | null>(null);

  const dados = useQuery({ queryKey: ["pessoas"], queryFn: carregar });

  async function criarModelos() {
    setErroModelos(null);
    setCriando(true);
    try {
      const { data, response } = await api.POST("/perfis/modelos");
      if (data) {
        setCriados(data.filter((m) => m.criado).map((m) => m.nome));
        await queryClient.invalidateQueries({ queryKey: ["pessoas"] });
      } else {
        setErroModelos(
          response.status === 403 ? SEM_PERMISSAO : "Não foi possível criar os perfis modelo. Tente de novo.",
        );
      }
    } catch {
      setErroModelos("Sem conexão. Tente de novo.");
    } finally {
      setCriando(false);
    }
  }

  async function alternar(pessoa: Pessoa, perfil: Perfil) {
    setErro(null);
    setEmCurso(perfil.id);
    const tem = pessoa.perfilIds.includes(perfil.id);
    const caminho = { params: { path: { id: pessoa.id, perfilId: perfil.id } } };
    try {
      const { response } = tem
        ? await api.DELETE("/contas/{id}/perfis/{perfilId}", caminho)
        : await api.PUT("/contas/{id}/perfis/{perfilId}", caminho);
      if (response.ok) {
        await queryClient.invalidateQueries({ queryKey: ["pessoas"] });
      } else if (response.status === 403) {
        setErro(SEM_PERMISSAO);
      } else if (response.status === 404) {
        setErro("Esta pessoa ou este perfil não existe mais. Atualize a tela.");
      } else {
        setErro("Não foi possível salvar. Tente de novo.");
      }
    } catch {
      setErro("Sem conexão. Tente de novo.");
    } finally {
      setEmCurso(null);
    }
  }

  if (!permissoes.carregado || dados.isPending) {
    return <EsqueletoDePessoas />;
  }

  if (!permissoes.pode("perfil:ver") || dados.error instanceof Recusado) {
    return <Notice tone="info">{SEM_PERMISSAO}</Notice>;
  }

  if (dados.isError) {
    return (
      <Notice
        tone="error"
        action={
          <Button variant="secondary" onClick={() => void dados.refetch()}>
            Tentar novamente
          </Button>
        }
      >
        Não foi possível carregar as pessoas.
      </Notice>
    );
  }

  const { pessoas, perfis, unidades } = dados.data;
  const podeAtribuir = permissoes.pode("permissao:atribuir");
  const podeCriarModelos = permissoes.pode("perfil:gerir") && perfis.length === 0;
  const selecionada = pessoas.find((p) => p.id === aberta) ?? null;
  const proprio = selecionada !== null && ehOProprio(selecionada, contaDaSessao());

  return (
    <section aria-labelledby="pessoas-titulo">
      <h2 id="pessoas-titulo" className="app-empty-title">
        Pessoas
      </h2>
      <p className="pessoas-lead">Quem trabalha com você e o perfil de cada um.</p>

      {criados && criados.length > 0 ? (
        <Notice tone="info">{`Perfis criados: ${criados.join(", ")}. Agora escolha o perfil de cada pessoa.`}</Notice>
      ) : null}

      {podeCriarModelos ? (
        <div className="pessoas-modelos">
          <p>Ainda não há perfis. Crie os modelos Garçom, Caixa e Gerente com um toque.</p>
          <Button loading={criando} error={erroModelos ?? undefined} onClick={() => void criarModelos()}>
            Criar perfis modelo
          </Button>
        </div>
      ) : null}

      {pessoas.length === 0 ? (
        <p className="pessoas-lead">
          Nenhuma pessoa cadastrada. Quando o dono convidar alguém, a pessoa aparece aqui para receber um perfil.
        </p>
      ) : (
        <ul className="pessoas-lista" aria-label="Pessoas">
          {pessoas.map((p) => {
            const perfisDela = nomesDosPerfis(p, perfis);
            const lugares = nomesDasUnidades(p, unidades);
            const conteudo = (
              <>
                <span className="pessoa-nome">{p.nome}</span>
                <span className="pessoa-meta">
                  {[p.usuario, lugares.length > 0 ? lugares.join(", ") : "Sem unidade"].filter(Boolean).join(" · ")}
                </span>
                <span className="pessoa-perfis">
                  {p.dono ? <ToneBadge tone="success">Dono</ToneBadge> : null}
                  {!p.ativa ? <ToneBadge tone="alert">Inativa</ToneBadge> : null}
                  {perfisDela.length === 0 && !p.dono ? <ToneBadge tone="warning">Sem perfil</ToneBadge> : null}
                  {perfisDela.map((nome) => (
                    <ToneBadge key={nome} tone="neutral">
                      {nome}
                    </ToneBadge>
                  ))}
                </span>
              </>
            );
            return (
              <li key={p.id}>
                {podeAtribuir ? (
                  <button
                    type="button"
                    className="pessoa-linha"
                    onClick={() => {
                      setErro(null);
                      setAberta(p.id);
                    }}
                  >
                    <span className="pessoa-corpo">{conteudo}</span>
                    <span className="pessoa-seta" aria-hidden="true">
                      ›
                    </span>
                  </button>
                ) : (
                  <div className="pessoa-linha">
                    <span className="pessoa-corpo">{conteudo}</span>
                  </div>
                )}
              </li>
            );
          })}
        </ul>
      )}

      <BottomSheet
        open={selecionada !== null}
        onOpenChange={(open) => {
          if (!open) {
            setAberta(null);
          }
        }}
        title={selecionada?.nome ?? "Pessoa"}
        description="Escolha os perfis desta pessoa. Ela entra de novo para valer."
        error={erro ?? undefined}
        loading={emCurso !== null}
        footer={<Button onClick={() => setAberta(null)}>Pronto</Button>}
      >
        {selecionada && proprio ? (
          <Notice tone="info">Você não pode mudar o seu próprio perfil. Peça a outra pessoa com acesso.</Notice>
        ) : selecionada && perfis.length === 0 ? (
          <Notice tone="info">Não há perfis ainda. Crie os perfis modelo na lista.</Notice>
        ) : selecionada ? (
          <>
            <div className="pessoa-sheet-perfis" role="group" aria-label="Perfis">
              {perfis.map((perfil) => (
                <Chip
                  key={perfil.id}
                  selected={selecionada.perfilIds.includes(perfil.id)}
                  loading={emCurso === perfil.id}
                  disabled={emCurso !== null}
                  onClick={() => void alternar(selecionada, perfil)}
                >
                  {perfil.nome}
                </Chip>
              ))}
            </div>
            <ul className="pessoa-sheet-resumo" aria-label="O que cada perfil permite">
              {perfis.map((perfil) => (
                <li key={perfil.id}>{`${perfil.nome}: ${textoDeAcoes(acoesPermitidas(perfil))}`}</li>
              ))}
            </ul>
          </>
        ) : null}
      </BottomSheet>
    </section>
  );
}
