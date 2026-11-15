import { Button, formatBRL, Notice, Skeleton } from "@flow4tech/design-system";
import { Link } from "@tanstack/react-router";
import { useState } from "react";
import { useOnline } from "../fila/conexao";
import {
  buscarTexto,
  type Comprovante,
  compartilharTexto,
  falhaDe,
  horaCurta,
  rotuloDoItem,
  type TipoDeComprovante,
  textoDaFalha,
  useComprovante,
} from "./comprovante";
import "./comprovante.css";

/** The document as the server wrote it. Pre-bill and receipt share it; the receipt adds the payments. Never fiscal: the notice from the server is always shown. */
export function CorpoDoComprovante({ c }: { c: Comprovante }) {
  const { conta } = c;
  return (
    <div className="comp">
      <p className="comp-aviso">{c.aviso}</p>
      {c.segundaVia ? <p className="comp-via">2ª via</p> : null}
      <ul className="comp-itens" aria-label="Itens">
        {c.itens.map((i, n) => {
          const rotulo = rotuloDoItem(i.tipo);
          const detalhe = [...i.opcoes, i.observacao].filter(Boolean).join(" · ");
          return (
            // biome-ignore lint/suspicious/noArrayIndexKey: the document is read-only and has no item ids.
            <li key={n} className="comp-item">
              <span className="f4-num">{i.quantidade}×</span>
              <span>
                {i.descricao}
                {rotulo ? <span className="comp-rotulo">{rotulo}</span> : null}
                {detalhe ? <span className="comp-detalhe">{detalhe}</span> : null}
              </span>
              <span className="f4-num">{formatBRL(i.totalCentavos)}</span>
            </li>
          );
        })}
      </ul>
      <dl className="comp-conta" aria-label="Conta">
        <div>
          <dt>Consumo</dt>
          <dd className="f4-num">{formatBRL(conta.consumoCentavos)}</dd>
        </div>
        {conta.descontoCentavos > 0 ? (
          <div>
            <dt>Desconto</dt>
            <dd className="f4-num">−{formatBRL(conta.descontoCentavos)}</dd>
          </div>
        ) : null}
        <div>
          <dt>{conta.taxaServicoRemovida ? "Serviço (removido)" : `Serviço (${conta.taxaServicoPercentual}%)`}</dt>
          <dd className="f4-num">{formatBRL(conta.taxaServicoCentavos)}</dd>
        </div>
        <div className="comp-total">
          <dt>Total</dt>
          <dd className="f4-num">{formatBRL(conta.totalCentavos)}</dd>
        </div>
        {conta.pagoCentavos > 0 ? (
          <div>
            <dt>Pago</dt>
            <dd className="f4-num">{formatBRL(conta.pagoCentavos)}</dd>
          </div>
        ) : null}
        {c.tipo === "pre-conta" || conta.saldoCentavos > 0 ? (
          <div>
            <dt>Falta pagar</dt>
            <dd className="f4-num">{formatBRL(conta.saldoCentavos)}</dd>
          </div>
        ) : null}
      </dl>
      {c.partes && c.partes.length > 0 ? (
        <ul className="comp-partes" aria-label="Partes da divisão">
          {c.partes.map((p) => (
            <li key={p.numero}>
              <span>Parte {p.numero}</span>
              <span className="f4-num">{formatBRL(p.totalCentavos)}</span>
            </li>
          ))}
        </ul>
      ) : null}
      {c.pagamentos.length > 0 ? (
        <ul className="comp-partes" aria-label="Pagamentos">
          {c.pagamentos.map((p, n) => (
            // biome-ignore lint/suspicious/noArrayIndexKey: the document is read-only and has no payment ids.
            <li key={n}>
              <span>
                {p.metodoNome}
                {p.gorjetaCentavos > 0 ? ` · gorjeta ${formatBRL(p.gorjetaCentavos)}` : ""}
              </span>
              <span className="f4-num">{formatBRL(p.aplicadoCentavos)}</span>
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}

const TITULO: Record<TipoDeComprovante, string> = { "pre-conta": "Pré-conta", recibo: "Recibo" };

/** Share (the phone's sheet, else copy) the server's plain text. Offline it says so and keeps the document on screen. */
export function useCompartilhar(tipo: TipoDeComprovante, comandaId: string) {
  const [ocupado, setOcupado] = useState(false);
  const [aviso, setAviso] = useState<string | undefined>(undefined);
  const [erro, setErro] = useState<string | undefined>(undefined);

  async function compartilhar() {
    setOcupado(true);
    setAviso(undefined);
    setErro(undefined);
    try {
      const texto = await buscarTexto(tipo, comandaId);
      const r = await compartilharTexto(TITULO[tipo], texto);
      if (r === "copiado") {
        setAviso("Texto copiado. Cole onde quiser enviar.");
      } else if (r === "compartilhado") {
        setAviso("Compartilhado.");
      } else if (r === "indisponivel") {
        setErro("Este aparelho não deixou compartilhar nem copiar.");
      }
    } catch (e) {
      setErro(textoDaFalha(falhaDe(e), tipo));
    } finally {
      setOcupado(false);
    }
  }

  return { ocupado, aviso, erro, compartilhar };
}

interface PaginaProps {
  comandaId: string;
  nome: string;
}

/** The receipt of a closed comanda (always a second copy; each read is audited by the server). A page, reached from the closed bill or by link. */
export function ReciboDaComanda({ comandaId, nome }: PaginaProps) {
  const recibo = useComprovante("recibo", comandaId, true);
  const { ocupado, aviso, erro, compartilhar } = useCompartilhar("recibo", comandaId);
  const online = useOnline();

  if (recibo.isPending) {
    return <Skeleton rows={4} label="Carregando o recibo" />;
  }

  if (recibo.isError) {
    return (
      <Notice
        tone="error"
        action={
          falhaDe(recibo.error) === "rede" || falhaDe(recibo.error) === "outro" ? (
            <Button variant="secondary" onClick={() => void recibo.refetch()}>
              Tentar novamente
            </Button>
          ) : undefined
        }
      >
        {textoDaFalha(falhaDe(recibo.error), "recibo")}
      </Notice>
    );
  }

  const c = recibo.data;
  return (
    <section className="comp-pagina" aria-labelledby="recibo-titulo">
      <header className="comanda-topo">
        <h2 id="recibo-titulo" className="app-empty-title">
          Recibo · {nome === "" ? c.identificacao : nome}
        </h2>
        <p className="comp-sub">
          {c.numeroTexto}
          {c.fechadaEm ? ` · fechada às ${horaCurta(c.fechadaEm)}` : ""}
        </p>
      </header>
      <CorpoDoComprovante c={c} />
      {!online ? <Notice tone="info">Sem conexão. Para compartilhar, espere o sinal voltar.</Notice> : null}
      {aviso ? (
        <p role="status" className="comp-status">
          {aviso}
        </p>
      ) : null}
      {erro ? (
        <p role="alert" className="f4-field-error">
          {erro}
        </p>
      ) : null}
      <div className="comp-acoes">
        <Button loading={ocupado} disabled={!online} onClick={() => void compartilhar()}>
          Compartilhar
        </Button>
        <Link to="/salao" className="f4-btn comp-voltar" data-variant="secondary">
          Voltar às comandas
        </Link>
      </div>
    </section>
  );
}
