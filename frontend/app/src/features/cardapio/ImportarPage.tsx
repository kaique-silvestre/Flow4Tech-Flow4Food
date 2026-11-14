import { Button, Notice, ToneBadge } from "@flow4tech/design-system";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useRef, useState } from "react";
import { usePermissoes } from "../sessao/permissoes";
import {
  type Aplicacao,
  type Arquivo,
  agruparPorLinha,
  aplicar,
  carregarModelo,
  type Falha,
  lerArquivo,
  linhasDoResumo,
  type Previa,
  pedirPrevia,
  type Resumo,
  SEM_PERMISSAO,
  semMudanca,
} from "./importacao";
import "./importacao.css";

type Passo =
  | { passo: "escolher" }
  | { passo: "conferindo"; arquivo: Arquivo }
  | { passo: "previa"; arquivo: Arquivo; previa: Previa }
  | { passo: "aplicando"; arquivo: Arquivo; previa: Previa }
  | { passo: "feito"; jaAplicada: boolean; resumo: Resumo };

function Esqueleto() {
  return (
    <section role="status" aria-label="Carregando a importação" aria-busy="true">
      <div className="f4-skeleton gestao-esq-titulo" aria-hidden="true" />
      <div className="f4-skeleton gestao-esq-busca" aria-hidden="true" />
    </section>
  );
}

function Contagens({ resumo }: { resumo: Resumo }) {
  return (
    <ul className="imp-contagens" aria-label="O que a planilha faz">
      {linhasDoResumo(resumo).map((l) => (
        <li key={l.rotulo} className="imp-contagem">
          <span>{l.rotulo}</span>
          <span className="f4-num">
            {l.criados} novos · {l.atualizados} atualizados
          </span>
        </li>
      ))}
    </ul>
  );
}

function Erros({ previa }: { previa: Previa }) {
  const grupos = agruparPorLinha(previa.erros);
  return (
    <section aria-labelledby="imp-erros-titulo">
      <h3 id="imp-erros-titulo" className="imp-subtitulo">
        {previa.erros.length === 1 ? "1 erro para corrigir" : `${previa.erros.length} erros para corrigir`}
      </h3>
      <p className="gestao-lead">Corrija a planilha e escolha o arquivo de novo. Nada foi importado.</p>
      <ol className="imp-erros">
        {grupos.map((g) => (
          <li key={g.linha} className="imp-erro">
            <strong className="imp-erro-linha">Linha {g.linha}</strong>
            <ul>
              {g.erros.map((e) => (
                <li key={`${e.coluna}-${e.mensagem}`}>
                  {e.coluna ? <span className="imp-erro-coluna">{e.coluna}: </span> : null}
                  {e.mensagem}
                </li>
              ))}
            </ul>
          </li>
        ))}
      </ol>
    </section>
  );
}

/** Imports the menu from a spreadsheet on the phone: template, file, line-by-line preview, then apply. The server decides; the screen shows its answer. */
export function ImportarPage() {
  const queryClient = useQueryClient();
  const permissoes = usePermissoes();
  const modelo = useQuery({ queryKey: ["cardapio", "importacao", "modelo"], queryFn: carregarModelo, retry: false });
  const [estado, setEstado] = useState<Passo>({ passo: "escolher" });
  const [falha, setFalha] = useState<Falha | null>(null);
  const [recusa, setRecusa] = useState<string | null>(null);
  const entrada = useRef<HTMLInputElement>(null);

  if (!permissoes.carregado || modelo.isPending) {
    return <Esqueleto />;
  }

  if (!permissoes.pode("cardapio:importar") || (modelo.error instanceof Error && modelo.error.message === "403")) {
    return <Notice tone="info">{SEM_PERMISSAO}</Notice>;
  }

  if (modelo.isError) {
    return (
      <Notice
        tone="error"
        action={
          <Button variant="secondary" onClick={() => void modelo.refetch()}>
            Tentar novamente
          </Button>
        }
      >
        Não foi possível carregar a importação.
      </Notice>
    );
  }

  const { exemploCsv, bytesMaximos, linhasMaximas } = modelo.data;

  function baixarModelo() {
    const url = URL.createObjectURL(new Blob([`﻿${exemploCsv}`], { type: "text/csv;charset=utf-8" }));
    const a = document.createElement("a");
    a.href = url;
    a.download = "modelo-cardapio.csv";
    a.click();
    URL.revokeObjectURL(url);
  }

  async function conferir(arquivo: Arquivo) {
    setFalha(null);
    setEstado({ passo: "conferindo", arquivo });
    const r = await pedirPrevia(arquivo);
    if (r.tipo === "previa") {
      setEstado({ passo: "previa", arquivo, previa: r.previa });
      return;
    }

    setEstado(r.refazer || r.semPermissao ? { passo: "escolher" } : { passo: "conferindo", arquivo });
    setFalha(r);
  }

  async function escolher(file: File | undefined) {
    if (!file) {
      return;
    }

    setRecusa(null);
    setFalha(null);
    const lido = await lerArquivo(file, bytesMaximos);
    if (typeof lido === "string") {
      setRecusa(lido);
      setEstado({ passo: "escolher" });
      return;
    }

    await conferir(lido);
  }

  async function confirmar(arquivo: Arquivo, previa: Previa) {
    setFalha(null);
    setEstado({ passo: "aplicando", arquivo, previa });
    const r: Aplicacao = await aplicar(arquivo);
    if (r.tipo === "aplicada") {
      await queryClient.invalidateQueries({ queryKey: ["cardapio"] });
      setEstado({ passo: "feito", jaAplicada: r.jaAplicada, resumo: r.resumo });
      return;
    }

    if (r.tipo === "invalida") {
      setEstado({ passo: "previa", arquivo, previa: r.previa });
      return;
    }

    // A resend keeps the same file and the same importacaoId; only a refusal that needs another file goes back to choosing.
    setEstado(r.refazer || r.semPermissao ? { passo: "escolher" } : { passo: "previa", arquivo, previa });
    setFalha(r);
  }

  const ocupado = (estado.passo === "conferindo" && !falha) || estado.passo === "aplicando";

  if (estado.passo === "feito") {
    const nada = estado.jaAplicada || semMudanca(estado.resumo);
    return (
      <section aria-labelledby="imp-titulo">
        <h2 id="imp-titulo" className="app-empty-title">
          {nada ? "Nada para mudar" : "Cardápio importado"}
        </h2>
        <p className="gestao-lead" role="status">
          {estado.jaAplicada
            ? "Esta planilha já tinha sido importada. Nada foi alterado."
            : nada
              ? "O cardápio já está igual à planilha."
              : "As mudanças já estão valendo."}
        </p>
        {nada ? null : <Contagens resumo={estado.resumo} />}
        <div className="imp-acoes">
          <Link to="/salao/cardapio" className="f4-btn imp-link" data-variant="primary">
            Ver o cardápio
          </Link>
          <Button variant="secondary" onClick={() => setEstado({ passo: "escolher" })}>
            Importar outra planilha
          </Button>
        </div>
      </section>
    );
  }

  return (
    <section aria-labelledby="imp-titulo">
      <p className="imp-voltar">
        <Link to="/salao/cardapio">‹ Cardápio</Link>
      </p>
      <h2 id="imp-titulo" className="app-empty-title">
        Importar cardápio
      </h2>
      <p className="gestao-lead">
        Envie uma planilha CSV ou XLSX (até {Math.round(bytesMaximos / 1024 / 1024)} MB e {linhasMaximas} linhas). Você
        confere tudo antes de valer. A planilha nunca apaga nada.
      </p>

      <div className="imp-acoes">
        <Button variant="secondary" onClick={baixarModelo}>
          Baixar modelo
        </Button>
        <input
          ref={entrada}
          id="imp-arquivo"
          className="f4-visually-hidden"
          type="file"
          accept=".csv,.xlsx,text/csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
          tabIndex={-1}
          aria-label="Arquivo da planilha"
          onChange={(e) => {
            const file = e.target.files?.[0];
            e.target.value = "";
            void escolher(file);
          }}
        />
        <Button
          variant={estado.passo === "escolher" ? "primary" : "secondary"}
          disabled={ocupado}
          onClick={() => entrada.current?.click()}
        >
          {estado.passo === "escolher" ? "Escolher arquivo" : "Escolher outro arquivo"}
        </Button>
      </div>

      {recusa ? (
        <p role="alert" className="f4-field-error">
          {recusa}
        </p>
      ) : null}

      {estado.passo === "conferindo" && !falha ? (
        <p role="status" className="imp-progresso">
          <span className="f4-spinner" aria-hidden="true" /> Conferindo {estado.arquivo.nome}…
        </p>
      ) : null}

      {falha && estado.passo !== "previa" ? (
        <Notice
          tone={falha.semPermissao ? "info" : "error"}
          action={
            falha.offline && estado.passo === "conferindo" ? (
              <Button variant="secondary" onClick={() => void conferir(estado.arquivo)}>
                Tentar de novo
              </Button>
            ) : undefined
          }
        >
          {falha.mensagem}
        </Notice>
      ) : null}

      {estado.passo === "previa" || estado.passo === "aplicando" ? (
        <section aria-labelledby="imp-previa" className="imp-previa">
          <h3 id="imp-previa" className="imp-subtitulo">
            {estado.arquivo.nome}{" "}
            <ToneBadge tone={estado.previa.valida ? "success" : "warning"}>
              {estado.previa.valida ? "Pronta" : "Com erros"}
            </ToneBadge>
          </h3>
          {estado.previa.valida ? (
            <>
              {semMudanca(estado.previa.resumo) ? (
                <p className="gestao-lead">Nada para mudar: o cardápio já está igual à planilha.</p>
              ) : (
                <Contagens resumo={estado.previa.resumo} />
              )}
              <Button
                loading={estado.passo === "aplicando"}
                error={falha?.mensagem}
                onClick={() => void confirmar(estado.arquivo, estado.previa)}
              >
                {falha?.offline ? "Tentar de novo" : "Confirmar importação"}
              </Button>
            </>
          ) : (
            <Erros previa={estado.previa} />
          )}
        </section>
      ) : null}
    </section>
  );
}
