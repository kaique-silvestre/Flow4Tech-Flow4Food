import { formatBRL, ToneBadge } from "@flow4tech/design-system";
import "./caixa.css";
import { type Contagem, PALAVRA_DA_SITUACAO, palavraDaDiferenca, type Turno, tomDaDiferenca } from "./logica";

function Linha({
  nome,
  esperado,
  contado,
  diferenca,
}: {
  nome: string;
  esperado: number;
  contado: number;
  diferenca: number;
}) {
  return (
    <li className="caixa-conferencia-linha">
      <span className="caixa-conferencia-nome">{nome}</span>
      <span className="caixa-conferencia-valores f4-num">
        Esperado {formatBRL(esperado)} · Contado {formatBRL(contado)}
      </span>
      <ToneBadge tone={tomDaDiferenca(diferenca)}>{palavraDaDiferenca(diferenca)}</ToneBadge>
    </li>
  );
}

/**
 * The count of a closed shift in words: cash first, then each counted method, then what happens to the difference (concluded, waiting for approval, approved).
 * Colour is never the only signal: every difference has its word.
 */
export function Conferencia({ turno, contagens }: { turno: Turno; contagens: readonly Contagem[] }) {
  return (
    <div className="caixa-resultado">
      <ul className="caixa-conferencia" aria-label="Conferência por método">
        <Linha
          nome="Dinheiro"
          esperado={turno.esperadoCentavos ?? 0}
          contado={turno.contadoCentavos ?? 0}
          diferenca={turno.diferencaCentavos ?? 0}
        />
        {contagens.map((c) => (
          <Linha
            key={c.metodoId}
            nome={c.metodoNome}
            esperado={c.esperadoCentavos}
            contado={c.contadoCentavos}
            diferenca={c.diferencaCentavos}
          />
        ))}
      </ul>
      <p className="caixa-situacao" data-situacao={turno.situacao}>
        {PALAVRA_DA_SITUACAO[turno.situacao]}
        {turno.situacao === "pendente" ? ": a diferença passou da tolerância e um gerente precisa decidir." : "."}
      </p>
    </div>
  );
}
