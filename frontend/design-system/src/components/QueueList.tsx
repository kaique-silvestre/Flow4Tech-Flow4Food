export type EstadoDaAcao = "pendente" | "enviando" | "falhou";

export interface AcaoDaFila {
  id: string;
  /** What it is ("Rodada · #7"). */
  titulo: string;
  /** One line of detail ("3 itens · R$ 59,90"). */
  detalhe: string;
  estado: EstadoDaAcao;
  /** The state in words ("Aguardando conexão", "Tentando de novo em 8 s", "Não enviada: item indisponível"). */
  situacao: string;
}

export interface QueueListProps {
  acoes: readonly AcaoDaFila[];
  /** Send now instead of waiting for the next attempt (pending actions). */
  onTentar: (id: string) => void;
  /** Drop an action that will not go (a failed one) or that the waiter no longer wants. */
  onRemover: (id: string) => void;
}

const MARCADOR: Record<EstadoDaAcao, string> = { pendente: "pendente", enviando: "enviando", falhou: "falhou" };

/** The visible queue of pending actions (docs/design/refs/salao/13-fila.png): oldest first, each with its state in words and a marker that differs in shape, never color alone. */
export function QueueList({ acoes, onTentar, onRemover }: QueueListProps) {
  return (
    <ol className="f4-queue" aria-label="Ações pendentes">
      {acoes.map((a, i) => (
        <li key={a.id} className="f4-queue-item" data-estado={MARCADOR[a.estado]}>
          <span className="f4-queue-ordem f4-num" aria-hidden="true">
            {i + 1}
          </span>
          <span className="f4-queue-corpo">
            <span className="f4-queue-titulo">{a.titulo}</span>
            <span className="f4-queue-detalhe">{a.detalhe}</span>
            <span className="f4-queue-situacao">
              <span className="f4-queue-marcador" aria-hidden="true" />
              {a.situacao}
            </span>
          </span>
          <span className="f4-queue-acoes">
            {a.estado === "pendente" ? (
              <button type="button" className="f4-row-action" onClick={() => onTentar(a.id)}>
                Tentar agora
              </button>
            ) : null}
            {a.estado !== "enviando" ? (
              <button type="button" className="f4-row-action" aria-label={`Remover ${a.titulo}`} onClick={() => onRemover(a.id)}>
                Remover
              </button>
            ) : null}
          </span>
        </li>
      ))}
    </ol>
  );
}
