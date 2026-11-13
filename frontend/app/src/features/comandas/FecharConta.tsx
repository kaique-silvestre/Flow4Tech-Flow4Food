import {
  BottomSheet,
  Button,
  centsFromDigits,
  formatBRL,
  MoneyField,
  Notice,
  ToneBadge,
} from "@flow4tech/design-system";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate } from "@tanstack/react-router";
import { useEffect, useRef, useState } from "react";
import { enfileirarPagamento, pagamentoNaFila, recebidoNaFila, temAcaoViva, useFila } from "../fila/fila";
import { usePermissoes } from "../sessao/permissoes";
import { api } from "../sessao/sessao";
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
  | { tipo: "parcial"; trocoCentavos: number; restaCentavos: number }
  | { tipo: "naFila"; recebidoCentavos: number };

/**
 * Closing the bill in cash (docs/design/refs/salao/05.png): consumption, the service fee (removable only by who may), the total, the amount to receive, ONE method (Dinheiro), the cash
 * received with the change in large type, and a partial payment that leaves the rest open. The payment id is made on the client, so a retry after a lost answer is one payment; with no signal
 * the payment waits in the visible queue.
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
  const [digitos, setDigitos] = useState("");
  const [fase, setFase] = useState<Fase>({ tipo: "digitando" });
  const [enviando, setEnviando] = useState(false);
  const [erro, setErro] = useState<string | undefined>(undefined);
  const [semCaixa, setSemCaixa] = useState(false);
  const pagamentoId = useRef<string | null>(null);
  // A payment of this comanda waiting in the queue is money the server does not know yet: while it waits the sheet is blocked, so the same amount is never received under a second payment id.
  const fila = useFila();
  const naFila = fila.acoes.some((a) => a.tipo === "pagamento" && a.comandaId === comandaId && a.estado !== "falhou");
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

  // Each time the sheet opens it starts from the balance: the exact amount is one tap (the field is prefilled), and a new payment id is made.
  // biome-ignore lint/correctness/useExhaustiveDependencies: runs only when the sheet opens; `saldo` is read at that moment on purpose, a later change is handled by `removerTaxa`.
  useEffect(() => {
    if (open) {
      setDigitos(saldo > 0 ? String(saldo) : "");
      setFase({ tipo: "digitando" });
      setErro(undefined);
      setSemCaixa(false);
      pagamentoId.current = null;
    }
    // Only when it opens: the balance changing under an open sheet (the fee removed) is handled by `removerTaxa`.
  }, [open]);

  const recebido = digitos === "" ? 0 : centsFromDigits(digitos);
  const troco = Math.max(0, recebido - saldo);
  const resta = Math.max(0, saldo - recebido);
  const tudoPago = saldo === 0;
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
        const nova = Number(r.ok.saldoCentavos);
        setDigitos(nova > 0 ? String(nova) : "");
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
        ? { tipo: "parcial", trocoCentavos: pago.trocoCentavos, restaCentavos: pago.restaCentavos }
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

  function guardarNaFila(id: string) {
    enfileirarPagamento({
      pagamentoId: id,
      comandaId,
      recebidoCentavos: recebido,
      titulo: `Recebimento · ${nome}`,
      detalhe: `Dinheiro ${formatBRL(recebido)}`,
    });
    setFase({ tipo: "naFila", recebidoCentavos: recebido });
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

      pagamentoId.current ??= uuidv7();
      const id = pagamentoId.current;
      // An earlier action of this comanda still in the queue goes first: the payment waits behind it.
      if (!navigator.onLine || temAcaoViva(comandaId)) {
        guardarNaFila(id);
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
          const pago = r.ok;
          aoReceber({
            trocoCentavos: Number(pago.trocoCentavos),
            restaCentavos: Number(pago.saldoRestanteCentavos),
            versao: Number(pago.comandaVersao),
          });
          pagamentoId.current = null;
          if (Number(pago.saldoRestanteCentavos) === 0) {
            if (!(await fechar())) {
              // The money is recorded; the sheet goes back to "close" mode through the refreshed balance.
              setFase({ tipo: "digitando" });
              await onAtualizar();
            }
          } else {
            await onAtualizar();
          }

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
          guardarNaFila(id);
        } else {
          setErro("Não foi possível registrar o recebimento. Confira o valor e tente de novo.");
        }
      } catch {
        guardarNaFila(id);
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
                A comanda segue aberta. Restam <strong className="f4-num">{formatBRL(fase.restaCentavos)}</strong>.
              </p>
            ) : null}
          </>
        )}
      </BottomSheet>
    );
  }

  return (
    <BottomSheet
      open={open}
      onOpenChange={onOpenChange}
      title="Fechar conta"
      description={nome}
      error={erro}
      loading={enviando}
      footer={
        <>
          <Button variant="secondary" onClick={() => onOpenChange(false)} disabled={enviando}>
            Voltar
          </Button>
          {tudoPago ? (
            <Button loading={enviando} disabled={bloqueadoPelaFila} onClick={() => void receber()}>
              Fechar conta
            </Button>
          ) : (
            <Button
              loading={enviando}
              disabled={caixaFechado || recebido <= 0 || bloqueadoPelaFila}
              onClick={() => void receber()}
            >
              Dinheiro
            </Button>
          )}
        </>
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

      {tudoPago ? (
        <p className="fechar-resta" role="status">
          A conta está paga. Falta só fechar.
        </p>
      ) : (
        <>
          <p className="fechar-receber" aria-label="Valor a receber">
            Valor a receber <strong className="f4-num">{formatBRL(saldo)}</strong>
            {recebido > 0 && resta > 0 ? (
              <span className="fechar-restara">
                {" "}
                · restará <span className="f4-num">{formatBRL(resta)}</span>
              </span>
            ) : null}
          </p>
          <MoneyField label="Dinheiro recebido" digits={digitos} onDigitsChange={setDigitos} />
          {recebido > saldo ? (
            <div className="fechar-troco" role="status" aria-live="polite">
              <span>Troco</span>
              <strong className="f4-num fechar-troco-valor">{formatBRL(troco)}</strong>
            </div>
          ) : recebido > 0 && resta > 0 ? (
            <p className="fechar-parcial" role="status">
              Pagamento parcial. A comanda continua aberta com {formatBRL(resta)}.
            </p>
          ) : null}
        </>
      )}
    </BottomSheet>
  );
}
