import {
  BottomSheet,
  Button,
  Chip,
  centsFromDigits,
  formatBRL,
  Keypad,
  Notice,
  Skeleton,
  ToneBadge,
} from "@flow4tech/design-system";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate } from "@tanstack/react-router";
import { useEffect, useRef, useState, useSyncExternalStore } from "react";
import { enfileirarPagamento, pagamentoNaFila, recebidoNaFila, temAcaoViva, useFila } from "../fila/fila";
import { usePermissoes } from "../sessao/permissoes";
import { api } from "../sessao/sessao";
import {
  alternarMetodo,
  alvoDoRecebimento,
  apagarValor,
  calcularRecebimento,
  digitarValor,
  type LinhaDeRecebimento,
  type MetodoDeRecebimento,
  type ParteParaReceber,
  partesParaReceber,
  textoDoProblema,
  textoDoRecebimento,
} from "./recebimento";
import "./recebimento.css";
import type { ContaDaComanda, ItemDaConta } from "./rodada";
import { uuidv7 } from "./uuid";

type ErroDeApi = { codigo?: string; versaoAtual?: number | null } | undefined;

interface Props {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  comandaId: string;
  nome: string;
  conta: ContaDaComanda;
  /** The launched items: the courtesies and gifts are listed in the bill as such. */
  itens?: readonly ItemDaConta[];
  /** The highest comanda version the screen knows; the sheet raises it with what its writes answer. */
  versao: { current: number };
  onAtualizar: () => Promise<void>;
}

type Fase =
  | { tipo: "digitando" }
  | { tipo: "fechada"; trocoCentavos: number }
  | { tipo: "parcial"; trocoCentavos: number; restaCentavos: number; parte: number | null }
  | { tipo: "naFila"; recebidoCentavos: number };

/** The only method of the old route: used when the list of methods cannot be read (no permission, no signal). */
const DINHEIRO_SIMPLES: MetodoDeRecebimento = { id: "dinheiro-simples", nome: "Dinheiro", dinheiro: true };

const GORJETA = "gorjeta";

function assinarOnline(ouvinte: () => void) {
  window.addEventListener("online", ouvinte);
  window.addEventListener("offline", ouvinte);
  return () => {
    window.removeEventListener("online", ouvinte);
    window.removeEventListener("offline", ouvinte);
  };
}

/** Re-renders when the signal comes or goes (the sheet's reason and button follow it live). */
function useOnline(): boolean {
  return useSyncExternalStore(
    assinarOnline,
    () => navigator.onLine,
    () => true,
  );
}

/**
 * Closing the bill (docs/design/refs/salao/05.png, F4-0064): consumption, the service fee (removable only by who may), the total, then HOW it is received: the active methods of the organization as
 * chips, one line per method with its amount on the keypad, the balance live (what is received, what is left, the change in large type for cash), an optional tip (pagamento:registrar-gorjeta),
 * several lines (pagamento:dividir) and, when the bill has a current split, whose part is being paid. The payment ids are made on the client, so a retry after a lost answer is one payment.
 * Only ONE line of cash with no tip and no part keeps the old route and its offline queue (the payment waits in the visible queue with no signal); everything else needs the server now and never waits.
 */
export function FecharContaSheet({
  open,
  onOpenChange,
  comandaId,
  nome,
  conta,
  itens = [],
  versao,
  onAtualizar,
}: Props) {
  const permissoes = usePermissoes();
  const navegar = useNavigate();
  const queryClient = useQueryClient();
  const online = useOnline();
  const [linhas, setLinhas] = useState<LinhaDeRecebimento[]>([]);
  const [ativo, setAtivo] = useState<string | null>(null);
  const [gorjeta, setGorjeta] = useState("");
  const [escolhaDaParte, setEscolhaDaParte] = useState<"toda" | number | null>(null);
  const [fase, setFase] = useState<Fase>({ tipo: "digitando" });
  const [enviando, setEnviando] = useState(false);
  const [erro, setErro] = useState<string | undefined>(undefined);
  const [semCaixa, setSemCaixa] = useState(false);
  // A payment of this comanda waiting in the queue is money the server does not know yet: while it waits the sheet is blocked, so the same amount is never received under a second payment id.
  const fila = useFila();
  const naFila = fila.acoes.some((a) => a.tipo === "pagamento" && a.comandaId === comandaId && a.estado !== "falhou");
  const filaDaComanda = temAcaoViva(comandaId);
  const guardado = recebidoNaFila(comandaId);
  const bloqueadoPelaFila = naFila && fase.tipo === "digitando";

  const saldo = conta.saldoCentavos;
  const podeVerCaixa = permissoes.pode("caixa:ver");
  const turno = useQuery({
    queryKey: ["caixa", "aberto"],
    enabled: open && podeVerCaixa,
    queryFn: async () => {
      const { data, response } = await api.GET("/caixa/turnos/atual");
      if (data) {
        return true;
      }

      if (response.status === 404) {
        return false;
      }

      throw new Error("caixa");
    },
  });
  const caixaFechado = semCaixa || turno.data === false;

  // The methods come from the API (the organization's active ones); without the permission to list them, or when the list cannot be read, the sheet falls back to the old cash-only receipt.
  const podeVerMetodos = permissoes.pode("pagamento:ver");
  const consultaMetodos = useQuery<MetodoDeRecebimento[]>({
    queryKey: ["pagamentos", "metodos"],
    enabled: open && podeVerMetodos,
    staleTime: 60_000,
    retry: false,
    networkMode: "always",
    queryFn: async () => {
      const { data } = await api.GET("/pagamentos/metodos");
      if (!data) {
        throw new Error("metodos");
      }

      return data
        .filter((m) => m.ativo)
        .sort((a, b) => Number(a.ordem) - Number(b.ordem))
        .map((m) => ({ id: m.id, nome: m.nome, dinheiro: m.dinheiro }));
    },
  });
  const carregandoMetodos = podeVerMetodos && consultaMetodos.isPending;
  const modoAntigo = !podeVerMetodos || consultaMetodos.isError;
  const metodos: readonly MetodoDeRecebimento[] = modoAntigo ? [DINHEIRO_SIMPLES] : (consultaMetodos.data ?? []);
  const podeVariasLinhas = !modoAntigo && permissoes.pode("pagamento:dividir");
  const podeGorjeta = !modoAntigo && permissoes.pode("pagamento:registrar-gorjeta");

  // The current split of the bill, and what was already received for each part (when the caller may list the payments).
  const podeVerDivisao = !modoAntigo && permissoes.pode("pagamento:dividir") && permissoes.pode("comanda:dividir");
  const consultaDivisao = useQuery({
    queryKey: ["comanda", comandaId, "divisao"],
    enabled: open && podeVerDivisao,
    retry: false,
    networkMode: "always",
    queryFn: async () => {
      const { data, response } = await api.GET("/comandas/{comandaId}/divisao", {
        params: { path: { comandaId } },
      });
      if (data) {
        return data;
      }

      // No split yet (404) or the bill changed after it (409): there is no part to pay.
      if (response.status === 404 || response.status === 409) {
        return null;
      }

      throw new Error("divisao");
    },
  });
  const divisao = consultaDivisao.data?.vigente ? consultaDivisao.data : null;
  const consultaPagamentos = useQuery({
    queryKey: ["comanda", comandaId, "pagamentos"],
    enabled: open && divisao !== null && permissoes.pode("pagamento:ver"),
    retry: false,
    networkMode: "always",
    queryFn: async () => {
      const { data } = await api.GET("/comandas/{comandaId}/pagamentos", { params: { path: { comandaId } } });
      return data ?? null;
    },
  });
  const partes: ParteParaReceber[] = divisao
    ? partesParaReceber(divisao.id, divisao.partes, consultaPagamentos.data ?? null)
    : [];
  const padrao = partes.find((p) => p.faltaCentavos > 0)?.numero ?? "toda";
  const escolhida = escolhaDaParte ?? padrao;
  const parte = divisao && escolhida !== "toda" ? (partes.find((p) => p.numero === escolhida) ?? null) : null;
  const alvo = alvoDoRecebimento(saldo, parte);

  // Each time the sheet opens it starts clean: no lines (one tap on a method fills the whole balance), no tip, a new payment id per line.
  // biome-ignore lint/correctness/useExhaustiveDependencies: runs only when the sheet opens; a later change of the balance is handled by `removerTaxa` and the refreshed conta.
  useEffect(() => {
    if (open) {
      setLinhas([]);
      setAtivo(null);
      setGorjeta("");
      setEscolhaDaParte(null);
      setFase({ tipo: "digitando" });
      setErro(undefined);
      setSemCaixa(false);
    }
  }, [open]);

  // Old route: its only method is already the line, so there is nothing to choose.
  useEffect(() => {
    if (open && modoAntigo && fase.tipo === "digitando") {
      setLinhas((atual) =>
        atual.length > 0 ? atual : [{ pagamentoId: uuidv7(), metodoId: DINHEIRO_SIMPLES.id, digitos: "" }],
      );
      setAtivo((a) => a ?? DINHEIRO_SIMPLES.id);
    }
  }, [open, modoAntigo, fase.tipo]);

  const calculo = calcularRecebimento(linhas, metodos, alvo);
  const gorjetaCentavos = gorjeta === "" ? 0 : centsFromDigits(gorjeta);
  const tudoPago = saldo === 0;
  const unicaEmDinheiro = linhas.length === 1 && (metodos.find((m) => m.id === linhas[0]?.metodoId)?.dinheiro ?? false);
  // One line of cash, no tip, no part of a split: the old route, which can wait in the queue.
  const simples = unicaEmDinheiro && gorjetaCentavos === 0 && parte === null;
  const podeRemoverTaxa =
    permissoes.pode("comanda:remover-taxa-servico") && !conta.taxaServicoRemovida && conta.taxaServicoCentavos > 0;
  const percentual = conta.taxaServicoPercentual ?? 0;

  async function tentarComVersao<T>(
    escrever: (
      v: number,
    ) => Promise<{ ok: T | null; status: number; codigo: string | undefined; atual: number | null | undefined }>,
  ): Promise<{ ok: T | null; status: number; codigo: string | undefined }> {
    let r = await escrever(versao.current);
    if (r.ok === null && r.status === 409 && r.codigo === "versao-desatualizada" && typeof r.atual === "number") {
      versao.current = Math.max(versao.current, r.atual);
      r = await escrever(versao.current);
    }

    return r;
  }

  async function removerTaxa() {
    setErro(undefined);
    setEnviando(true);
    try {
      const r = await tentarComVersao(async (v) => {
        const { data, response, error } = await api.POST("/comandas/{comandaId}/taxa-servico/remover", {
          params: { path: { comandaId } },
          body: { versao: v },
        });
        return {
          ok: data ?? null,
          status: response.status,
          codigo: (error as ErroDeApi)?.codigo,
          atual: (error as ErroDeApi)?.versaoAtual,
        };
      });
      if (r.ok) {
        versao.current = Math.max(versao.current, Number(r.ok.versao));
        await onAtualizar();
        // The balance changed under the lines: what was typed no longer fits it, the lines start again from the new balance.
        setLinhas((atual) => atual.map((l) => ({ ...l, digitos: "" })));
        return;
      }

      setErro(
        r.status === 403
          ? "Você não tem permissão para tirar o serviço."
          : r.codigo === "valor-ja-recebido"
            ? "Já há valor recebido acima do novo total."
            : "Não foi possível tirar o serviço. Tente de novo.",
      );
    } catch {
      setErro("Sem conexão. Tente de novo.");
    } finally {
      setEnviando(false);
    }
  }

  function aoReceber(pago: { trocoCentavos: number; restaCentavos: number; versao: number }) {
    versao.current = Math.max(versao.current, pago.versao);
    setFase(
      pago.restaCentavos > 0
        ? {
            tipo: "parcial",
            trocoCentavos: pago.trocoCentavos,
            restaCentavos: pago.restaCentavos,
            parte: parte?.numero ?? null,
          }
        : { tipo: "fechada", trocoCentavos: pago.trocoCentavos },
    );
  }

  /** Closes the bill once nothing is left to pay. */
  async function fechar(): Promise<boolean> {
    const r = await tentarComVersao(async (v) => {
      const { data, response, error } = await api.POST("/comandas/{comandaId}/fechar", {
        params: { path: { comandaId } },
        body: { versao: v },
      });
      return {
        ok: data ?? null,
        status: response.status,
        codigo: (error as ErroDeApi)?.codigo,
        atual: (error as ErroDeApi)?.versaoAtual,
      };
    });
    if (r.ok || (r.status === 409 && r.codigo === "comanda-nao-aberta")) {
      return true;
    }

    setErro(
      r.codigo === "saldo-pendente" ? "Ainda há saldo a receber." : "Não foi possível fechar a conta. Tente de novo.",
    );
    return false;
  }

  function guardarNaFila(id: string, recebido: number) {
    enfileirarPagamento({
      pagamentoId: id,
      comandaId,
      recebidoCentavos: recebido,
      titulo: `Recebimento · ${nome}`,
      detalhe: `Dinheiro ${formatBRL(recebido)}`,
    });
    setFase({ tipo: "naFila", recebidoCentavos: recebido });
  }

  /** The money is recorded: the sheet says what came back, and closes the bill when nothing is left. */
  async function aposRegistrar(pago: { trocoCentavos: number; restaCentavos: number; versao: number }) {
    aoReceber(pago);
    if (pago.restaCentavos === 0) {
      if (!(await fechar())) {
        // The money is recorded; the sheet goes back to "close" mode through the refreshed balance.
        setFase({ tipo: "digitando" });
        await onAtualizar();
      }

      return;
    }

    await onAtualizar();
  }

  /** One line of cash through the old route (it waits in the queue with no signal or a server hiccup). */
  async function receberSimples(linha: LinhaDeRecebimento, recebido: number) {
    const id = linha.pagamentoId;
    // An earlier action of this comanda still in the queue goes first: the payment waits behind it.
    if (!navigator.onLine || filaDaComanda) {
      guardarNaFila(id, recebido);
      return;
    }

    try {
      const r = await tentarComVersao(async (v) => {
        const { data, response, error } = await api.POST("/comandas/{comandaId}/pagamentos", {
          params: { path: { comandaId } },
          body: { pagamentoId: id, recebidoCentavos: recebido, versao: v },
        });
        return {
          ok: data ?? null,
          status: response.status,
          codigo: (error as ErroDeApi)?.codigo,
          atual: (error as ErroDeApi)?.versaoAtual,
        };
      });
      if (r.ok) {
        await aposRegistrar({
          trocoCentavos: Number(r.ok.trocoCentavos),
          restaCentavos: Number(r.ok.saldoRestanteCentavos),
          versao: Number(r.ok.comandaVersao),
        });
        return;
      }

      if (r.status === 409 && r.codigo === "caixa-fechado") {
        setSemCaixa(true);
      } else if (r.status === 409 && r.codigo === "sem-saldo") {
        await onAtualizar();
        setErro("A conta já está paga. Feche a conta.");
      } else if (r.status === 409 && r.codigo === "comanda-nao-aberta") {
        setErro("Esta comanda não está mais aberta.");
      } else if (r.status === 409) {
        await onAtualizar();
        setErro("Esta comanda foi alterada em outro aparelho. Atualizamos a conta; confira o valor.");
      } else if (r.status === 403) {
        setErro("Você não tem permissão para receber.");
      } else if (r.status >= 500 || r.status === 408 || r.status === 429) {
        guardarNaFila(id, recebido);
      } else {
        setErro("Não foi possível registrar o recebimento. Confira o valor e tente de novo.");
      }
    } catch {
      guardarNaFila(id, recebido);
    }
  }

  /** Several lines, other methods, a tip or a part of a split: the server now, one request, never queued. A 409 refreshes the numbers and keeps every line. */
  async function receberLinhas() {
    try {
      const { data, response, error } = await api.POST("/comandas/{comandaId}/recebimentos", {
        params: { path: { comandaId } },
        body: {
          versao: versao.current,
          linhas: linhas.map((l, i) => ({
            pagamentoId: l.pagamentoId,
            metodoId: l.metodoId,
            recebidoCentavos: calculo.valores[i] ?? 0,
            gorjetaCentavos: i === 0 && gorjetaCentavos > 0 ? gorjetaCentavos : null,
            divisaoId: parte && divisao ? divisao.id : null,
            divisaoParte: parte ? parte.numero : null,
          })),
        },
      });
      if (data) {
        await aposRegistrar({
          trocoCentavos: data.pagamentos.reduce((soma, p) => soma + Number(p.trocoCentavos), 0),
          restaCentavos: Number(data.conta.saldoCentavos),
          versao: Number(data.conta.versao),
        });
        return;
      }

      const falha = error as ErroDeApi;
      if (response.status === 409 && falha?.codigo === "caixa-fechado") {
        setSemCaixa(true);
        return;
      }

      if (response.status === 409 && typeof falha?.versaoAtual === "number") {
        versao.current = Math.max(versao.current, falha.versaoAtual);
      }

      setErro(textoDoRecebimento(response.status, falha?.codigo));
      if (response.status === 409 || response.status === 404) {
        await onAtualizar();
      }
    } catch {
      // The answer may have been lost after the server recorded it: the same ids on the next tap are a repeat, never a second payment.
      setErro("Não deu para falar com o servidor. Toque em Receber de novo: nada é cobrado duas vezes.");
    }
  }

  async function receber() {
    setErro(undefined);
    setEnviando(true);
    try {
      if (tudoPago) {
        if (await fechar()) {
          setFase({ tipo: "fechada", trocoCentavos: 0 });
        }

        return;
      }

      if (pagamentoNaFila(comandaId)) {
        return;
      }

      const primeira = linhas[0];
      if (simples && primeira) {
        await receberSimples(primeira, calculo.valores[0] ?? 0);
      } else {
        await receberLinhas();
      }
    } finally {
      setEnviando(false);
    }
  }

  function concluir() {
    onOpenChange(false);
    if (fase.tipo === "fechada" || fase.tipo === "naFila") {
      // The comanda left the open list: the screen behind must not try to reload it.
      void navegar({ to: "/salao" }).then(() => queryClient.invalidateQueries({ queryKey: ["comandas"] }));
    }
  }

  function digitar(d: string) {
    if (ativo === GORJETA) {
      setGorjeta((g) => digitarValor(g, d));
    } else if (ativo !== null) {
      setLinhas((atual) =>
        atual.map((l) => (l.metodoId === ativo ? { ...l, digitos: digitarValor(l.digitos, d) } : l)),
      );
    }
  }

  function apagar() {
    if (ativo === GORJETA) {
      setGorjeta((g) => apagarValor(g));
    } else if (ativo !== null) {
      setLinhas((atual) => atual.map((l) => (l.metodoId === ativo ? { ...l, digitos: apagarValor(l.digitos) } : l)));
    }
  }

  function alternar(metodoId: string) {
    const r = alternarMetodo(linhas, metodoId, podeVariasLinhas, uuidv7());
    setLinhas(r.linhas);
    setAtivo(r.ativo);
    setErro(undefined);
  }

  if (fase.tipo !== "digitando") {
    return (
      <BottomSheet
        open={open}
        onOpenChange={onOpenChange}
        title={
          fase.tipo === "fechada"
            ? "Conta fechada"
            : fase.tipo === "parcial"
              ? "Recebimento registrado"
              : "Recebimento na fila"
        }
        description={nome}
        footer={<Button onClick={concluir}>{fase.tipo === "parcial" ? "Continuar" : "Concluir"}</Button>}
      >
        {fase.tipo === "naFila" ? (
          <Notice tone="info">
            Sem conexão com o servidor. O recebimento de {formatBRL(fase.recebidoCentavos)} ficou na fila e será
            registrado quando der. Nada se perde.
          </Notice>
        ) : (
          <>
            {fase.trocoCentavos > 0 ? (
              <div className="fechar-troco" role="status">
                <span>Troco</span>
                <strong className="f4-num fechar-troco-valor">{formatBRL(fase.trocoCentavos)}</strong>
              </div>
            ) : (
              <p className="fechar-sem-troco" role="status">
                Sem troco.
              </p>
            )}
            {fase.tipo === "parcial" ? (
              <p className="fechar-resta" role="status">
                {fase.parte === null ? "" : `Parte ${fase.parte} recebida. `}A comanda segue aberta. Restam{" "}
                <strong className="f4-num">{formatBRL(fase.restaCentavos)}</strong>.
              </p>
            ) : null}
          </>
        )}
      </BottomSheet>
    );
  }

  // Why the receipt cannot go now, in the pinned footer next to the disabled button.
  const bloqueioDeRede = simples
    ? null
    : !online
      ? "Sem conexão. Receber com outro método, vários métodos, gorjeta ou uma parte da divisão precisa do servidor e não fica na fila."
      : filaDaComanda
        ? "Há ações desta comanda na fila. Receba depois que forem enviadas."
        : null;
  const motivo =
    linhas.length === 0
      ? "Escolha como receber."
      : calculo.problema
        ? textoDoProblema(calculo.problema, metodos)
        : bloqueioDeRede;
  const pronto = !tudoPago && motivo === null && !caixaFechado && !bloqueadoPelaFila;
  const nomeDoMetodo = (id: string) => metodos.find((m) => m.id === id)?.nome ?? "";

  return (
    <BottomSheet
      open={open}
      onOpenChange={onOpenChange}
      title="Fechar conta"
      description={nome}
      loading={enviando}
      footer={
        <div className="mov-rodape">
          {bloqueadoPelaFila ? (
            <Notice tone="info">
              {guardado > 0
                ? `Há um recebimento de ${formatBRL(guardado)} nesta comanda esperando na fila. Aguarde ele ser registrado antes de receber de novo.`
                : "Há um recebimento desta comanda esperando na fila. Aguarde ele ser registrado."}
            </Notice>
          ) : null}
          {caixaFechado ? (
            <Notice
              tone="warning"
              action={
                <Link to="/salao/caixa" onClick={() => onOpenChange(false)}>
                  Ir para o caixa
                </Link>
              }
            >
              Abra o caixa para receber.
            </Notice>
          ) : null}
          {!tudoPago && motivo !== null && !bloqueadoPelaFila && !caixaFechado ? (
            <p role="status" className="mov-bloqueio">
              {motivo}
            </p>
          ) : null}
          {erro ? (
            <p role="alert" className="f4-field-error">
              {erro}
            </p>
          ) : null}
          {tudoPago ? null : (
            <>
              <p className="mov-resumo receb-resumo" aria-label="Resumo do recebimento">
                {parte && partes.length > 0
                  ? `Parte ${parte.numero} de ${partes.length}${parte.assento === null ? "" : ` · assento ${parte.assento}`} · `
                  : ""}
                Recebendo <span className="f4-num">{formatBRL(calculo.aplicadoCentavos)}</span> · falta{" "}
                <span className="f4-num">{formatBRL(calculo.faltaCentavos)}</span>
                {gorjetaCentavos > 0 ? (
                  <>
                    {" "}
                    · gorjeta <span className="f4-num">{formatBRL(gorjetaCentavos)}</span>
                  </>
                ) : null}
              </p>
              {calculo.trocoCentavos > 0 ? (
                <div className="fechar-troco fechar-troco-linha" role="status" aria-live="polite">
                  <span>Troco</span>
                  <strong className="f4-num fechar-troco-valor">{formatBRL(calculo.trocoCentavos)}</strong>
                </div>
              ) : null}
            </>
          )}
          <div className="mov-botoes">
            <Button variant="secondary" onClick={() => onOpenChange(false)} disabled={enviando}>
              Voltar
            </Button>
            {tudoPago ? (
              <Button loading={enviando} disabled={bloqueadoPelaFila} onClick={() => void receber()}>
                Fechar conta
              </Button>
            ) : (
              <Button loading={enviando} disabled={!pronto} onClick={() => void receber()}>
                Receber
              </Button>
            )}
          </div>
        </div>
      }
    >
      <dl className="fechar-conta" aria-label="Conta">
        <div>
          <dt>Consumo</dt>
          <dd className="f4-num">{formatBRL(conta.consumoCentavos)}</dd>
        </div>
        {itens
          .filter((i) => i.tipo === "cortesia" || i.tipo === "brinde")
          .map((i) => (
            <div key={i.id} className="fechar-cortesia">
              <dt>
                <span className="conta-tipo">
                  <ToneBadge tone="warning">{i.tipo === "brinde" ? "Brinde" : "Cortesia"}</ToneBadge>
                </span>
                {i.quantidade}× {i.produtoNome}
              </dt>
              <dd className="f4-num">{formatBRL(0)}</dd>
            </div>
          ))}
        {(conta.descontoCentavos ?? 0) > 0 ? (
          <div>
            <dt>Desconto</dt>
            <dd className="f4-num">−{formatBRL(conta.descontoCentavos ?? 0)}</dd>
          </div>
        ) : null}
        <div>
          <dt>
            {conta.taxaServicoRemovida ? "Serviço removido" : percentual > 0 ? `Serviço (${percentual}%)` : "Serviço"}
          </dt>
          <dd className="f4-num">{formatBRL(conta.taxaServicoCentavos)}</dd>
        </div>
        {podeRemoverTaxa ? (
          <div className="fechar-taxa">
            <Button variant="secondary" onClick={() => void removerTaxa()} disabled={enviando}>
              Remover serviço
            </Button>
          </div>
        ) : null}
        <div className="fechar-total">
          <dt>Total</dt>
          <dd className="f4-num">{formatBRL(conta.totalCentavos)}</dd>
        </div>
        {conta.pagoCentavos > 0 ? (
          <div>
            <dt>Já recebido</dt>
            <dd className="f4-num">{formatBRL(conta.pagoCentavos)}</dd>
          </div>
        ) : null}
      </dl>

      {tudoPago ? (
        <p className="fechar-resta" role="status">
          A conta está paga. Falta só fechar.
        </p>
      ) : (
        <>
          <p className="fechar-receber" aria-label="Valor a receber">
            {parte ? `Parte ${parte.numero}: valor a receber ` : "Valor a receber "}
            <strong className="f4-num">{formatBRL(alvo)}</strong>
          </p>

          {partes.length > 0 ? (
            <>
              <p id="receb-quem" className="mov-rotulo">
                Quem paga
              </p>
              <div className="receb-chips" role="group" aria-labelledby="receb-quem">
                <Chip selected={parte === null} onClick={() => setEscolhaDaParte("toda")}>
                  Conta toda
                </Chip>
                {partes.map((p) => (
                  <Chip
                    key={p.numero}
                    selected={parte?.numero === p.numero}
                    disabled={p.faltaCentavos === 0}
                    onClick={() => setEscolhaDaParte(p.numero)}
                  >
                    {`Parte ${p.numero}${p.assento === null ? "" : ` · assento ${p.assento}`} · ${p.faltaCentavos === 0 ? "paga" : formatBRL(p.faltaCentavos)}`}
                  </Chip>
                ))}
              </div>
            </>
          ) : null}

          {modoAntigo ? null : carregandoMetodos ? (
            <Skeleton rows={1} label="Carregando métodos de pagamento" />
          ) : (
            <>
              <p id="receb-metodo" className="mov-rotulo">
                {podeVariasLinhas ? "Métodos (um ou mais)" : "Método"}
              </p>
              <div className="receb-chips" role="group" aria-labelledby="receb-metodo">
                {metodos.map((m) => (
                  <Chip
                    key={m.id}
                    selected={linhas.some((l) => l.metodoId === m.id)}
                    disabled={enviando}
                    onClick={() => alternar(m.id)}
                  >
                    {m.nome}
                  </Chip>
                ))}
              </div>
            </>
          )}
          {podeVerMetodos && consultaMetodos.isError ? (
            <Notice tone="info">Não deu para ler os métodos de pagamento. Por enquanto só dinheiro.</Notice>
          ) : null}

          {linhas.length > 0 || podeGorjeta ? (
            <ul className="receb-linhas" aria-label="Valores por método">
              {linhas.map((l, i) => (
                <li key={l.metodoId}>
                  <button
                    type="button"
                    className="receb-linha"
                    aria-pressed={ativo === l.metodoId}
                    aria-label={`${nomeDoMetodo(l.metodoId)}: ${formatBRL(calculo.valores[i] ?? 0)}`}
                    onClick={() => setAtivo(l.metodoId)}
                  >
                    <span className="receb-linha-nome">
                      {nomeDoMetodo(l.metodoId)}
                      {l.digitos === "" && linhas.length > 1 ? <small> o que falta</small> : null}
                    </span>
                    <span className="f4-num receb-linha-valor">{formatBRL(calculo.valores[i] ?? 0)}</span>
                  </button>
                </li>
              ))}
              {podeGorjeta ? (
                <li>
                  <button
                    type="button"
                    className="receb-linha"
                    aria-pressed={ativo === GORJETA}
                    aria-label={`Gorjeta: ${formatBRL(gorjetaCentavos)}`}
                    onClick={() => setAtivo(GORJETA)}
                  >
                    <span className="receb-linha-nome">
                      Gorjeta <small>opcional</small>
                    </span>
                    <span className="f4-num receb-linha-valor">{formatBRL(gorjetaCentavos)}</span>
                  </button>
                </li>
              ) : null}
            </ul>
          ) : null}

          {linhas.length > 0 || (podeGorjeta && ativo === GORJETA) ? (
            <div className="receb-teclado">
              <Keypad
                onDigit={digitar}
                onBackspace={apagar}
                onCancel={() => onOpenChange(false)}
                disabled={enviando || ativo === null}
              />
            </div>
          ) : null}
        </>
      )}
    </BottomSheet>
  );
}
