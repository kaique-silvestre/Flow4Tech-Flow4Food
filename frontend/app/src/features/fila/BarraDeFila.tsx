import { Button, Notice, QueueList, type AcaoDaFila } from "@flow4tech/design-system";
import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { useOnline } from "./conexao";
import { iniciarProcessador, removerAcao, tentarAgora, useFila, type AcaoDaFilaDeRodada } from "./fila";

function situacao(a: AcaoDaFilaDeRodada, online: boolean, agora: number): string {
  if (a.estado === "enviando") {
    return "Enviando";
  }

  if (a.estado === "falhou") {
    return `Não enviada: ${a.motivo ?? "o servidor recusou"}`;
  }

  if (!online) {
    return "Aguardando conexão";
  }

  const espera = Math.ceil((a.proxima - agora) / 1000);
  return espera > 0 ? `${a.motivo ?? "Tentando de novo"} · próxima tentativa em ${espera} s` : (a.motivo ?? "Na fila");
}

/**
 * The offline notice and the visible queue (docs/design/refs/salao/12-offline.png, 13-fila.png). It also runs the queue: when the signal returns the pending actions go out in order, and the
 * screens that show what they changed are refreshed.
 */
export function BarraDeFila() {
  const queryClient = useQueryClient();
  const online = useOnline();
  const { acoes, mensagem } = useFila();
  const [aberta, setAberta] = useState(false);
  const [agora, setAgora] = useState(() => Date.now());

  useEffect(
    () =>
      iniciarProcessador((acao) => {
        void queryClient.invalidateQueries({ queryKey: ["comanda", acao.comandaId] });
        void queryClient.invalidateQueries({ queryKey: ["comandas"] });
        void queryClient.invalidateQueries({ queryKey: ["cardapio", "frequentes"] });
      }),
    [queryClient],
  );

  useEffect(() => {
    if (acoes.length === 0) {
      return undefined;
    }

    const timer = window.setInterval(() => setAgora(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [acoes.length]);

  const itens: AcaoDaFila[] = acoes.map((a) => ({ id: a.id, titulo: a.titulo, detalhe: a.detalhe, estado: a.estado, situacao: situacao(a, online, agora) }));
  if (online && acoes.length === 0 && !mensagem) {
    return null;
  }

  return (
    <div className="fila" aria-label="Conexão e ações pendentes">
      {!online ? <Notice tone="info">Sem conexão. Você pode seguir lançando. Nada se perde.</Notice> : null}
      {mensagem ? <Notice tone="info">{mensagem.texto}</Notice> : null}
      {acoes.length > 0 ? (
        <div className="fila-resumo">
          <Button variant="secondary" aria-expanded={aberta} onClick={() => setAberta((v) => !v)}>
            {acoes.length === 1 ? "1 ação pendente" : `${acoes.length} ações pendentes`}
          </Button>
          {aberta ? <QueueList acoes={itens} onTentar={tentarAgora} onRemover={removerAcao} /> : null}
        </div>
      ) : null}
    </div>
  );
}
