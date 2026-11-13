import { BottomSheet, Button, formatBRL, Notice, TextField } from "@flow4tech/design-system";
import { useEffect, useState } from "react";
import { api } from "../sessao/sessao";
import { alternarSelecao, type FalhaDeMovimento, textoDoMovimento } from "./movimento";
import "./movimento.css";
import type { ItemDaConta } from "./rodada";
import { type Comanda, nomeExibido } from "./tipos";

interface Comum {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  comandaId: string;
  /** The comanda of this screen: what the sheet says it leaves from or joins. */
  nome: string;
  /** The highest comanda version the screen knows; the sheet raises it with what its writes answer. */
  versao: { current: number };
  /** The other open comandas the caller may see: where items go, what can be joined. */
  outras: readonly Comanda[];
  /** What blocks it now (no signal, actions of the comanda waiting in the queue); null when it can go. These operations are never queued. */
  bloqueio: string | null;
  /** Refreshes the bill, the items and the list: the numbers on screen are the server's after any answer. */
  onAtualizar: () => Promise<void>;
  /** The operation went through: the screen says what happened. */
  onFeito: (mensagem: string) => void;
}

const ITENS = (n: number) => `${n} ${n === 1 ? "item" : "itens"}`;

function valorDaLinha(item: ItemDaConta): number {
  return item.tipo === "cortesia" || item.tipo === "brinde" ? 0 : item.unitarioCentavos * item.quantidade;
}

/** One answer of the movement routes, read the same way by the three sheets: the data on success, else the sentence (the screen is refreshed first, the selection is never touched). */
async function executar<T extends { origem: { versao: number | string } }>(
  acao: "transferir" | "separar" | "juntar",
  comandaId: string,
  versao: { current: number },
  onAtualizar: () => Promise<void>,
  chamar: () => Promise<{ data?: T; error?: unknown; response: Response }>,
): Promise<{ data: T } | { erro: string }> {
  try {
    const { data, error, response } = await chamar();
    if (data) {
      versao.current = Math.max(versao.current, Number(data.origem.versao));
      await onAtualizar();
      return { data };
    }

    const falha = (error ?? {}) as FalhaDeMovimento;
    if (falha.comandaId === comandaId && typeof falha.versaoAtual === "number") {
      versao.current = Math.max(versao.current, falha.versaoAtual);
    }

    if (response.status === 404 || response.status === 409) {
      await onAtualizar();
    }

    return { erro: textoDoMovimento(acao, response.status, falha.codigo) };
  } catch {
    return { erro: "Sem conexão. Isto precisa do servidor e não fica na fila. Tente de novo quando o sinal voltar." };
  }
}

function ListaDeComandas({
  nome,
  comandas,
  escolhida,
  onEscolher,
  vazio,
}: {
  nome: string;
  comandas: readonly Comanda[];
  escolhida: string | null;
  onEscolher: (id: string) => void;
  vazio: string;
}) {
  if (comandas.length === 0) {
    return <p className="mov-vazio">{vazio}</p>;
  }

  return (
    <ul className="mov-lista" aria-label={nome}>
      {comandas.map((c) => (
        <li key={c.id}>
          <label className="mov-linha">
            <input type="radio" name="mov-comanda" checked={escolhida === c.id} onChange={() => onEscolher(c.id)} />
            <span className="mov-marca" data-forma="radio" aria-hidden="true" />
            <span className="mov-nome">{nomeExibido(c)}</span>
            <span className="mov-num f4-num">{c.numeroTexto}</span>
          </label>
        </li>
      ))}
    </ul>
  );
}

interface TransferirProps extends Comum {
  modo: "transferir" | "separar";
  /** The launched items of the comanda. */
  itens: readonly ItemDaConta[];
}

/**
 * Transfer items to another open comanda, or separate them into a new one: tick the items, pick where they go (or name the new comanda), confirm. It needs the server now and never waits in the
 * queue; a 409 refreshes the numbers, says why and KEEPS the ticked items so the confirm is one tap away.
 */
export function TransferirSheet({
  open,
  onOpenChange,
  modo,
  comandaId,
  nome,
  versao,
  itens,
  outras,
  bloqueio,
  onAtualizar,
  onFeito,
}: TransferirProps) {
  const [escolhidos, setEscolhidos] = useState<Set<string>>(new Set());
  const [destinoId, setDestinoId] = useState<string | null>(null);
  const [identificacao, setIdentificacao] = useState("");
  const [enviando, setEnviando] = useState(false);
  const [erro, setErro] = useState<string | undefined>(undefined);
  useEffect(() => {
    if (open) {
      setEscolhidos(new Set());
      setDestinoId(null);
      setIdentificacao("");
      setErro(undefined);
    }
  }, [open]);

  const marcados = itens.filter((i) => escolhidos.has(i.id));
  const destino = outras.find((c) => c.id === destinoId);
  const total = marcados.reduce((soma, i) => soma + valorDaLinha(i), 0);
  const pronto = marcados.length > 0 && (modo === "separar" || destino !== undefined) && bloqueio === null;
  const separar = modo === "separar";

  async function confirmar() {
    setErro(undefined);
    setEnviando(true);
    const itemIds = marcados.map((i) => i.id);
    const params = { path: { comandaId } };
    const resultado = separar
      ? await executar("separar", comandaId, versao, onAtualizar, () =>
          api.POST("/comandas/{comandaId}/itens/separar", {
            params,
            body: {
              versao: versao.current,
              itemIds,
              identificacao: identificacao.trim() === "" ? null : identificacao.trim(),
            },
          }),
        )
      : destino
        ? await executar("transferir", comandaId, versao, onAtualizar, () =>
            api.POST("/comandas/{comandaId}/itens/transferir", {
              params,
              body: { versao: versao.current, destinoId: destino.id, destinoVersao: destino.versao, itemIds },
            }),
          )
        : null;
    setEnviando(false);
    if (resultado === null) {
      return;
    }

    if ("erro" in resultado) {
      setErro(resultado.erro);
      return;
    }

    const movidos = Number(resultado.data.itensMovidos);
    onFeito(
      separar
        ? `${ITENS(movidos)} separado${movidos === 1 ? "" : "s"} na comanda ${resultado.data.destino.numeroTexto}.`
        : `${ITENS(movidos)} transferido${movidos === 1 ? "" : "s"} para ${destino ? nomeExibido(destino) : "a outra comanda"}.`,
    );
  }

  return (
    <BottomSheet
      open={open}
      onOpenChange={onOpenChange}
      title={separar ? "Separar itens" : "Transferir itens"}
      description={
        separar
          ? `Os itens marcados saem de ${nome} e vão para uma comanda nova, na mesma mesa.`
          : `Marque os itens de ${nome} e escolha a comanda que recebe.`
      }
      loading={enviando}
      footer={
        <div className="mov-rodape">
          {bloqueio ? (
            <p role="status" className="mov-bloqueio">
              {bloqueio}
            </p>
          ) : null}
          {erro ? (
            <p role="alert" className="f4-field-error">
              {erro}
            </p>
          ) : null}
          <p className="mov-resumo f4-num" aria-live="polite">
            {marcados.length === 0
              ? "Nenhum item marcado"
              : `${ITENS(marcados.length)} · ${formatBRL(total)}${destino ? ` para ${nomeExibido(destino)}` : ""}`}
          </p>
          <div className="mov-botoes">
            <Button variant="secondary" onClick={() => onOpenChange(false)} disabled={enviando}>
              Voltar
            </Button>
            <Button loading={enviando} disabled={!pronto} onClick={() => void confirmar()}>
              {separar ? "Separar" : "Transferir"}
            </Button>
          </div>
        </div>
      }
    >
      <p className="mov-rotulo" id="mov-itens-rotulo">
        Itens
      </p>
      {itens.length === 0 ? (
        <p className="mov-vazio">Esta comanda não tem itens lançados.</p>
      ) : (
        <ul className="mov-lista" aria-labelledby="mov-itens-rotulo">
          {itens.map((i) => (
            <li key={i.id}>
              <label className="mov-linha">
                <input
                  type="checkbox"
                  checked={escolhidos.has(i.id)}
                  onChange={() => setEscolhidos((atual) => alternarSelecao(atual, i.id))}
                />
                <span className="mov-marca" data-forma="caixa" aria-hidden="true" />
                <span className="mov-nome">
                  {i.quantidade}× {i.produtoNome}
                </span>
                <span className="mov-num f4-num">{formatBRL(valorDaLinha(i))}</span>
              </label>
            </li>
          ))}
        </ul>
      )}
      {separar ? (
        <TextField
          label="Nome da comanda nova (opcional)"
          value={identificacao}
          maxLength={40}
          autoComplete="off"
          onChange={(e) => setIdentificacao(e.target.value)}
        />
      ) : (
        <>
          <p className="mov-rotulo" id="mov-destino-rotulo">
            Vai para
          </p>
          <ListaDeComandas
            nome="Comandas que podem receber"
            comandas={outras}
            escolhida={destinoId}
            onEscolher={setDestinoId}
            vazio="Não há outra comanda aberta para receber os itens."
          />
        </>
      )}
    </BottomSheet>
  );
}

/**
 * Merge another open comanda INTO this one: pick it, read what will happen, confirm. The other comanda closes as joined and its items come here; it cannot be undone, so the confirmation is its
 * own step. A comanda with discount or payment is refused by the server and the sheet says so.
 */
export function JuntarSheet({
  open,
  onOpenChange,
  comandaId,
  nome,
  versao,
  outras,
  bloqueio,
  onAtualizar,
  onFeito,
}: Comum) {
  const [origemId, setOrigemId] = useState<string | null>(null);
  const [confirmando, setConfirmando] = useState(false);
  const [enviando, setEnviando] = useState(false);
  const [erro, setErro] = useState<string | undefined>(undefined);
  useEffect(() => {
    if (open) {
      setOrigemId(null);
      setConfirmando(false);
      setErro(undefined);
    }
  }, [open]);

  const origem = outras.find((c) => c.id === origemId);

  async function confirmar() {
    if (!origem) {
      return;
    }

    setErro(undefined);
    setEnviando(true);
    const resultado = await executar("juntar", comandaId, versao, onAtualizar, () =>
      api.POST("/comandas/{comandaId}/juntar", {
        params: { path: { comandaId } },
        body: { versao: versao.current, origemId: origem.id, origemVersao: origem.versao },
      }),
    );
    setEnviando(false);
    if ("erro" in resultado) {
      // The choice stays; the person goes back to it to read the refusal next to the list.
      setErro(resultado.erro);
      setConfirmando(false);
      return;
    }

    onFeito(`${nomeExibido(origem)} (${origem.numeroTexto}) foi juntada em ${nome}.`);
  }

  return (
    <BottomSheet
      open={open}
      onOpenChange={onOpenChange}
      title="Juntar comandas"
      description={confirmando ? undefined : `Escolha a comanda que entra em ${nome}. Os itens dela vêm para cá.`}
      loading={enviando}
      footer={
        <div className="mov-rodape">
          {bloqueio ? (
            <p role="status" className="mov-bloqueio">
              {bloqueio}
            </p>
          ) : null}
          {erro ? (
            <p role="alert" className="f4-field-error">
              {erro}
            </p>
          ) : null}
          <div className="mov-botoes">
            <Button
              variant="secondary"
              onClick={() => (confirmando ? setConfirmando(false) : onOpenChange(false))}
              disabled={enviando}
            >
              Voltar
            </Button>
            {confirmando ? (
              <Button loading={enviando} disabled={bloqueio !== null} onClick={() => void confirmar()}>
                Juntar comandas
              </Button>
            ) : (
              <Button disabled={!origem || bloqueio !== null} onClick={() => setConfirmando(true)}>
                Continuar
              </Button>
            )}
          </div>
        </div>
      }
    >
      {confirmando && origem ? (
        <Notice tone="warning" title="Não dá para desfazer">
          {`${nomeExibido(origem)} (${origem.numeroTexto}) fecha como juntada e todos os itens dela passam para ${nome}. A mesa dela fica livre.`}
        </Notice>
      ) : (
        <ListaDeComandas
          nome="Comandas que podem ser juntadas"
          comandas={outras}
          escolhida={origemId}
          onEscolher={setOrigemId}
          vazio="Não há outra comanda aberta para juntar."
        />
      )}
    </BottomSheet>
  );
}
