import {
  BottomSheet,
  Button,
  centsFromDigits,
  formatBRL,
  MoneyField,
  Notice,
  TextField,
} from "@flow4tech/design-system";
import { useEffect, useState } from "react";
import { useOnline } from "../fila/conexao";
import { api } from "../sessao/sessao";
import "./caixa.css";
import {
  LIMITE_DO_DESTINO,
  LIMITE_DO_MOTIVO,
  problemaDoMovimento,
  SEM_SINAL,
  type TipoDeMovimento,
  textoDoMovimento,
} from "./logica";

interface Props {
  tipo: TipoDeMovimento;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** The server answered the movement (or said the shift is gone): the screen reads the shift again. */
  onAtualizar: () => Promise<void>;
  onFeito: (mensagem: string) => void;
}

const TEXTOS = {
  suprimento: {
    titulo: "Suprimento",
    descricao: "Dinheiro que entra na gaveta durante o turno, por exemplo mais troco.",
    destino: "Origem do dinheiro (opcional)",
    botao: "Registrar suprimento",
  },
  sangria: {
    titulo: "Sangria",
    descricao: "Dinheiro que sai da gaveta durante o turno, por exemplo para o cofre.",
    destino: "Destino do dinheiro",
    botao: "Registrar sangria",
  },
} as const;

/** Supply or withdrawal of the open shift: value, reason and destination. It needs the server now and never waits in the queue; a refusal keeps what was typed. */
export function MovimentoSheet({ tipo, open, onOpenChange, onAtualizar, onFeito }: Props) {
  const online = useOnline();
  const [valor, setValor] = useState("");
  const [motivo, setMotivo] = useState("");
  const [destino, setDestino] = useState("");
  const [enviando, setEnviando] = useState(false);
  const [erro, setErro] = useState<string | undefined>(undefined);
  const textos = TEXTOS[tipo];
  useEffect(() => {
    if (open) {
      setValor("");
      setMotivo("");
      setDestino("");
      setErro(undefined);
    }
  }, [open]);

  const problema = problemaDoMovimento({ tipo, valorDigitos: valor, motivo, destino });

  async function enviar() {
    setErro(undefined);
    setEnviando(true);
    const centavos = centsFromDigits(valor);
    const corpo = {
      valorCentavos: centavos,
      motivo: motivo.trim(),
      destino: destino.trim() === "" ? null : destino.trim(),
    };
    try {
      const { response, error } =
        tipo === "sangria"
          ? await api.POST("/caixa/turnos/atual/sangrias", { body: corpo })
          : await api.POST("/caixa/turnos/atual/suprimentos", { body: corpo });
      if (response.ok) {
        await onAtualizar();
        onFeito(`${tipo === "sangria" ? "Sangria" : "Suprimento"} de ${formatBRL(centavos)} registrado.`);
        return;
      }

      if (response.status === 404 || response.status === 409) {
        await onAtualizar();
      }

      setErro(textoDoMovimento(tipo, response.status, (error as { codigo?: string } | undefined)?.codigo));
    } catch {
      setErro("Sem conexão. Isto precisa do servidor e não fica na fila. Tente de novo quando o sinal voltar.");
    } finally {
      setEnviando(false);
    }
  }

  return (
    <BottomSheet
      open={open}
      onOpenChange={onOpenChange}
      title={textos.titulo}
      description={textos.descricao}
      error={erro}
      loading={enviando}
      footer={
        <>
          <Button variant="secondary" onClick={() => onOpenChange(false)} disabled={enviando}>
            Voltar
          </Button>
          <Button loading={enviando} disabled={problema !== null || !online} onClick={() => void enviar()}>
            {textos.botao}
          </Button>
        </>
      }
    >
      {online ? null : <Notice tone="warning">{SEM_SINAL}</Notice>}
      <MoneyField label="Valor" digits={valor} onDigitsChange={setValor} disabled={enviando} />
      <TextField
        label="Motivo"
        value={motivo}
        maxLength={LIMITE_DO_MOTIVO}
        onChange={(e) => setMotivo(e.target.value)}
        disabled={enviando}
        autoComplete="off"
      />
      <TextField
        label={textos.destino}
        value={destino}
        maxLength={LIMITE_DO_DESTINO}
        onChange={(e) => setDestino(e.target.value)}
        disabled={enviando}
        autoComplete="off"
      />
    </BottomSheet>
  );
}
