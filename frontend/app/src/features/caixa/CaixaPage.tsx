import type { components } from "@flow4tech/api-client";
import {
  BottomSheet,
  Button,
  centsFromDigits,
  formatBRL,
  MoneyField,
  Notice,
  Skeleton,
  ToneBadge,
} from "@flow4tech/design-system";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { usePermissoes } from "../sessao/permissoes";
import { api } from "../sessao/sessao";

interface Turno {
  id: string;
  abertoEm: string;
  fundoCentavos: number;
  recebidoDinheiroCentavos: number;
  contadoCentavos: number | null;
  esperadoCentavos: number | null;
  diferencaCentavos: number | null;
  diferencaTexto: string | null;
}

type Leitura = { tipo: "turno"; turno: Turno } | { tipo: "fechado" } | { tipo: "semPermissao" };

function paraTurno(t: components["schemas"]["TurnoDto"]): Turno {
  const n = (v: number | string | null | undefined) => (v === null || v === undefined ? null : Number(v));
  return {
    id: t.id,
    abertoEm: t.abertoEm,
    fundoCentavos: Number(t.fundoCentavos),
    recebidoDinheiroCentavos: Number(t.recebidoDinheiroCentavos),
    contadoCentavos: n(t.contadoCentavos),
    esperadoCentavos: n(t.esperadoCentavos),
    diferencaCentavos: n(t.diferencaCentavos),
    diferencaTexto: t.diferencaTexto ?? null,
  };
}

function hora(iso: string): string {
  return new Date(iso).toLocaleTimeString("pt-BR", { hour: "2-digit", minute: "2-digit" });
}

/** Tone of the difference in words: exact is calm, a shortage is an alert, a surplus is a warning. Never color alone: the word is always there. */
export function tomDaDiferenca(diferencaCentavos: number): "success" | "alert" | "warning" {
  return diferencaCentavos === 0 ? "success" : diferencaCentavos < 0 ? "alert" : "warning";
}

export function CaixaPage() {
  const queryClient = useQueryClient();
  const permissoes = usePermissoes();
  const [fundo, setFundo] = useState("");
  const [abrindo, setAbrindo] = useState(false);
  const [erroAbrir, setErroAbrir] = useState<{ texto: string; atualizar: boolean } | null>(null);
  const [fechando, setFechando] = useState(false);
  const [contagem, setContagem] = useState("");
  const [enviando, setEnviando] = useState(false);
  const [erroFechar, setErroFechar] = useState<string | null>(null);
  const [resultado, setResultado] = useState<Turno | null>(null);

  const leitura = useQuery<Leitura>({
    queryKey: ["caixa", "atual"],
    queryFn: async () => {
      const { data, response } = await api.GET("/caixa/turnos/atual");
      if (data) {
        return { tipo: "turno", turno: paraTurno(data) };
      }

      if (response.status === 404) {
        return { tipo: "fechado" };
      }

      if (response.status === 403) {
        return { tipo: "semPermissao" };
      }

      throw new Error("caixa");
    },
  });

  async function abrir() {
    setErroAbrir(null);
    setAbrindo(true);
    try {
      const { data, response } = await api.POST("/caixa/turnos", { body: { fundoCentavos: centsFromDigits(fundo) } });
      if (data) {
        setFundo("");
        await queryClient.invalidateQueries({ queryKey: ["caixa"] });
      } else if (response.status === 409) {
        setErroAbrir({ texto: "Você já tem um caixa aberto nesta unidade.", atualizar: true });
      } else if (response.status === 403) {
        setErroAbrir({ texto: "Você não tem permissão para abrir o caixa.", atualizar: false });
      } else {
        setErroAbrir({ texto: "Não foi possível abrir o caixa. Confira o valor e tente de novo.", atualizar: false });
      }
    } catch {
      setErroAbrir({ texto: "Sem conexão. Tente de novo.", atualizar: false });
    } finally {
      setAbrindo(false);
    }
  }

  async function fechar() {
    setErroFechar(null);
    setEnviando(true);
    try {
      const { data, response } = await api.POST("/caixa/turnos/atual/fechar", {
        body: { contadoCentavos: centsFromDigits(contagem) },
      });
      if (data) {
        setResultado(paraTurno(data));
      } else if (response.status === 404) {
        setErroFechar("Este caixa já foi fechado. Atualize a tela.");
      } else if (response.status === 403) {
        setErroFechar("Você não tem permissão para fechar o caixa.");
      } else {
        setErroFechar("Não foi possível fechar o caixa. Tente de novo.");
      }
    } catch {
      setErroFechar("Sem conexão. Tente de novo.");
    } finally {
      setEnviando(false);
    }
  }

  async function concluir() {
    setFechando(false);
    setResultado(null);
    setContagem("");
    await queryClient.invalidateQueries({ queryKey: ["caixa"] });
  }

  if (leitura.isPending || !permissoes.carregado) {
    return <Skeleton rows={3} label="Carregando o caixa" />;
  }

  if (leitura.isError) {
    return (
      <Notice
        tone="error"
        action={
          <Button variant="secondary" onClick={() => void leitura.refetch()}>
            Tentar novamente
          </Button>
        }
      >
        Não foi possível carregar o caixa.
      </Notice>
    );
  }

  if (leitura.data.tipo === "semPermissao") {
    return <Notice tone="info">Você não tem permissão para ver o caixa.</Notice>;
  }

  if (leitura.data.tipo === "fechado") {
    return (
      <section className="caixa" aria-labelledby="caixa-titulo">
        <h2 id="caixa-titulo" className="app-empty-title">
          Caixa fechado
        </h2>
        <p className="caixa-lead">Caixa fechado. Abra o caixa para receber.</p>
        {permissoes.pode("caixa:abrir") ? (
          <form
            className="caixa-card"
            onSubmit={(e) => {
              e.preventDefault();
              void abrir();
            }}
          >
            <MoneyField
              label="Fundo de troco"
              hint="É o dinheiro que já está na gaveta para dar troco. Não é venda."
              digits={fundo}
              onDigitsChange={setFundo}
              disabled={abrindo}
            />
            {erroAbrir ? (
              <Notice
                tone="error"
                action={
                  erroAbrir.atualizar ? (
                    <Button
                      variant="secondary"
                      onClick={() => void queryClient.invalidateQueries({ queryKey: ["caixa"] })}
                    >
                      Atualizar
                    </Button>
                  ) : undefined
                }
              >
                {erroAbrir.texto}
              </Notice>
            ) : null}
            <Button type="submit" loading={abrindo} disabled={fundo === ""} className="entry-submit">
              Abrir caixa
            </Button>
            {fundo === "" ? <p className="f4-field-hint">Informe o fundo (pode ser R$ 0,00).</p> : null}
          </form>
        ) : null}
      </section>
    );
  }

  const turno = leitura.data.turno;
  const esperado = turno.fundoCentavos + turno.recebidoDinheiroCentavos;
  return (
    <section className="caixa" aria-labelledby="caixa-titulo">
      <h2 id="caixa-titulo" className="app-empty-title">
        Caixa aberto
      </h2>
      <p className="caixa-lead">Aberto às {hora(turno.abertoEm)}</p>
      <dl className="caixa-card caixa-valores">
        <div>
          <dt>Fundo de troco</dt>
          <dd className="f4-num">{formatBRL(turno.fundoCentavos)}</dd>
        </div>
        <div>
          <dt>Recebido em dinheiro</dt>
          <dd className="f4-num">{formatBRL(turno.recebidoDinheiroCentavos)}</dd>
        </div>
        <div className="caixa-total">
          <dt>Esperado na gaveta</dt>
          <dd className="f4-num">{formatBRL(esperado)}</dd>
        </div>
      </dl>
      <p className="f4-field-hint">O fundo não é venda: ele só entra na conta do esperado.</p>
      {permissoes.pode("caixa:fechar") ? (
        <Button className="entry-submit" onClick={() => setFechando(true)}>
          Fechar caixa
        </Button>
      ) : null}

      <BottomSheet
        open={fechando}
        onOpenChange={(aberto) => {
          if (!aberto) {
            void concluir();
          }
        }}
        title={resultado ? "Caixa fechado" : "Fechar caixa"}
        description={resultado ? undefined : "Conte o dinheiro da gaveta e informe o total. Pode ser R$ 0,00."}
        error={erroFechar ?? undefined}
        loading={enviando}
        footer={
          resultado ? (
            <Button onClick={() => void concluir()}>Concluir</Button>
          ) : (
            <>
              <Button variant="secondary" onClick={() => setFechando(false)} disabled={enviando}>
                Voltar
              </Button>
              <Button loading={enviando} disabled={contagem === ""} onClick={() => void fechar()}>
                Fechar caixa
              </Button>
            </>
          )
        }
      >
        {resultado ? (
          <div className="caixa-resultado">
            <p className="caixa-resultado-linha">
              Esperado <strong className="f4-num">{formatBRL(resultado.esperadoCentavos ?? 0)}</strong>
            </p>
            <p className="caixa-resultado-linha">
              Contado <strong className="f4-num">{formatBRL(resultado.contadoCentavos ?? 0)}</strong>
            </p>
            <ToneBadge tone={tomDaDiferenca(resultado.diferencaCentavos ?? 0)}>
              {resultado.diferencaTexto ?? "Confere"}
            </ToneBadge>
          </div>
        ) : (
          <MoneyField label="Dinheiro contado" digits={contagem} onDigitsChange={setContagem} disabled={enviando} />
        )}
      </BottomSheet>
    </section>
  );
}
