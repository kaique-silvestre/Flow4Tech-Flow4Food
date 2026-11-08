import { BottomSheet, Button, Chip, formatBRL, TextField } from "@flow4tech/design-system";
import { useEffect, useState } from "react";
import { resumoDasOpcoes } from "./opcoes";
import type { PreviaDaRepeticao } from "./repetir";
import {
  type CodigoDeMotivo,
  DURACAO_DO_DESFAZER_MS,
  type ItemDaConta,
  type ItemEmPreparo,
  MOTIVOS,
  precoUnitario,
  totalEmPreparo,
} from "./rodada";

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
          <li key={i.chave} className="enviar-linha">
            <span className="f4-num">{i.quantidade}×</span>
            <span>
              {i.produto.nome}
              {i.opcoes.length > 0 || i.observacao ? (
                <span className="enviar-detalhe">
                  {[resumoDasOpcoes(i.opcoes), i.observacao].filter(Boolean).join(" · ")}
                </span>
              ) : null}
            </span>
            <span className="f4-num">{formatBRL(precoUnitario(i) * i.quantidade)}</span>
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
  // biome-ignore lint/correctness/useExhaustiveDependencies: the form resets when ANOTHER item is opened, so the item id is the trigger even though the body does not read it.
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
      {motivo === "outro" ? (
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

interface RepetirProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  previa: PreviaDaRepeticao;
  enviando: boolean;
  /** What did not go (the sheet stays open). */
  erro: string | undefined;
  onRepetir: () => void;
}

/** "Repetir última rodada": what goes (with the menu prices of now), what does not and why, the undo window; one tap to confirm. */
export function RepetirRodadaSheet({ open, onOpenChange, previa, enviando, erro, onRepetir }: RepetirProps) {
  const nada = previa.entram.length === 0;
  return (
    <BottomSheet
      open={open}
      onOpenChange={onOpenChange}
      title="Repetir última rodada"
      description={`Os mesmos itens, com os preços de agora. Você pode desfazer por ${DURACAO_DO_DESFAZER_MS / 1000} segundos.`}
      error={erro}
      loading={enviando}
      footer={
        <>
          <Button variant="secondary" onClick={() => onOpenChange(false)} disabled={enviando}>
            Voltar
          </Button>
          <Button loading={enviando} disabled={nada} onClick={onRepetir}>
            Repetir agora
          </Button>
        </>
      }
    >
      {nada ? (
        <p className="repetir-vazio">Nenhum item da última rodada está disponível agora.</p>
      ) : (
        <ul className="enviar-lista" aria-label="Itens que vão na rodada">
          {previa.entram.map(({ item, unitarioCentavos }) => (
            <li key={item.id} className="enviar-linha">
              <span className="f4-num">{item.quantidade}×</span>
              <span>
                {item.produtoNome}
                {item.opcoes.length > 0 || item.observacao ? (
                  <span className="enviar-detalhe">
                    {[resumoDasOpcoes(item.opcoes), item.observacao].filter(Boolean).join(" · ")}
                  </span>
                ) : null}
              </span>
              <span className="f4-num">{formatBRL(unitarioCentavos * item.quantidade)}</span>
            </li>
          ))}
        </ul>
      )}
      {previa.naoEntram.length > 0 ? (
        <div className="repetir-fora" role="group" aria-label="Itens que não entram">
          <p className="repetir-fora-titulo">Não entram</p>
          <ul className="enviar-lista">
            {previa.naoEntram.map((item) => (
              <li key={item.id} className="enviar-linha">
                <span className="f4-num">{item.quantidade}×</span>
                <span>
                  {item.produtoNome}
                  <span className="enviar-detalhe">Acabou ou saiu do cardápio</span>
                </span>
                <span />
              </li>
            ))}
          </ul>
        </div>
      ) : null}
      {nada ? null : (
        <p className="enviar-total">
          Total estimado <strong className="f4-num">{formatBRL(previa.totalCentavos)}</strong>
        </p>
      )}
    </BottomSheet>
  );
}
