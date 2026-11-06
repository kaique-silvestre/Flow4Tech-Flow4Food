import { BottomSheet, Button, Chip, formatBRL, TextField } from "@flow4tech/design-system";
import { useEffect, useState } from "react";
import { DURACAO_DO_DESFAZER_MS, MOTIVOS, totalEmPreparo, type CodigoDeMotivo, type ItemDaConta, type ItemEmPreparo } from "./rodada";

interface EnviarProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  itens: readonly ItemEmPreparo[];
  enviando: boolean;
  /** What did not go (the round stays staged). */
  erro: string | undefined;
  onEnviar: () => void;
}

/** The send sheet (docs/design/refs/salao/03.png): where it goes, the undo window, the itemized round, Voltar and the accent Enviar agora. */
export function EnviarRodadaSheet({ open, onOpenChange, itens, enviando, erro, onEnviar }: EnviarProps) {
  return (
    <BottomSheet
      open={open}
      onOpenChange={onOpenChange}
      title="Enviar rodada"
      description={`Vai para a cozinha e o bar. Você pode desfazer por ${DURACAO_DO_DESFAZER_MS / 1000} segundos.`}
      error={erro}
      loading={enviando}
      footer={
        <>
          <Button variant="secondary" onClick={() => onOpenChange(false)} disabled={enviando}>
            Voltar
          </Button>
          <Button loading={enviando} onClick={onEnviar}>
            Enviar agora
          </Button>
        </>
      }
    >
      <ul className="enviar-lista" aria-label="Itens da rodada">
        {itens.map((i) => (
          <li key={i.produto.id} className="enviar-linha">
            <span className="f4-num">{i.quantidade}×</span>
            <span>{i.produto.nome}</span>
            <span className="f4-num">{formatBRL(i.produto.precoCentavos * i.quantidade)}</span>
          </li>
        ))}
      </ul>
      <p className="enviar-total">
        Total da rodada <strong className="f4-num">{formatBRL(totalEmPreparo(itens))}</strong>
      </p>
    </BottomSheet>
  );
}

interface CancelarProps {
  item: ItemDaConta | null;
  onOpenChange: (open: boolean) => void;
  enviando: boolean;
  erro: string | undefined;
  onCancelar: (motivo: CodigoDeMotivo, texto: string) => void;
}

/** Cancelling an item needs a reason from the standard list; "Outro" asks for text. The item leaves the bill when the server accepts it. */
export function CancelarItemSheet({ item, onOpenChange, enviando, erro, onCancelar }: CancelarProps) {
  const [motivo, setMotivo] = useState<CodigoDeMotivo | null>(null);
  const [texto, setTexto] = useState("");
  useEffect(() => {
    setMotivo(null);
    setTexto("");
  }, [item?.id]);

  const valido = motivo !== null && (motivo !== "outro" || texto.trim() !== "");
  return (
    <BottomSheet
      open={item !== null}
      onOpenChange={onOpenChange}
      title="Cancelar item"
      description={item ? `${item.quantidade}× ${item.produtoNome}. Escolha o motivo.` : undefined}
      error={erro}
      loading={enviando}
      footer={
        <>
          <Button variant="secondary" onClick={() => onOpenChange(false)} disabled={enviando}>
            Voltar
          </Button>
          <Button loading={enviando} disabled={!valido} onClick={() => motivo && onCancelar(motivo, texto.trim())}>
            Cancelar item
          </Button>
        </>
      }
    >
      <div className="motivos" role="group" aria-label="Motivo do cancelamento">
        {MOTIVOS.map((m) => (
          <Chip key={m.codigo} selected={motivo === m.codigo} onClick={() => setMotivo(m.codigo)}>
            {m.rotulo}
          </Chip>
        ))}
      </div>
      {motivo === "outro" ? <TextField label="Qual o motivo?" value={texto} maxLength={200} autoComplete="off" onChange={(e) => setTexto(e.target.value)} /> : null}
    </BottomSheet>
  );
}
