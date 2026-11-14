import { Button, formatBRL, Notice, Skeleton } from "@flow4tech/design-system";
import type { UseQueryResult } from "@tanstack/react-query";
import "./caixa.css";
import { hora, type Movimento } from "./logica";

/** The supplies and withdrawals of the shift: what, how much, why and where to, newest last as the server sends them. */
export function Movimentos({ consulta }: { consulta: UseQueryResult<Movimento[]> }) {
  return (
    <section className="caixa-secao" aria-labelledby="caixa-movimentos-titulo">
      <h3 id="caixa-movimentos-titulo" className="caixa-subtitulo">
        Movimentos do turno
      </h3>
      {consulta.isPending ? (
        <Skeleton rows={2} label="Carregando os movimentos" />
      ) : consulta.isError ? (
        <Notice
          tone="error"
          action={
            <Button variant="secondary" onClick={() => void consulta.refetch()}>
              Tentar novamente
            </Button>
          }
        >
          Não foi possível carregar os movimentos.
        </Notice>
      ) : consulta.data.length === 0 ? (
        <p className="caixa-vazio">Nenhum suprimento ou sangria neste turno.</p>
      ) : (
        <ul className="caixa-lista">
          {consulta.data.map((m) => (
            <li key={m.id} className="caixa-movimento" data-tipo={m.tipo}>
              <span className="caixa-movimento-tipo">{m.tipo === "sangria" ? "Sangria" : "Suprimento"}</span>
              <span className="caixa-movimento-valor f4-num">
                {m.tipo === "sangria" ? "−" : "+"}
                {formatBRL(m.valorCentavos)}
              </span>
              <span className="caixa-movimento-motivo">
                {m.motivo}
                {m.destino ? ` · ${m.destino}` : ""}
              </span>
              <span className="caixa-movimento-hora f4-num">{hora(m.criadoEm)}</span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
