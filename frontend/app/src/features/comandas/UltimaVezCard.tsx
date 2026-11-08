import { Button } from "@flow4tech/design-system";
import type { UltimaVez } from "./consultas";
import { quandoFechou } from "./repetir";

/** One line of the card: "2× Chopp 300ml, 1× Porção de batata" (add-ons are not listed here, they come along when the items are added). */
export function resumoDaUltimaVez(itens: UltimaVez["itens"]): string {
  return itens.map((i) => `${i.quantidade}× ${i.produtoNome}`).join(", ");
}

interface Props {
  ultimaVez: UltimaVez;
  agora: number;
  onAdicionarTudo: () => void;
}

/** The grouped suggestion of the design ("last time at this table", docs/design/DESIGN.md): what the table had the last time its bill closed, and one button to bring it all in. */
export function UltimaVezCard({ ultimaVez, agora, onAdicionarTudo }: Props) {
  const quando = quandoFechou(ultimaVez.fechadaEm, agora);
  return (
    <section className="ultima-vez" aria-label="Última vez nesta mesa">
      <div className="ultima-vez-texto">
        <h3 className="ultima-vez-titulo">Última vez nesta mesa</h3>
        {quando ? <p className="ultima-vez-quando">{`${ultimaVez.numeroTexto} · fechada ${quando}`.trim()}</p> : null}
        <p className="ultima-vez-itens">{resumoDaUltimaVez(ultimaVez.itens)}</p>
      </div>
      <Button variant="secondary" aria-label="Adicionar tudo da última vez nesta mesa" onClick={onAdicionarTudo}>
        Adicionar tudo
      </Button>
    </section>
  );
}
