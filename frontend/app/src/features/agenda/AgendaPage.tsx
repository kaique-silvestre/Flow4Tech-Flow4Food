import { Button, Icon, Notice, Switch, TextField, ToneBadge } from "@flow4tech/design-system";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { type FormEvent, useState } from "react";
import { usePermissoes } from "../sessao/permissoes";
import { api } from "../sessao/sessao";
import "./agenda.css";
import {
  type AgendaDto,
  dataBr,
  type ExcecaoDto,
  type Janela,
  type LinhaDaSemana,
  type LinhaDeExcecao,
  mensagemDeErro,
  montarExcecoes,
  montarSemana,
  paraExcecoes,
  paraSemana,
  textoDaJanela,
  validarExcecao,
  validarSemana,
} from "./logica";

const SEM_PERMISSAO = "Você não tem permissão para isso.";
const MAXIMO_DE_EXCECOES = 10;

/** A refusal by right is a bare 403: the screen says "sem permissão" and never why. */
class Recusado extends Error {}

async function carregar(): Promise<AgendaDto> {
  const { data, response } = await api.GET("/agenda");
  if (response.status === 403) {
    throw new Recusado();
  }

  if (!data) {
    throw new Error("agenda");
  }

  return data as unknown as AgendaDto;
}

interface Aviso {
  tom: "info" | "error";
  texto: string;
}

interface Resposta {
  response: Response;
  error?: { codigo: string } | undefined;
}

/** Sends one change and returns what the owner reads: a confirmation, or the message of the refusal (the server decides; this only words it). */
async function enviar(chamada: () => Promise<Resposta>, ok: string, virada: string): Promise<Aviso> {
  try {
    const { response, error } = await chamada();
    return response.ok
      ? { tom: "info", texto: ok }
      : { tom: "error", texto: mensagemDeErro(response.status, error?.codigo, virada) };
  } catch {
    return { tom: "error", texto: "Sem conexão. Tente de novo." };
  }
}

function AvisoDaSecao({ aviso }: { aviso: Aviso | null }) {
  return aviso ? <Notice tone={aviso.tom}>{aviso.texto}</Notice> : null;
}

function Esqueleto() {
  return (
    <section role="status" aria-label="Carregando a agenda" aria-busy="true">
      {[0, 1, 2, 3].map((i) => (
        <div key={i} className="f4-skeleton agenda-esqueleto" aria-hidden="true" />
      ))}
    </section>
  );
}

function Hoje({ dados }: { dados: AgendaDto }) {
  const queryClient = useQueryClient();
  const [aberto, setAberto] = useState(false);
  const [fecha, setFecha] = useState("");
  const [motivo, setMotivo] = useState("");
  const [erroDeFormato, setErroDeFormato] = useState<string | undefined>();
  const [aviso, setAviso] = useState<Aviso | null>(null);
  const [enviando, setEnviando] = useState(false);

  async function estender(e: FormEvent) {
    e.preventDefault();
    if (!fecha) {
      setErroDeFormato("Informe o novo horário de fechamento.");
      return;
    }

    setErroDeFormato(undefined);
    setEnviando(true);
    const resultado = await enviar(
      () => api.POST("/agenda/estender-hoje", { body: { fecha, motivo: motivo.trim() || null } }),
      "Hoje estendido.",
      dados.virada,
    );
    setEnviando(false);
    setAviso(resultado);
    if (resultado.tom === "info") {
      setAberto(false);
      setFecha("");
      setMotivo("");
      await queryClient.invalidateQueries({ queryKey: ["agenda"] });
    }
  }

  return (
    <section className="agenda-secao" aria-labelledby="agenda-hoje">
      <h2 id="agenda-hoje" className="agenda-subtitulo">
        Hoje, {dataBr(dados.diaOperacional)}
      </h2>
      <p className="agenda-linha">
        {dados.abertoAgora ? (
          <ToneBadge tone="success">Aberto agora</ToneBadge>
        ) : (
          <ToneBadge tone="alert">Fora do horário agora</ToneBadge>
        )}
        <span>
          {dados.janelaDeHoje ? `Funciona ${textoDaJanela(dados.janelaDeHoje)}` : "Fechado hoje"}
          {dados.estendidoAte ? ` · Estendido até ${dados.estendidoAte}` : ""}
        </span>
      </p>
      <p className="agenda-nota">
        {dados.abertoAgora
          ? "Dentro do horário, o salão lança normalmente."
          : "Fora do horário, abrir comanda e lançar rodada serão recusados pelo servidor. Fechar a conta continua liberado."}
      </p>
      {aberto ? (
        <form className="agenda-form" onSubmit={(e) => void estender(e)} noValidate>
          <TextField
            label="Novo fechamento"
            type="time"
            value={fecha}
            onChange={(e) => setFecha(e.target.value)}
            error={erroDeFormato}
            hint="Só vale hoje, até a virada do dia."
          />
          <TextField
            label="Motivo (opcional)"
            value={motivo}
            maxLength={200}
            onChange={(e) => setMotivo(e.target.value)}
            autoComplete="off"
          />
          <div className="agenda-acoes">
            <Button type="submit" loading={enviando}>
              Estender
            </Button>
            <Button variant="secondary" onClick={() => setAberto(false)}>
              Cancelar
            </Button>
          </div>
        </form>
      ) : (
        <div className="agenda-acoes">
          <Button variant="secondary" onClick={() => setAberto(true)}>
            <Icon name="clock" /> Estender hoje
          </Button>
        </div>
      )}
      <AvisoDaSecao aviso={aviso} />
    </section>
  );
}

function Semana({ dados }: { dados: AgendaDto }) {
  const queryClient = useQueryClient();
  const [linhas, setLinhas] = useState<LinhaDaSemana[]>(() => montarSemana(dados.semana));
  const [erros, setErros] = useState<Record<number, string>>({});
  const [aviso, setAviso] = useState<Aviso | null>(null);
  const [enviando, setEnviando] = useState(false);

  const mudar = (i: number, parte: Partial<LinhaDaSemana>) =>
    setLinhas((atual) => atual.map((l, j) => (j === i ? { ...l, ...parte } : l)));

  async function salvar() {
    const achados = validarSemana(linhas);
    setErros(achados);
    if (Object.keys(achados).length > 0) {
      setAviso({ tom: "error", texto: "Corrija os horários marcados." });
      return;
    }

    setEnviando(true);
    const resultado = await enviar(
      () => api.PUT("/agenda/semana", { body: { semana: paraSemana(linhas) as Janela[], motivo: null } }),
      "Semana salva.",
      dados.virada,
    );
    setEnviando(false);
    setAviso(resultado);
    if (resultado.tom === "info") {
      await queryClient.invalidateQueries({ queryKey: ["agenda"] });
    }
  }

  return (
    <section className="agenda-secao" aria-labelledby="agenda-semana">
      <h2 id="agenda-semana" className="agenda-subtitulo">
        Semana padrão
      </h2>
      <p className="agenda-nota">
        Abertura e fechamento de cada dia. Fechar depois da meia-noite vale como o mesmo dia de trabalho, até a virada
        das {dados.virada}. Abertura e fechamento iguais significam o dia todo.
      </p>
      <ul className="agenda-lista" aria-label="Dias da semana">
        {linhas.map((l, i) => (
          <li key={l.dia} className="agenda-dia">
            <b className="agenda-dia-nome">{l.dia}</b>
            <Switch
              label={l.aberto ? "Aberto" : "Fechado"}
              aria-label={`${l.dia}: ${l.aberto ? "aberto" : "fechado"}`}
              checked={l.aberto}
              onCheckedChange={(aberto) => mudar(i, { aberto })}
            />
            {l.aberto ? (
              <div className="agenda-horas">
                <TextField
                  label="Abre"
                  aria-label={`${l.dia}: abre`}
                  type="time"
                  value={l.abre}
                  onChange={(e) => mudar(i, { abre: e.target.value })}
                  error={erros[i]}
                />
                <TextField
                  label="Fecha"
                  aria-label={`${l.dia}: fecha`}
                  type="time"
                  value={l.fecha}
                  onChange={(e) => mudar(i, { fecha: e.target.value })}
                />
              </div>
            ) : null}
          </li>
        ))}
      </ul>
      <div className="agenda-acoes">
        <Button onClick={() => void salvar()} loading={enviando}>
          Salvar semana
        </Button>
      </div>
      <AvisoDaSecao aviso={aviso} />
    </section>
  );
}

const NOVA: LinhaDeExcecao = { dia: "", fechado: true, abre: "", fecha: "" };

function Excecoes({ dados }: { dados: AgendaDto }) {
  const queryClient = useQueryClient();
  const existentes = montarExcecoes(dados.excecoes);
  const [nova, setNova] = useState<LinhaDeExcecao>(NOVA);
  const [motivo, setMotivo] = useState("");
  const [erroDeFormato, setErroDeFormato] = useState<string | null>(null);
  const [aviso, setAviso] = useState<Aviso | null>(null);
  const [enviando, setEnviando] = useState(false);
  const cheio = existentes.length >= MAXIMO_DE_EXCECOES;

  async function gravar(lista: ExcecaoDto[], ok: string, motivoDoEnvio: string | null) {
    setEnviando(true);
    const resultado = await enviar(
      () => api.PUT("/agenda/excecoes", { body: { excecoes: lista, motivo: motivoDoEnvio } }),
      ok,
      dados.virada,
    );
    setEnviando(false);
    setAviso(resultado);
    if (resultado.tom === "info") {
      await queryClient.invalidateQueries({ queryKey: ["agenda"] });
    }

    return resultado.tom === "info";
  }

  async function adicionar(e: FormEvent) {
    e.preventDefault();
    const erro = validarExcecao(nova);
    setErroDeFormato(erro);
    if (erro) {
      return;
    }

    // A second exception on the same date replaces the first (one rule per day).
    const lista = paraExcecoes([...existentes.filter((x) => x.dia !== nova.dia), nova]);
    if (await gravar(lista, "Exceção salva.", motivo.trim() || null)) {
      setNova(NOVA);
      setMotivo("");
    }
  }

  return (
    <section className="agenda-secao" aria-labelledby="agenda-excecoes">
      <h2 id="agenda-excecoes" className="agenda-subtitulo">
        Exceções por data
      </h2>
      <p className="agenda-nota">
        Feriados e horários especiais valem só na data e vencem a semana padrão. Datas que já passaram saem sozinhas ao
        salvar. {existentes.length} de {MAXIMO_DE_EXCECOES} exceções.
      </p>
      {existentes.length > 0 ? (
        <ul className="agenda-lista" aria-label="Exceções cadastradas">
          {existentes.map((x) => (
            <li key={x.dia} className="agenda-excecao">
              <b>{dataBr(x.dia)}</b>
              <span>{textoDaJanela(x.fechado ? null : { abre: x.abre, fecha: x.fecha })}</span>
              <Button
                variant="secondary"
                aria-label={`Remover exceção de ${dataBr(x.dia).split(" · ")[0]}`}
                disabled={enviando}
                onClick={() =>
                  void gravar(paraExcecoes(existentes.filter((y) => y.dia !== x.dia)), "Exceção removida.", null)
                }
              >
                Remover
              </Button>
            </li>
          ))}
        </ul>
      ) : (
        <p className="agenda-nota">Nenhuma exceção cadastrada.</p>
      )}
      <form className="agenda-form" onSubmit={(e) => void adicionar(e)} noValidate>
        <TextField
          label="Data"
          type="date"
          value={nova.dia}
          onChange={(e) => setNova({ ...nova, dia: e.target.value })}
          error={erroDeFormato === "Escolha a data." ? erroDeFormato : undefined}
        />
        <Switch
          label="Fechado nesse dia"
          checked={nova.fechado}
          onCheckedChange={(fechado) => setNova({ ...nova, fechado })}
        />
        {nova.fechado ? null : (
          <div className="agenda-horas">
            <TextField
              label="Abre às"
              type="time"
              value={nova.abre}
              onChange={(e) => setNova({ ...nova, abre: e.target.value })}
              error={erroDeFormato && erroDeFormato !== "Escolha a data." ? erroDeFormato : undefined}
            />
            <TextField
              label="Fecha às"
              type="time"
              value={nova.fecha}
              onChange={(e) => setNova({ ...nova, fecha: e.target.value })}
            />
          </div>
        )}
        <TextField
          label="Motivo (opcional)"
          value={motivo}
          maxLength={200}
          onChange={(e) => setMotivo(e.target.value)}
          autoComplete="off"
          hint="Fica no histórico da alteração."
        />
        <div className="agenda-acoes">
          <Button type="submit" loading={enviando} disabled={cheio}>
            Adicionar exceção
          </Button>
        </div>
        {cheio ? <p className="agenda-nota">Limite atingido: remova uma exceção para incluir outra.</p> : null}
      </form>
      <AvisoDaSecao aviso={aviso} />
    </section>
  );
}

/**
 * Agenda de funcionamento at /gestao/agenda: the weekly default, the exceptions by date and "estender hoje" of the active unit. The server decides what is allowed and when a launch is blocked
 * (409 fora-do-horario); this screen only collects the times and words the answers.
 * Visual reference: .scratch/S-00-novo-sistema/prototipos/index.html#m=web&screen=configuracoes&ctab=op (section "Horário de funcionamento", cfAgendaHtml).
 */
export function AgendaPage() {
  const permissoes = usePermissoes();
  const pode = permissoes.carregado && permissoes.pode("organizacao:configurar");
  const consulta = useQuery({ queryKey: ["agenda"], queryFn: carregar, enabled: pode });

  if (!permissoes.carregado || (pode && consulta.isPending)) {
    return <Esqueleto />;
  }

  if (!pode || consulta.error instanceof Recusado) {
    return <Notice tone="info">{SEM_PERMISSAO}</Notice>;
  }

  if (consulta.isError) {
    return (
      <Notice
        tone="error"
        action={
          <Button variant="secondary" onClick={() => void consulta.refetch()}>
            Tentar novamente
          </Button>
        }
      >
        Não foi possível carregar a agenda.
      </Notice>
    );
  }

  const dados = consulta.data;
  if (!dados) {
    return <Esqueleto />;
  }

  return (
    <section className="agenda" aria-labelledby="agenda-titulo">
      <h1 id="agenda-titulo" className="agenda-titulo">
        Agenda de funcionamento
      </h1>
      <p className="agenda-nota">
        Fuso {dados.fuso}. O dia de trabalho vira às {dados.virada}.
      </p>
      <Hoje dados={dados} />
      <Semana dados={dados} />
      <Excecoes dados={dados} />
    </section>
  );
}
