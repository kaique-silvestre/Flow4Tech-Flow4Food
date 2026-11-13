import {
  BottomSheet,
  Button,
  Chip,
  formatBRL,
  MoneyField,
  Skeleton,
  Stepper,
  TextField,
} from "@flow4tech/design-system";
import { useQuery } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { api } from "../sessao/sessao";
import {
  digitarPercentual,
  type FormularioDaDivisao,
  formularioInicial,
  MODOS_DA_DIVISAO,
  type ModoDaDivisao,
  mudarPartes,
  PARTES_MAXIMO,
  PARTES_MINIMO,
  pedidoDaDivisao,
  textoDaFalhaDaDivisao,
} from "./divisao";
import type { FalhaDeMovimento } from "./movimento";
import "./movimento.css";
import type { ContaDaComanda, ItemDaConta } from "./rodada";
import { uuidv7 } from "./uuid";

interface Parte {
  numero: number;
  assento: number | null;
  aPagarCentavos: number;
  totalCentavos: number;
  gorjetaCentavos: number;
}

interface Previa {
  modo: string;
  comandaVersao: number;
  totalCentavos: number;
  taxaServicoCentavos: number;
  partes: Parte[];
}

/** A failed preview, with what the server said, so the sheet can word it (the preview is a read: nothing was written). */
class FalhaDaPrevia extends Error {
  constructor(
    readonly status: number,
    readonly codigo: string | undefined,
  ) {
    super("previa-da-divisao");
  }
}

function paraParte(p: {
  numero: number | string;
  assento: number | string | null;
  aPagarCentavos: number | string;
  totalCentavos: number | string;
  gorjetaCentavos: number | string;
}): Parte {
  return {
    numero: Number(p.numero),
    assento: p.assento === null ? null : Number(p.assento),
    aPagarCentavos: Number(p.aPagarCentavos),
    totalCentavos: Number(p.totalCentavos),
    gorjetaCentavos: Number(p.gorjetaCentavos),
  };
}

interface Props {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  comandaId: string;
  nome: string;
  conta: ContaDaComanda;
  /** The launched items of the comanda (the item mode assigns each one to a part). */
  itens: readonly ItemDaConta[];
  /** The highest comanda version the screen knows; the sheet raises it with what its writes answer. */
  versao: { current: number };
  /** What blocks it now (no signal, actions of the comanda waiting in the queue); null when it can go. Splitting is never queued. */
  bloqueio: string | null;
  onAtualizar: () => Promise<void>;
}

function Partes({ valor, onChange }: { valor: number; onChange: (n: number) => void }) {
  return (
    <div className="div-partes">
      <span>Partes</span>
      <Stepper label="Partes" value={valor} min={PARTES_MINIMO} max={PARTES_MAXIMO} onChange={onChange} />
    </div>
  );
}

/**
 * Split the bill (docs/agents/salao.md, F4-0061): five ways, and the server's PREVIEW of every part before confirming. Equal parts is the first way and starts with two parts, so the common
 * case is Mais, Dividir a conta, (+), Confirmar. Confirming sends the version of the PREVIEW the person read, so a bill that changed meanwhile answers 409, the numbers and the preview refresh and
 * what was typed stays. It needs the server now and never waits in the queue.
 */
export function DividirSheet({
  open,
  onOpenChange,
  comandaId,
  nome,
  conta,
  itens,
  versao,
  bloqueio,
  onAtualizar,
}: Props) {
  const [form, setForm] = useState<FormularioDaDivisao>(() => formularioInicial(itens, conta.totalCentavos));
  const [feita, setFeita] = useState<Parte[] | null>(null);
  const [enviando, setEnviando] = useState(false);
  const [erro, setErro] = useState<string | undefined>(undefined);
  // The id of the split is made once per request and kept until the server accepts it: a retry after a lost answer is a repeat, never a second split.
  const idDaDivisao = useRef<{ id: string; chave: string } | null>(null);
  // biome-ignore lint/correctness/useExhaustiveDependencies: the form restarts each time the sheet opens, from the items and the bill of that moment, not on every refresh while it is open.
  useEffect(() => {
    if (open) {
      setForm(formularioInicial(itens, conta.totalCentavos));
      setFeita(null);
      setErro(undefined);
      idDaDivisao.current = null;
    }
  }, [open]);

  const montado = pedidoDaDivisao(form, itens, conta.totalCentavos);
  const chave = montado.ok ? JSON.stringify(montado.pedido) : null;
  const previa = useQuery<Previa>({
    queryKey: ["comanda", comandaId, "divisao-previa", chave],
    enabled: open && feita === null && montado.ok && bloqueio === null,
    gcTime: 0,
    staleTime: 0,
    retry: false,
    queryFn: async () => {
      if (!montado.ok) {
        throw new Error("pedido");
      }

      const { data, error, response } = await api.POST("/comandas/{comandaId}/divisao/previa", {
        params: { path: { comandaId } },
        body: montado.pedido,
      });
      if (!data) {
        throw new FalhaDaPrevia(response.status, (error as FalhaDeMovimento | undefined)?.codigo);
      }

      return {
        modo: data.modo,
        comandaVersao: Number(data.comandaVersao),
        totalCentavos: Number(data.totalCentavos),
        taxaServicoCentavos: Number(data.taxaServicoCentavos),
        partes: data.partes.map(paraParte),
      };
    },
  });

  function mudar(parcial: Partial<FormularioDaDivisao>) {
    setErro(undefined);
    setForm((atual) => ({ ...atual, ...parcial }));
  }

  async function confirmar() {
    if (!montado.ok || !previa.data) {
      return;
    }

    setErro(undefined);
    setEnviando(true);
    if (idDaDivisao.current?.chave !== chave) {
      idDaDivisao.current = { id: uuidv7(), chave: chave ?? "" };
    }

    try {
      const { data, error, response } = await api.POST("/comandas/{comandaId}/divisao", {
        params: { path: { comandaId } },
        // The version of the preview the person read: if the bill changed since, the server says so instead of dividing something else.
        body: { divisaoId: idDaDivisao.current.id, versao: previa.data.comandaVersao, pedido: montado.pedido },
      });
      if (data) {
        versao.current = Math.max(versao.current, Number(data.comandaVersao));
        setFeita(data.partes.map(paraParte));
        await onAtualizar();
        return;
      }

      const falha = (error ?? {}) as FalhaDeMovimento;
      if (typeof falha.versaoAtual === "number") {
        versao.current = Math.max(versao.current, falha.versaoAtual);
      }

      if (response.status === 404 || response.status === 409) {
        await onAtualizar();
      }

      setErro(textoDaFalhaDaDivisao(response.status, falha.codigo, form.modo));
    } catch {
      setErro(
        "Sem conexão. Dividir a conta precisa do servidor e não fica na fila. Tente de novo quando o sinal voltar.",
      );
    } finally {
      setEnviando(false);
    }
  }

  const falhaDaPrevia = previa.error instanceof FalhaDaPrevia ? previa.error : null;
  const calculando = montado.ok && bloqueio === null && (previa.isPending || previa.isFetching) && !previa.isError;
  const pronto = montado.ok && previa.data !== undefined && !previa.isFetching && bloqueio === null;

  if (feita) {
    return (
      <BottomSheet
        open={open}
        onOpenChange={onOpenChange}
        title="Conta dividida"
        description={`${nome} em ${feita.length} partes. Cada parte é cobrada à parte.`}
        footer={<Button onClick={() => onOpenChange(false)}>Fechar</Button>}
      >
        <ul className="div-previa" aria-label="Partes da divisão">
          {feita.map((p) => (
            <li key={p.numero}>
              <span>
                Parte {p.numero}
                {p.assento === null ? "" : ` · assento ${p.assento}`}
              </span>
              <span className="f4-num">{formatBRL(p.aPagarCentavos)}</span>
            </li>
          ))}
        </ul>
      </BottomSheet>
    );
  }

  const modo: ModoDaDivisao = form.modo;
  const partes = (
    <Partes
      valor={form.partes}
      onChange={(n) => {
        setErro(undefined);
        setForm((atual) => mudarPartes(atual, n, itens, conta.totalCentavos));
      }}
    />
  );
  return (
    <BottomSheet
      open={open}
      onOpenChange={onOpenChange}
      title="Dividir a conta"
      description={`${nome} · total ${formatBRL(conta.totalCentavos)}`}
      loading={enviando}
      footer={
        <div className="mov-rodape">
          {bloqueio ? (
            <p role="status" className="mov-bloqueio">
              {bloqueio}
            </p>
          ) : null}
          {!montado.ok && montado.aviso ? <p className="div-aviso">{montado.aviso}</p> : null}
          {montado.ok && bloqueio === null && calculando ? (
            <p role="status" className="div-aviso">
              Calculando a divisão…
            </p>
          ) : null}
          {montado.ok && bloqueio === null && !calculando && falhaDaPrevia ? (
            <>
              <p role="alert" className="f4-field-error">
                {textoDaFalhaDaDivisao(falhaDaPrevia.status, falhaDaPrevia.codigo, modo)}
              </p>
              {modo === "assento" && falhaDaPrevia.status === 400 ? (
                <Button variant="secondary" onClick={() => mudar({ modo: "item" })}>
                  Dividir por item
                </Button>
              ) : null}
              {falhaDaPrevia.status >= 500 ? (
                <Button variant="secondary" onClick={() => void previa.refetch()}>
                  Tentar novamente
                </Button>
              ) : null}
            </>
          ) : null}
          {montado.ok && bloqueio === null && !calculando && !falhaDaPrevia && previa.isError ? (
            <>
              <p role="alert" className="f4-field-error">
                Não foi possível calcular a prévia. Confira a conexão.
              </p>
              <Button variant="secondary" onClick={() => void previa.refetch()}>
                Tentar novamente
              </Button>
            </>
          ) : null}
          {erro ? (
            <p role="alert" className="f4-field-error">
              {erro}
            </p>
          ) : null}
          {montado.ok && bloqueio === null && !calculando && previa.data ? (
            <section aria-label="Prévia da divisão">
              <p className="div-previa-total f4-num">
                <span>Total</span>
                <span>{formatBRL(previa.data.totalCentavos)}</span>
              </p>
              <ul className="div-previa" aria-label="Partes da divisão">
                {previa.data.partes.map((p) => (
                  <li key={p.numero}>
                    <span>
                      Parte {p.numero}
                      {p.assento === null ? "" : ` · assento ${p.assento}`}
                    </span>
                    <span className="f4-num">{formatBRL(p.aPagarCentavos)}</span>
                  </li>
                ))}
              </ul>
            </section>
          ) : null}
          <div className="mov-botoes">
            <Button variant="secondary" onClick={() => onOpenChange(false)} disabled={enviando}>
              Voltar
            </Button>
            <Button loading={enviando} disabled={!pronto} onClick={() => void confirmar()}>
              Confirmar divisão
            </Button>
          </div>
        </div>
      }
    >
      <div className="div-modos" role="group" aria-label="Forma de dividir">
        {MODOS_DA_DIVISAO.map((m) => (
          <Chip key={m.modo} selected={modo === m.modo} onClick={() => mudar({ modo: m.modo })}>
            {m.rotulo}
          </Chip>
        ))}
      </div>

      {modo === "iguais" ? partes : null}
      {modo === "assento" ? (
        <p className="div-ajuda">
          Cada assento paga o que consumiu. O que não tem assento é dividido por igual entre os assentos.
        </p>
      ) : null}
      {modo === "item" ? (
        <>
          {partes}
          <ul className="mov-lista" aria-label="Itens por parte">
            {itens.map((i) => (
              <li key={i.id} className="div-item">
                <span className="mov-nome">
                  {i.quantidade}× {i.produtoNome}
                </span>
                <select
                  aria-label={`Parte de ${i.produtoNome}`}
                  value={form.grupoPorItem[i.id] ?? 1}
                  onChange={(e) => mudar({ grupoPorItem: { ...form.grupoPorItem, [i.id]: Number(e.target.value) } })}
                >
                  {Array.from({ length: form.partes }, (_, k) => (
                    // biome-ignore lint/suspicious/noArrayIndexKey: the options are the numbers 1..n, fixed by the count.
                    <option key={k} value={k + 1}>
                      Parte {k + 1}
                    </option>
                  ))}
                </select>
              </li>
            ))}
          </ul>
        </>
      ) : null}
      {modo === "valores" ? (
        <>
          {partes}
          {form.valores.map((v, k) => (
            <MoneyField
              // biome-ignore lint/suspicious/noArrayIndexKey: the parts are identified by their number.
              key={k}
              label={`Parte ${k + 1}`}
              digits={v}
              onDigitsChange={(d) => mudar({ valores: form.valores.map((x, j) => (j === k ? d : x)) })}
            />
          ))}
        </>
      ) : null}
      {modo === "percentual" ? (
        <>
          {partes}
          {form.percentuais.map((p, k) => (
            <TextField
              // biome-ignore lint/suspicious/noArrayIndexKey: the parts are identified by their number.
              key={k}
              label={`Parte ${k + 1} (%)`}
              inputMode="decimal"
              autoComplete="off"
              value={p.replace(".", ",")}
              onChange={(e) =>
                mudar({
                  percentuais: form.percentuais.map((x, j) => (j === k ? digitarPercentual(e.target.value) : x)),
                })
              }
            />
          ))}
        </>
      ) : null}
    </BottomSheet>
  );
}
