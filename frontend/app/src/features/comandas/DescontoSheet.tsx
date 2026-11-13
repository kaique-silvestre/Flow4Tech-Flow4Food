import { BottomSheet, Button, Chip, formatBRL, Keypad, Notice, TextField } from "@flow4tech/design-system";
import { useEffect, useState } from "react";
import {
  type AcaoDeDesconto,
  type FormaDoDesconto,
  MOTIVOS_DE_DESCONTO,
  numeroValido,
  type PedidoDeDesconto,
  previaDoTotal,
  quantoTira,
} from "./desconto";
import type { ContaDaComanda, ItemDaConta } from "./rodada";

interface Props {
  /** Closed when null; `item: null` is the discount of the whole comanda. */
  alvo: { item: ItemDaConta | null } | null;
  onOpenChange: (open: boolean) => void;
  conta: ContaDaComanda;
  /** What blocks it now (no signal, actions of the comanda waiting in the queue); null when it can go. */
  bloqueio: string | null;
  enviando: boolean;
  erro: string | undefined;
  onAplicar: (pedido: PedidoDeDesconto) => void;
}

const ACOES: { acao: AcaoDeDesconto; rotulo: string }[] = [
  { acao: "desconto", rotulo: "Desconto" },
  { acao: "cortesia", rotulo: "Cortesia" },
  { acao: "brinde", rotulo: "Brinde" },
];

/**
 * Discount, courtesy or gift on one item (or a discount on the whole comanda): percent or value on a keypad, a mandatory reason, and the new total before confirming. The preview uses the
 * server's numbers of the bill; what the server answers on confirming replaces it. It needs a connection and never waits in the queue; a refusal says only that this value is not allowed.
 */
export function DescontoSheet({ alvo, onOpenChange, conta, bloqueio, enviando, erro, onAplicar }: Props) {
  const item = alvo?.item ?? null;
  const [acao, setAcao] = useState<AcaoDeDesconto>("desconto");
  const [forma, setForma] = useState<FormaDoDesconto>("percentual");
  const [digitos, setDigitos] = useState("");
  const [motivo, setMotivo] = useState<string | null>(null);
  const [texto, setTexto] = useState("");
  // biome-ignore lint/correctness/useExhaustiveDependencies: the form restarts each time the sheet opens on a target (the item id, or the comanda), not on every render of the same one.
  useEffect(() => {
    if (alvo) {
      setAcao("desconto");
      setForma("percentual");
      setDigitos("");
      setMotivo(null);
      setTexto("");
    }
  }, [alvo === null, item?.id]);

  const numero = digitos === "" ? 0 : Number(digitos);
  const linhaDoItem = item ? Math.max(0, (item.unitarioCentavos ?? 0) * item.quantidade) : 0;
  const base = item ? linhaDoItem : Math.max(0, conta.consumoCentavos - (conta.descontoCentavos ?? 0));
  const valorDeTabela = item?.valorDeTabelaCentavos || linhaDoItem;
  const tira = acao === "desconto" ? quantoTira(forma, numero, base) : valorDeTabela;
  const previa = previaDoTotal(conta, tira);
  const motivoFinal = motivo === "Outro" ? texto.trim() : (motivo ?? "");
  const numeroOk = acao !== "desconto" || numeroValido(forma, numero);
  const pronto = numeroOk && motivoFinal !== "" && bloqueio === null && (acao === "desconto" || item !== null);
  const rotulo = acao === "desconto" ? "Aplicar desconto" : acao === "cortesia" ? "Dar cortesia" : "Dar brinde";

  function aplicar() {
    if (acao === "desconto") {
      const itemId = item?.id ?? null;
      onAplicar(
        forma === "percentual"
          ? { acao, itemId, forma, percentual: numero, motivo: motivoFinal }
          : { acao, itemId, forma, valorCentavos: numero, motivo: motivoFinal },
      );
    } else if (item) {
      onAplicar({ acao, itemId: item.id, motivo: motivoFinal });
    }
  }

  function digitar(d: string) {
    const novo = `${digitos}${d}`.replace(/^0+/, "");
    setDigitos(novo.slice(0, forma === "percentual" ? 3 : 8));
  }

  const mostrado = forma === "percentual" ? `${numero}%` : formatBRL(numero);
  return (
    <BottomSheet
      open={alvo !== null}
      onOpenChange={onOpenChange}
      title={item ? "Desconto, cortesia ou brinde" : "Desconto na comanda"}
      description={item ? `${item.quantidade}× ${item.produtoNome}` : "Sai do consumo da comanda inteira."}
      loading={enviando}
      footer={
        <div className="desconto-rodape">
          {erro ? (
            <p role="alert" className="f4-field-error">
              {erro}
            </p>
          ) : null}
          <dl className="desconto-previa" aria-label="Prévia da conta">
            <div>
              <dt>Tira</dt>
              <dd className="f4-num">{formatBRL(tira)}</dd>
            </div>
            <div className="desconto-novo">
              <dt>Novo total</dt>
              <dd className="f4-num">{formatBRL(previa.totalCentavos)}</dd>
            </div>
          </dl>
          <div className="desconto-botoes">
            <Button variant="secondary" onClick={() => onOpenChange(false)} disabled={enviando}>
              Voltar
            </Button>
            <Button loading={enviando} disabled={!pronto} onClick={aplicar}>
              {rotulo}
            </Button>
          </div>
        </div>
      }
    >
      {bloqueio ? <Notice tone="info">{bloqueio}</Notice> : null}
      {item ? (
        <div className="motivos" role="group" aria-label="O que dar">
          {ACOES.map((a) => (
            <Chip key={a.acao} selected={acao === a.acao} onClick={() => setAcao(a.acao)}>
              {a.rotulo}
            </Chip>
          ))}
        </div>
      ) : null}
      {acao === "desconto" ? (
        <>
          <div className="motivos" role="group" aria-label="Forma do desconto">
            <Chip
              selected={forma === "percentual"}
              onClick={() => {
                setForma("percentual");
                setDigitos("");
              }}
            >
              Percentual
            </Chip>
            <Chip
              selected={forma === "valor"}
              onClick={() => {
                setForma("valor");
                setDigitos("");
              }}
            >
              Valor
            </Chip>
          </div>
          <p className="desconto-valor f4-num" aria-live="polite" aria-label="Desconto digitado">
            {mostrado}
          </p>
          <Keypad
            onDigit={digitar}
            onBackspace={() => setDigitos((d) => d.slice(0, -1))}
            onCancel={() => onOpenChange(false)}
            disabled={enviando}
            error={
              digitos !== "" && !numeroOk
                ? forma === "percentual"
                  ? "Use de 1% a 100%."
                  : "Use pelo menos R$ 0,01."
                : undefined
            }
          />
        </>
      ) : (
        <p className="desconto-valor-info">
          O item sai da conta (valor de tabela {formatBRL(valorDeTabela)}). O preço fica guardado para os relatórios.
        </p>
      )}
      <p id="desconto-motivo" className="desconto-rotulo">
        Motivo (obrigatório)
      </p>
      <div className="motivos" role="group" aria-labelledby="desconto-motivo">
        {MOTIVOS_DE_DESCONTO.map((m) => (
          <Chip key={m} selected={motivo === m} onClick={() => setMotivo(m)}>
            {m}
          </Chip>
        ))}
      </div>
      {motivo === "Outro" ? (
        <TextField
          label="Qual o motivo?"
          value={texto}
          maxLength={200}
          autoComplete="off"
          onChange={(e) => setTexto(e.target.value)}
        />
      ) : null}
    </BottomSheet>
  );
}
