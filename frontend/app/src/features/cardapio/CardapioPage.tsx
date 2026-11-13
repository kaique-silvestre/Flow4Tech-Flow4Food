import { Button, formatBRL, Notice, Switch, ToneBadge } from "@flow4tech/design-system";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { usePermissoes } from "../sessao/permissoes";
import { api } from "../sessao/sessao";
import { CHAVE_DA_GESTAO, carregarGestao, type DadosDaGestao, Recusado, type Resultado } from "./dados";
import { FolhaDePreco } from "./FolhaDePreco";
import { agrupar, corpoDaAlteracao, filtrar, type Mudanca, type ProdutoGestao, paraProdutoGestao } from "./logica";
import "./cardapio.css";

const SEM_PERMISSAO = "Você não tem permissão para isso.";

function EsqueletoDoCardapio() {
  return (
    <section role="status" aria-label="Carregando o cardápio" aria-busy="true">
      <div className="f4-skeleton gestao-esq-titulo" aria-hidden="true" />
      <div className="f4-skeleton gestao-esq-busca" aria-hidden="true" />
      {[0, 1, 2, 3].map((i) => (
        <div key={i} className="gestao-esq-linha" aria-hidden="true">
          <div className="f4-skeleton gestao-esq-nome" />
          <div className="f4-skeleton gestao-esq-controles" />
        </div>
      ))}
    </section>
  );
}

function LinhaDoProduto({
  produto,
  emCurso,
  erro,
  onAlterar,
  onPreco,
}: {
  produto: ProdutoGestao;
  /** Which switch is waiting for the server, if any. */
  emCurso: "acabou" | "ativo" | null;
  erro: string | undefined;
  onAlterar: (mudanca: Mudanca) => void;
  onPreco: () => void;
}) {
  const noPreco = produto.precoEfetivoCentavos;
  const temSelo =
    produto.indisponivel || !produto.ativo || produto.promocao !== null || produto.precoDaUnidadeCentavos !== null;
  return (
    <li className="gestao-linha" data-parado={produto.indisponivel || !produto.ativo ? "true" : undefined}>
      <div className="gestao-topo">
        <span className="gestao-nome">
          {produto.nome}
          <span className="gestao-codigo">{produto.codigo}</span>
        </span>
        <span className="gestao-preco f4-num">{formatBRL(noPreco)}</span>
      </div>
      {temSelo ? (
        <div className="gestao-selos">
          {produto.indisponivel ? <ToneBadge tone="warning">Acabou</ToneBadge> : null}
          {!produto.ativo ? <ToneBadge tone="neutral">Inativo</ToneBadge> : null}
          {produto.promocao ? <ToneBadge tone="success">Promoção</ToneBadge> : null}
          {produto.precoDaUnidadeCentavos !== null ? <ToneBadge tone="neutral">Preço da unidade</ToneBadge> : null}
        </div>
      ) : null}
      <div className="gestao-controles">
        <Switch
          label="Acabou"
          aria-label={`Acabou: ${produto.nome}`}
          checked={produto.indisponivel}
          loading={emCurso === "acabou"}
          disabled={emCurso !== null}
          error={erro !== undefined}
          onCheckedChange={(indisponivel) => onAlterar({ indisponivel })}
        />
        <Switch
          label="Ativo"
          aria-label={`Ativo: ${produto.nome}`}
          checked={produto.ativo}
          loading={emCurso === "ativo"}
          disabled={emCurso !== null}
          error={erro !== undefined}
          onCheckedChange={(ativo) => onAlterar({ ativo })}
        />
        <Button
          variant="secondary"
          className="gestao-preco-botao"
          aria-label={`Preço de ${produto.nome}`}
          onClick={onPreco}
        >
          Preço
        </Button>
      </div>
      {erro ? (
        <p role="alert" className="f4-field-error">
          {erro}
        </p>
      ) : null}
    </li>
  );
}

/** Menu management on the phone: who may manage products toggles "acabou" and active in one tap and changes a price in a sheet. The server decides every write; the screen only shows its answer. */
export function CardapioPage() {
  const queryClient = useQueryClient();
  const permissoes = usePermissoes();
  const dados = useQuery({ queryKey: CHAVE_DA_GESTAO, queryFn: carregarGestao });
  const [busca, setBusca] = useState("");
  const [emCurso, setEmCurso] = useState<{ id: string; qual: "acabou" | "ativo" } | null>(null);
  const [erros, setErros] = useState<Record<string, string>>({});
  const [precoDe, setPrecoDe] = useState<string | null>(null);

  /** One write of a whole product. Success replaces the row with the server's answer and tells the waiter's grid to refresh. */
  async function gravar(produto: ProdutoGestao, mudanca: Mudanca): Promise<Resultado> {
    try {
      const { data, response } = await api.PUT("/cardapio/produtos/{id}", {
        params: { path: { id: produto.id } },
        body: corpoDaAlteracao(produto, mudanca),
      });
      if (data) {
        const novo = paraProdutoGestao(data);
        queryClient.setQueryData<DadosDaGestao>(CHAVE_DA_GESTAO, (atual) =>
          atual ? { ...atual, produtos: atual.produtos.map((p) => (p.id === novo.id ? novo : p)) } : atual,
        );
        // The waiter's product grid (and frequents) reads other keys under "cardapio": make them stale so the next look refetches.
        await queryClient.invalidateQueries({ queryKey: ["cardapio"], predicate: (q) => q.queryKey[1] !== "gestao" });
        return { ok: true };
      }

      if (response.status === 403) {
        return { ok: false, mensagem: SEM_PERMISSAO };
      }

      if (response.status === 409) {
        await dados.refetch();
        return {
          ok: false,
          mensagem: "Este produto foi alterado em outro aparelho. A lista foi atualizada; tente de novo.",
        };
      }

      if (response.status === 404) {
        await dados.refetch();
        return { ok: false, mensagem: "Este produto não existe mais. A lista foi atualizada." };
      }

      return { ok: false, mensagem: "Não foi possível salvar. Tente de novo." };
    } catch {
      return { ok: false, mensagem: "Sem conexão. Tente de novo." };
    }
  }

  async function alternar(produto: ProdutoGestao, qual: "acabou" | "ativo", mudanca: Mudanca) {
    setErros(({ [produto.id]: _, ...resto }) => resto);
    setEmCurso({ id: produto.id, qual });
    const r = await gravar(produto, mudanca);
    setEmCurso(null);
    if (!r.ok) {
      setErros((atual) => ({ ...atual, [produto.id]: r.mensagem }));
    }
  }

  if (!permissoes.carregado || dados.isPending) {
    return <EsqueletoDoCardapio />;
  }

  if (!permissoes.pode("produto:gerenciar") || dados.error instanceof Recusado) {
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
        Não foi possível carregar o cardápio.
      </Notice>
    );
  }

  const { produtos, categorias } = dados.data;
  const visiveis = filtrar(produtos, busca);
  const grupos = agrupar(visiveis, categorias);
  const emPreco = produtos.find((p) => p.id === precoDe) ?? null;

  return (
    <section aria-labelledby="cardapio-titulo">
      <h2 id="cardapio-titulo" className="app-empty-title">
        Cardápio
      </h2>
      <p className="gestao-lead">Marque o que acabou, desative o que saiu do cardápio e ajuste preços.</p>

      {produtos.length > 0 ? (
        <div className="f4-field gestao-busca">
          <label htmlFor="busca-gestao" className="f4-visually-hidden">
            Buscar produto
          </label>
          <input
            id="busca-gestao"
            className="f4-input"
            type="search"
            inputMode="search"
            placeholder="Buscar por nome ou código"
            autoComplete="off"
            value={busca}
            onChange={(e) => setBusca(e.target.value)}
          />
        </div>
      ) : null}

      {produtos.length === 0 ? (
        <p className="gestao-vazio">
          Nenhum produto no cardápio ainda. Importe o cardápio por planilha ou cadastre os produtos.
        </p>
      ) : visiveis.length === 0 ? (
        <p className="gestao-vazio">Nada com “{busca.trim()}”. Tente o código do produto.</p>
      ) : (
        grupos.map((grupo) => (
          <section key={grupo.id} className="gestao-grupo" aria-labelledby={`grupo-${grupo.id}`}>
            <h3 id={`grupo-${grupo.id}`} className="gestao-grupo-titulo" data-nivel={grupo.nivel}>
              {grupo.nome}
            </h3>
            {grupo.produtos.length > 0 ? (
              <ul className="gestao-lista" aria-label={grupo.nome}>
                {grupo.produtos.map((produto) => (
                  <LinhaDoProduto
                    key={produto.id}
                    produto={produto}
                    emCurso={emCurso?.id === produto.id ? emCurso.qual : null}
                    erro={erros[produto.id]}
                    onAlterar={(mudanca) =>
                      void alternar(produto, mudanca.ativo !== undefined ? "ativo" : "acabou", mudanca)
                    }
                    onPreco={() => setPrecoDe(produto.id)}
                  />
                ))}
              </ul>
            ) : null}
          </section>
        ))
      )}

      <FolhaDePreco
        produto={emPreco}
        onFechar={() => setPrecoDe(null)}
        onSalvar={(produto, mudanca) => gravar(produto, mudanca)}
      />
    </section>
  );
}
