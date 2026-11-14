import { Button, centsFromDigits, formatBRL, MoneyField, Notice, Skeleton } from "@flow4tech/design-system";
import { useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { usePermissoes } from "../sessao/permissoes";
import { api } from "../sessao/sessao";
import "./caixa.css";
import { useLeituraDoCaixa, useMovimentos } from "./dados";
import { FecharCaixaSheet } from "./FecharCaixaSheet";
import { esperadoDaGaveta, hora, type TipoDeMovimento } from "./logica";
import { MovimentoSheet } from "./MovimentoSheet";
import { Movimentos } from "./Movimentos";
import { Pendentes } from "./Pendentes";

export function CaixaPage() {
  const queryClient = useQueryClient();
  const permissoes = usePermissoes();
  const [fundo, setFundo] = useState("");
  const [abrindo, setAbrindo] = useState(false);
  const [erroAbrir, setErroAbrir] = useState<{ texto: string; atualizar: boolean } | null>(null);
  const [fechando, setFechando] = useState(false);
  const [movimento, setMovimento] = useState<TipoDeMovimento | null>(null);
  const [aviso, setAviso] = useState<string | null>(null);

  const leitura = useLeituraDoCaixa();
  const aberto = leitura.data?.tipo === "turno";
  const movimentos = useMovimentos(aberto);
  const atualizar = () => queryClient.invalidateQueries({ queryKey: ["caixa"] });

  async function abrir() {
    setErroAbrir(null);
    setAbrindo(true);
    try {
      const { data, response } = await api.POST("/caixa/turnos", { body: { fundoCentavos: centsFromDigits(fundo) } });
      if (data) {
        setFundo("");
        await atualizar();
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

  // Managers see the differences waiting for a decision whatever the state of their own shift.
  const pendentes = permissoes.pode("caixa:ver-todos") ? <Pendentes /> : null;

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
                    <Button variant="secondary" onClick={() => void atualizar()}>
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
        {pendentes}
      </section>
    );
  }

  const resumo = leitura.data.resumo;
  const turno = resumo.turno;
  const esperado = esperadoDaGaveta(resumo);
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
        {esperado === null ? null : (
          <div>
            <dt>Recebido em dinheiro</dt>
            <dd className="f4-num">{formatBRL(turno.recebidoDinheiroCentavos)}</dd>
          </div>
        )}
        <div>
          <dt>Suprimentos</dt>
          <dd className="f4-num">{formatBRL(resumo.suprimentosCentavos)}</dd>
        </div>
        <div>
          <dt>Sangrias</dt>
          <dd className="f4-num">{formatBRL(resumo.sangriasCentavos)}</dd>
        </div>
        <div className="caixa-total">
          <dt>Esperado na gaveta</dt>
          <dd className="f4-num">{esperado === null ? "Oculto até o fechamento" : formatBRL(esperado)}</dd>
        </div>
      </dl>
      <p className="f4-field-hint">
        {esperado === null
          ? "Conferência cega: o valor esperado aparece depois que você enviar a contagem."
          : "O fundo não é venda: ele só entra na conta do esperado."}
      </p>
      {aviso ? (
        <Notice tone="info">
          {aviso}{" "}
          <Button variant="secondary" onClick={() => setAviso(null)}>
            Ok
          </Button>
        </Notice>
      ) : null}
      <div className="caixa-acoes">
        <Button variant="secondary" onClick={() => setMovimento("suprimento")}>
          Suprimento
        </Button>
        <Button variant="secondary" onClick={() => setMovimento("sangria")}>
          Sangria
        </Button>
      </div>
      {permissoes.pode("caixa:fechar") ? (
        <Button className="entry-submit" onClick={() => setFechando(true)}>
          Fechar caixa
        </Button>
      ) : null}
      <Movimentos consulta={movimentos} />
      {pendentes}

      <MovimentoSheet
        tipo={movimento ?? "suprimento"}
        open={movimento !== null}
        onOpenChange={(aberta) => {
          if (!aberta) {
            setMovimento(null);
          }
        }}
        onAtualizar={atualizar}
        onFeito={(mensagem) => {
          setMovimento(null);
          setAviso(mensagem);
        }}
      />
      <FecharCaixaSheet
        resumo={resumo}
        open={fechando}
        onClose={() => {
          setFechando(false);
          void atualizar();
        }}
      />
    </section>
  );
}
