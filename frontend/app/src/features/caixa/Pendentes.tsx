import {
  BottomSheet,
  Button,
  centsFromDigits,
  formatBRL,
  MoneyField,
  Notice,
  Skeleton,
  TextField,
  ToneBadge,
} from "@flow4tech/design-system";
import { useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useOnline } from "../fila/conexao";
import { api } from "../sessao/sessao";
import { Conferencia } from "./Conferencia";
import "./caixa.css";
import { useDetalheDoTurno, usePendentes } from "./dados";
import {
  diaEHora,
  hora,
  LIMITE_DO_MOTIVO,
  montarContagens,
  palavraDaDiferenca,
  SEM_SINAL,
  textoDaDecisao,
  tomDaDiferenca,
} from "./logica";

/** The manager's view: closed shifts whose difference passed the tolerance and wait for a decision (caixa:ver-todos). Tapping one opens its count, with approve and recount. */
export function Pendentes() {
  const consulta = usePendentes(true);
  const [aberto, setAberto] = useState<string | null>(null);
  return (
    <section className="caixa-secao" aria-labelledby="caixa-pendentes-titulo">
      <h3 id="caixa-pendentes-titulo" className="caixa-subtitulo">
        Diferenças para aprovar
      </h3>
      {consulta.isPending ? (
        <Skeleton rows={2} label="Carregando as diferenças pendentes" />
      ) : consulta.isError ? (
        <Notice
          tone="error"
          action={
            <Button variant="secondary" onClick={() => void consulta.refetch()}>
              Tentar novamente
            </Button>
          }
        >
          Não foi possível carregar as diferenças pendentes.
        </Notice>
      ) : consulta.data.length === 0 ? (
        <p className="caixa-vazio">Nenhuma diferença esperando decisão.</p>
      ) : (
        <ul className="caixa-lista">
          {consulta.data.map((t) => (
            <li key={t.id}>
              <button type="button" className="caixa-pendente" onClick={() => setAberto(t.id)}>
                <span className="caixa-pendente-quando">
                  Caixa de {diaEHora(t.abertoEm)}
                  {t.fechadoEm ? ` a ${hora(t.fechadoEm)}` : ""}
                </span>
                <ToneBadge tone={tomDaDiferenca(t.diferencaCentavos ?? 0)}>
                  {t.diferencaTexto ?? palavraDaDiferenca(t.diferencaCentavos ?? 0)}
                </ToneBadge>
              </button>
            </li>
          ))}
        </ul>
      )}
      <DecisaoSheet key={aberto ?? "nenhum"} id={aberto} onClose={() => setAberto(null)} />
    </section>
  );
}

type Modo = "ver" | "aprovar" | "recontar";

/** Keyed by the shift, so every shift starts at the count, with empty fields. */
function DecisaoSheet({ id, onClose }: { id: string | null; onClose: () => void }) {
  const queryClient = useQueryClient();
  const online = useOnline();
  const detalhe = useDetalheDoTurno(id);
  const [modo, setModo] = useState<Modo>("ver");
  const [motivo, setMotivo] = useState("");
  const [dinheiro, setDinheiro] = useState("");
  const [porMetodo, setPorMetodo] = useState<Record<string, string>>({});
  const [enviando, setEnviando] = useState(false);
  const [erro, setErro] = useState<string | undefined>(undefined);
  const [feito, setFeito] = useState<string | null>(null);

  async function atualizar() {
    await queryClient.invalidateQueries({ queryKey: ["caixa"] });
  }

  async function decidir(acao: "aprovar" | "recontar") {
    if (id === null) {
      return;
    }

    setErro(undefined);
    setEnviando(true);
    const params = { params: { path: { id } } };
    try {
      const { data, error, response } =
        acao === "aprovar"
          ? await api.POST("/caixa/turnos/{id}/diferenca/aprovar", { ...params, body: { motivo: motivo.trim() } })
          : await api.POST("/caixa/turnos/{id}/diferenca/recontar", {
              ...params,
              body: {
                contadoCentavos: centsFromDigits(dinheiro),
                motivo: motivo.trim(),
                contagens: montarContagens(porMetodo),
              },
            });
      if (data) {
        setFeito(
          acao === "aprovar"
            ? "Diferença aprovada."
            : data.situacao === "pendente"
              ? "Recontagem registrada. A diferença continua acima da tolerância e segue pendente."
              : "Recontagem registrada. A diferença ficou dentro da tolerância e o caixa foi conferido.",
        );
        await atualizar();
      } else {
        setErro(textoDaDecisao(acao, response.status, (error as { codigo?: string } | undefined)?.codigo));
        if (response.status === 404 || response.status === 409) {
          await atualizar();
        }
      }
    } catch {
      setErro("Sem conexão. A decisão precisa do servidor e não fica na fila. Tente de novo quando o sinal voltar.");
    } finally {
      setEnviando(false);
    }
  }

  const turno = detalhe.data?.turno;
  const motivoOk = motivo.trim() !== "" && motivo.length <= LIMITE_DO_MOTIVO;
  const titulo = feito
    ? "Decisão registrada"
    : modo === "aprovar"
      ? "Aprovar diferença"
      : modo === "recontar"
        ? "Recontar caixa"
        : "Diferença do caixa";
  return (
    <BottomSheet
      open={id !== null}
      onOpenChange={(aberto) => {
        if (!aberto) {
          onClose();
        }
      }}
      title={titulo}
      description={turno && !feito ? `Caixa aberto às ${hora(turno.abertoEm)}.` : undefined}
      error={erro}
      loading={enviando || detalhe.isPending}
      footer={
        feito ? (
          <Button onClick={onClose}>Concluir</Button>
        ) : modo === "ver" ? (
          <>
            <Button variant="secondary" onClick={onClose}>
              Voltar
            </Button>
            <Button variant="secondary" disabled={!turno} onClick={() => setModo("recontar")}>
              Recontar
            </Button>
            <Button disabled={!turno || turno.situacao !== "pendente"} onClick={() => setModo("aprovar")}>
              Aprovar
            </Button>
          </>
        ) : (
          <>
            <Button variant="secondary" onClick={() => setModo("ver")} disabled={enviando}>
              Voltar
            </Button>
            <Button
              loading={enviando}
              disabled={!online || !motivoOk || (modo === "recontar" && dinheiro === "")}
              onClick={() => void decidir(modo)}
            >
              {modo === "aprovar" ? "Aprovar" : "Recontar"}
            </Button>
          </>
        )
      }
    >
      {feito ? (
        <p className="caixa-lead">{feito}</p>
      ) : detalhe.isPending ? (
        <Skeleton rows={3} label="Carregando o caixa" />
      ) : detalhe.isError ? (
        <Notice
          tone="error"
          action={
            <Button variant="secondary" onClick={() => void detalhe.refetch()}>
              Tentar novamente
            </Button>
          }
        >
          Não foi possível carregar este caixa.
        </Notice>
      ) : (
        <>
          {online ? null : <Notice tone="warning">{SEM_SINAL}</Notice>}
          {modo === "ver" ? (
            <>
              <Conferencia turno={detalhe.data.turno} contagens={detalhe.data.contagens} />
              <p className="f4-field-hint">
                Suprimentos {formatBRL(detalhe.data.turno.suprimentosCentavos)} · Sangrias{" "}
                {formatBRL(detalhe.data.turno.sangriasCentavos)}
              </p>
              {detalhe.data.aprovacoes.length > 0 ? (
                <ul className="caixa-lista" aria-label="Decisões anteriores">
                  {detalhe.data.aprovacoes.map((a) => (
                    <li key={a.id} className="caixa-aprovacao">
                      {a.tipo === "recontagem" ? "Recontagem" : "Aprovação"}: {a.motivo}
                    </li>
                  ))}
                </ul>
              ) : null}
            </>
          ) : (
            <div className="caixa-contagem">
              {modo === "recontar" ? (
                <>
                  <MoneyField
                    label="Dinheiro recontado"
                    digits={dinheiro}
                    onDigitsChange={setDinheiro}
                    disabled={enviando}
                  />
                  {detalhe.data.contagens.map((c) => (
                    <MoneyField
                      key={c.metodoId}
                      label={`${c.metodoNome} recontado`}
                      hint="Deixe em branco se não conferiu."
                      digits={porMetodo[c.metodoId] ?? ""}
                      onDigitsChange={(d) => setPorMetodo((atual) => ({ ...atual, [c.metodoId]: d }))}
                      disabled={enviando}
                    />
                  ))}
                </>
              ) : null}
              <TextField
                label="Motivo"
                value={motivo}
                maxLength={LIMITE_DO_MOTIVO}
                onChange={(e) => setMotivo(e.target.value)}
                disabled={enviando}
                autoComplete="off"
              />
            </div>
          )}
        </>
      )}
    </BottomSheet>
  );
}
