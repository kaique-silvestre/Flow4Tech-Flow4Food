import { BottomSheet, Button, Chip, formatBRL, Keypad, Notice, Skeleton, TextField } from "@flow4tech/design-system";
import { useQueries, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { api } from "../sessao/sessao";
import {
  corpoDoEstorno,
  estornavel,
  type FormaDoEstorno,
  LIMITE_DO_MOTIVO,
  MOTIVOS_DE_ESTORNO,
  motivoFinal,
  type PagamentoEstornavel,
  textoDoEstorno,
  valorDoEstorno,
  valorValido,
} from "./estorno";

interface Props {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  comandaId: string;
  /** What blocks it now (no signal); null when it can go. A refund needs the server and never waits in the queue. */
  bloqueio: string | null;
  /** Called after a refund was written, so the page refreshes the bill. */
  onEstornado: () => void;
}

const quando = (iso: string) =>
  new Date(iso).toLocaleTimeString("pt-BR", { hour: "2-digit", minute: "2-digit", timeZone: "America/Sao_Paulo" });

/**
 * Refund of a payment of the comanda, total or partial, with a mandatory reason. The window lists the confirmed payments with what is still refundable; the value and the shift are decided by the
 * server (the limit of the profile is asked with the cents it measured, and a refusal never says what the limit is). It needs a connection and is never queued.
 */
export function EstornoSheet({ open, onOpenChange, comandaId, bloqueio, onEstornado }: Props) {
  const queryClient = useQueryClient();
  const pagamentos = useQuery({
    queryKey: ["comanda", comandaId, "pagamentos", "estorno"],
    enabled: open,
    retry: false,
    queryFn: async () => {
      const { data } = await api.GET("/comandas/{comandaId}/pagamentos", { params: { path: { comandaId } } });
      if (!data) {
        throw new Error("pagamentos");
      }

      return data.filter(estornavel);
    },
  });
  const lista = pagamentos.data ?? [];
  const saldos = useQueries({
    queries: lista.map((p) => ({
      queryKey: ["pagamento", p.id, "estornos"],
      enabled: open,
      retry: false,
      queryFn: async () => {
        const { data } = await api.GET("/pagamentos/{pagamentoId}/estornos", {
          params: { path: { pagamentoId: p.id } },
        });
        if (!data) {
          throw new Error("estornos");
        }

        return data;
      },
    })),
  });
  const carregandoSaldos = saldos.some((s) => s.isPending);
  const falhouSaldos = saldos.some((s) => s.isError);
  const estornaveis: PagamentoEstornavel[] = lista.flatMap((p, i) => {
    const s = saldos[i]?.data;
    return s && Number(s.saldoEstornavelCentavos) > 0
      ? [
          {
            id: p.id,
            metodoNome: p.metodoNome,
            aplicadoCentavos: Number(p.aplicadoCentavos),
            estornadoCentavos: Number(s.estornadoCentavos),
            saldoEstornavelCentavos: Number(s.saldoEstornavelCentavos),
            registradoEm: p.registradoEm,
          },
        ]
      : [];
  });

  const [escolhido, setEscolhido] = useState<string | null>(null);
  const [forma, setForma] = useState<FormaDoEstorno>("total");
  const [digitos, setDigitos] = useState("");
  const [motivo, setMotivo] = useState<string | null>(null);
  const [texto, setTexto] = useState("");
  const [enviando, setEnviando] = useState(false);
  const [erro, setErro] = useState<string | undefined>();
  // biome-ignore lint/correctness/useExhaustiveDependencies: the form restarts each time the window opens, not on every render.
  useEffect(() => {
    if (open) {
      setEscolhido(null);
      setForma("total");
      setDigitos("");
      setMotivo(null);
      setTexto("");
      setErro(undefined);
    }
  }, [open]);

  const pagamento = estornaveis.find((p) => p.id === escolhido) ?? (estornaveis.length === 1 ? estornaveis[0] : null);
  const saldo = pagamento?.saldoEstornavelCentavos ?? 0;
  const valor = valorDoEstorno(forma, digitos, saldo);
  const motivoPronto = motivoFinal(motivo, texto);
  const pronto =
    pagamento !== null &&
    pagamento !== undefined &&
    valorValido(forma, digitos, saldo) &&
    motivoPronto !== "" &&
    bloqueio === null;

  async function estornar() {
    if (!pagamento || !pronto) {
      return;
    }

    setErro(undefined);
    setEnviando(true);
    try {
      const { data, response, error } = await api.POST("/pagamentos/{pagamentoId}/estornos", {
        params: { path: { pagamentoId: pagamento.id } },
        body: corpoDoEstorno(forma, digitos, motivoPronto),
      });
      if (data) {
        await Promise.all([
          queryClient.invalidateQueries({ queryKey: ["pagamento", pagamento.id, "estornos"] }),
          queryClient.invalidateQueries({ queryKey: ["comanda", comandaId] }),
          queryClient.invalidateQueries({ queryKey: ["caixa"] }),
        ]);
        onEstornado();
        onOpenChange(false);
        return;
      }

      setErro(textoDoEstorno(response.status, (error as { codigo?: string } | undefined)?.codigo));
      if (response.status === 400 || response.status === 404 || response.status === 409) {
        void queryClient.invalidateQueries({ queryKey: ["pagamento", pagamento.id, "estornos"] });
      }
    } catch {
      setErro("Sem conexão. Estornar precisa do servidor.");
    } finally {
      setEnviando(false);
    }
  }

  function digitar(d: string) {
    setDigitos(`${digitos}${d}`.replace(/^0+/, "").slice(0, 8));
  }

  return (
    <BottomSheet
      open={open}
      onOpenChange={onOpenChange}
      title="Estornar pagamento"
      description="Devolve ao cliente. O pagamento original continua no registro e o estorno entra como lançamento negativo."
      loading={enviando}
      footer={
        <div className="desconto-rodape">
          {bloqueio ? (
            <p role="status" className="desconto-bloqueio">
              {bloqueio}
            </p>
          ) : null}
          {erro ? (
            <p role="alert" className="f4-field-error">
              {erro}
            </p>
          ) : null}
          <dl className="desconto-previa" aria-label="Prévia do estorno">
            <div>
              <dt>Devolve</dt>
              <dd className="f4-num">{formatBRL(pagamento ? valor : 0)}</dd>
            </div>
            <div className="desconto-novo">
              <dt>Ainda estornável</dt>
              <dd className="f4-num">{formatBRL(pagamento ? Math.max(0, saldo - valor) : 0)}</dd>
            </div>
          </dl>
          <div className="desconto-botoes">
            <Button variant="secondary" onClick={() => onOpenChange(false)} disabled={enviando}>
              Voltar
            </Button>
            <Button loading={enviando} disabled={!pronto} onClick={() => void estornar()}>
              Estornar
            </Button>
          </div>
        </div>
      }
    >
      {pagamentos.isPending ? <Skeleton rows={2} label="Carregando pagamentos" /> : null}
      {pagamentos.isError ? <Notice tone="error">Não foi possível carregar os pagamentos.</Notice> : null}
      {!pagamentos.isPending && !pagamentos.isError && carregandoSaldos ? (
        <Skeleton rows={2} label="Carregando pagamentos" />
      ) : null}
      {falhouSaldos ? <Notice tone="error">Não foi possível carregar o que ainda pode ser estornado.</Notice> : null}
      {!pagamentos.isPending &&
      !pagamentos.isError &&
      !carregandoSaldos &&
      !falhouSaldos &&
      estornaveis.length === 0 ? (
        <p className="comandas-vazio">Esta comanda não tem pagamento para estornar.</p>
      ) : null}

      {estornaveis.length > 0 ? (
        <>
          <p id="estorno-pagamento" className="desconto-rotulo">
            Pagamento
          </p>
          <div className="motivos" role="group" aria-labelledby="estorno-pagamento">
            {estornaveis.map((p) => (
              <Chip
                key={p.id}
                selected={pagamento?.id === p.id}
                onClick={() => {
                  setEscolhido(p.id);
                  setDigitos("");
                  setErro(undefined);
                }}
              >
                {`${p.metodoNome} ${formatBRL(p.saldoEstornavelCentavos)} · ${quando(p.registradoEm)}`}
              </Chip>
            ))}
          </div>
        </>
      ) : null}

      {pagamento ? (
        <>
          <div className="motivos" role="group" aria-label="Quanto estornar">
            <Chip
              selected={forma === "total"}
              onClick={() => {
                setForma("total");
                setDigitos("");
              }}
            >
              Total
            </Chip>
            <Chip selected={forma === "parcial"} onClick={() => setForma("parcial")}>
              Parcial
            </Chip>
          </div>
          {forma === "parcial" ? (
            <>
              <p className="desconto-valor f4-num" aria-live="polite" aria-label="Valor digitado">
                {formatBRL(valor)}
              </p>
              <Keypad
                onDigit={digitar}
                onBackspace={() => setDigitos((d) => d.slice(0, -1))}
                onCancel={() => onOpenChange(false)}
                disabled={enviando}
                error={
                  digitos !== "" && !valorValido(forma, digitos, saldo)
                    ? `Use de R$ 0,01 até ${formatBRL(saldo)}.`
                    : undefined
                }
              />
            </>
          ) : (
            <p className="desconto-valor-info">Devolve tudo o que ainda pode ser estornado: {formatBRL(saldo)}.</p>
          )}
          <p id="estorno-motivo" className="desconto-rotulo">
            Motivo (obrigatório)
          </p>
          <div className="motivos" role="group" aria-labelledby="estorno-motivo">
            {MOTIVOS_DE_ESTORNO.map((m) => (
              <Chip key={m} selected={motivo === m} onClick={() => setMotivo(m)}>
                {m}
              </Chip>
            ))}
          </div>
          {motivo === "Outro" ? (
            <TextField
              label="Qual o motivo?"
              value={texto}
              maxLength={LIMITE_DO_MOTIVO}
              autoComplete="off"
              onChange={(e) => setTexto(e.target.value)}
            />
          ) : null}
        </>
      ) : null}
    </BottomSheet>
  );
}
