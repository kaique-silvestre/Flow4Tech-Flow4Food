import { Button, Notice, TextField } from "@flow4tech/design-system";
import { zodResolver } from "@hookform/resolvers/zod";
import { useNavigate } from "@tanstack/react-router";
import { useEffect, useState } from "react";
import { useForm } from "react-hook-form";
import {
  destinoDaSessao,
  entrar,
  lerSessao,
  limparAvisoDeDescarte,
  type ResultadoDeEntrada,
  useDescartadas,
} from "../sessao/sessao";
import { type EntrarValores, ehEmail, entrarSchema, organizacaoValida } from "./schema";

const CHAVE_ORGANIZACAO = "f4.organizacao";

function lerOrganizacaoSalva(): string {
  try {
    return window.localStorage.getItem(CHAVE_ORGANIZACAO) ?? "";
  } catch {
    return "";
  }
}

function salvarOrganizacao(valor: string) {
  try {
    window.localStorage.setItem(CHAVE_ORGANIZACAO, valor);
  } catch {
    // Private mode: the person types it next time.
  }
}

function formatarEspera(segundos: number): string {
  const m = Math.floor(segundos / 60);
  const s = segundos % 60;
  return `${m}:${s.toString().padStart(2, "0")}`;
}

export function EntrarPage() {
  const navigate = useNavigate();
  const [mostrar, setMostrar] = useState(false);
  const [enviando, setEnviando] = useState(false);
  const [resultado, setResultado] = useState<Exclude<ResultadoDeEntrada, { tipo: "ok" }> | null>(null);
  const [espera, setEspera] = useState(0);
  const form = useForm<EntrarValores>({
    resolver: zodResolver(entrarSchema),
    defaultValues: { identificador: "", senha: "", organizacao: lerOrganizacaoSalva() },
    mode: "onSubmit",
  });
  const descartadas = useDescartadas();
  const identificador = form.watch("identificador");
  const precisaOrganizacao = identificador.trim() !== "" && !ehEmail(identificador);

  // The wait of a blocked login counts down on screen; the button comes back when it reaches zero.
  useEffect(() => {
    if (espera <= 0) {
      return undefined;
    }

    const timer = window.setTimeout(() => setEspera((atual) => atual - 1), 1000);
    return () => window.clearTimeout(timer);
  }, [espera]);

  async function aoEnviar(valores: EntrarValores) {
    setResultado(null);
    const email = ehEmail(valores.identificador);
    if (!email && !organizacaoValida(valores.organizacao)) {
      // Same answer as a wrong user: a malformed code must not be told apart from a wrong one.
      setResultado({ tipo: "invalido" });
      return;
    }

    setEnviando(true);
    const r = await entrar(
      email
        ? { email: valores.identificador.trim(), senha: valores.senha }
        : {
            organizacaoDoLogin: valores.organizacao.trim(),
            usuario: valores.identificador.trim(),
            senha: valores.senha,
          },
    );
    setEnviando(false);
    if (r.tipo === "ok") {
      limparAvisoDeDescarte();
      if (!email) {
        salvarOrganizacao(valores.organizacao.trim());
      }

      await navigate({ to: destinoDaSessao(lerSessao()) });
      return;
    }

    setResultado(r);
    if (r.tipo === "bloqueado") {
      setEspera(r.esperaSegundos);
    }

    form.resetField("senha");
  }

  const bloqueado = espera > 0;
  return (
    <form className="entry-form" onSubmit={form.handleSubmit(aoEnviar)} noValidate>
      <h1 className="f4-title">Entrar</h1>
      <p className="entry-lead">Use o usuário que o responsável criou para você, ou o seu e-mail.</p>

      {descartadas > 0 ? (
        <Notice tone="warning" title="Sessão encerrada">
          {descartadas === 1
            ? "1 ação não enviada foi descartada."
            : `${descartadas} ações não enviadas foram descartadas.`}
        </Notice>
      ) : null}
      {resultado?.tipo === "invalido" ? <Notice tone="error">Usuário ou senha inválidos.</Notice> : null}
      {resultado?.tipo === "erro" ? (
        <Notice tone="error">Sem conexão ou servidor fora do ar. Tente de novo.</Notice>
      ) : null}
      {resultado?.tipo === "captcha" ? (
        <Notice tone="info" title="Verificação necessária">
          Por segurança, peça ao responsável para liberar o acesso e tente de novo.
        </Notice>
      ) : null}
      {bloqueado ? (
        <Notice tone="warning" title="Muitas tentativas">
          Tente de novo em {formatarEspera(espera)}.
        </Notice>
      ) : null}

      <TextField
        label="Usuário ou e-mail"
        autoComplete="username"
        autoCapitalize="none"
        autoCorrect="off"
        spellCheck={false}
        inputMode="email"
        disabled={bloqueado}
        error={form.formState.errors.identificador?.message}
        {...form.register("identificador")}
      />
      {precisaOrganizacao ? (
        <TextField
          label="Código da organização"
          hint="Informado pelo responsável. Fica salvo neste aparelho."
          autoComplete="organization"
          autoCapitalize="none"
          spellCheck={false}
          disabled={bloqueado}
          error={form.formState.errors.organizacao?.message}
          {...form.register("organizacao")}
        />
      ) : null}
      <TextField
        label="Senha"
        type={mostrar ? "text" : "password"}
        autoComplete="current-password"
        disabled={bloqueado}
        error={form.formState.errors.senha?.message}
        trailing={
          <button type="button" aria-pressed={mostrar} onClick={() => setMostrar((v) => !v)}>
            {mostrar ? "Ocultar" : "Mostrar"}
          </button>
        }
        {...form.register("senha")}
      />

      <Button type="submit" loading={enviando} disabled={bloqueado} className="entry-submit">
        Entrar
      </Button>
    </form>
  );
}
