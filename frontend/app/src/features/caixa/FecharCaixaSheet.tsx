import {
  BottomSheet,
  Button,
  centsFromDigits,
  formatBRL,
  MoneyField,
  Notice,
  ToneBadge,
} from "@flow4tech/design-system";
import { useEffect, useState } from "react";
import { useOnline } from "../fila/conexao";
import { api } from "../sessao/sessao";
import { Conferencia } from "./Conferencia";
import "./caixa.css";
import {
  esperadoDaGaveta,
  montarContagens,
  palavraDaDiferenca,
  paraTurno,
  type Resumo,
  SEM_SINAL,
  type Turno,
  textoDoFechamento,
  tomDaDiferenca,
} from "./logica";

interface Props {
  resumo: Resumo;
  open: boolean;
  /** The sheet went away (voltar, concluir or Escape): the screen reads the shift again. */
  onClose: () => void;
}

const AVISO_CEGO = "Conferência cega: o valor esperado só aparece depois que você enviar a contagem.";

/**
 * Closing the shift: the cash counted and, for each other method that took money, what was counted in it (a blank line is not counted).
 * With the blind count the expected values are not shown (the server does not even send them); the difference in words appears only after sending.
 */
export function FecharCaixaSheet({ resumo, open, onClose }: Props) {
  const online = useOnline();
  const [dinheiro, setDinheiro] = useState("");
  const [porMetodo, setPorMetodo] = useState<Record<string, string>>({});
  const [enviando, setEnviando] = useState(false);
  const [erro, setErro] = useState<string | undefined>(undefined);
  const [resultado, setResultado] = useState<Turno | null>(null);
  useEffect(() => {
    if (open) {
      setDinheiro("");
      setPorMetodo({});
      setErro(undefined);
      setResultado(null);
    }
  }, [open]);

  const esperado = esperadoDaGaveta(resumo);
  const cego = esperado === null;
  const diferencaDinheiro = !cego && dinheiro !== "" ? centsFromDigits(dinheiro) - esperado : null;

  async function fechar() {
    setErro(undefined);
    setEnviando(true);
    try {
      const contagens = montarContagens(porMetodo);
      const { data, response } = await api.POST("/caixa/turnos/atual/fechar", {
        body: { contadoCentavos: centsFromDigits(dinheiro), ...(contagens.length > 0 ? { contagens } : {}) },
      });
      if (data) {
        setResultado(paraTurno(data));
      } else {
        setErro(textoDoFechamento(response.status));
      }
    } catch {
      setErro("Sem conexão. O fechamento precisa do servidor e não fica na fila. Tente de novo quando o sinal voltar.");
    } finally {
      setEnviando(false);
    }
  }

  return (
    <BottomSheet
      open={open}
      onOpenChange={(aberto) => {
        if (!aberto) {
          onClose();
        }
      }}
      title={resultado ? "Caixa fechado" : "Fechar caixa"}
      description={
        resultado
          ? undefined
          : "Conte a gaveta e, se recebeu em outros métodos, o que foi conferido neles. Pode ser R$ 0,00."
      }
      error={erro}
      loading={enviando}
      footer={
        resultado ? (
          <Button onClick={onClose}>Concluir</Button>
        ) : (
          <>
            <Button variant="secondary" onClick={onClose} disabled={enviando}>
              Voltar
            </Button>
            <Button loading={enviando} disabled={dinheiro === "" || !online} onClick={() => void fechar()}>
              Fechar caixa
            </Button>
          </>
        )
      }
    >
      {resultado ? (
        <Conferencia turno={resultado} contagens={resultado.contagens} />
      ) : (
        <div className="caixa-contagem">
          {online ? null : <Notice tone="warning">{SEM_SINAL}</Notice>}
          {cego ? <Notice tone="info">{AVISO_CEGO}</Notice> : null}
          <MoneyField
            label="Dinheiro contado"
            hint={cego ? undefined : `Esperado na gaveta: ${formatBRL(esperado)}`}
            digits={dinheiro}
            onDigitsChange={setDinheiro}
            disabled={enviando}
          />
          {diferencaDinheiro !== null ? (
            <ToneBadge tone={tomDaDiferenca(diferencaDinheiro)}>{palavraDaDiferenca(diferencaDinheiro)}</ToneBadge>
          ) : null}
          {resumo.porMetodo.map((m) => {
            const digitos = porMetodo[m.metodoId] ?? "";
            const esperadoDoMetodo = cego ? null : m.esperadoCentavos;
            const diferenca =
              esperadoDoMetodo !== null && digitos !== "" ? centsFromDigits(digitos) - esperadoDoMetodo : null;
            return (
              <div key={m.metodoId} className="caixa-metodo">
                <MoneyField
                  label={`${m.metodoNome} conferido`}
                  hint={
                    esperadoDoMetodo === null
                      ? "Deixe em branco se não conferiu."
                      : `Esperado: ${formatBRL(esperadoDoMetodo)}. Deixe em branco se não conferiu.`
                  }
                  digits={digitos}
                  onDigitsChange={(d) => setPorMetodo((atual) => ({ ...atual, [m.metodoId]: d }))}
                  disabled={enviando}
                />
                {diferenca !== null ? (
                  <ToneBadge tone={tomDaDiferenca(diferenca)}>{palavraDaDiferenca(diferenca)}</ToneBadge>
                ) : null}
              </div>
            );
          })}
        </div>
      )}
    </BottomSheet>
  );
}
