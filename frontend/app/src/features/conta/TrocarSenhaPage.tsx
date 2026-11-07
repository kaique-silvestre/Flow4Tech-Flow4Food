import { Button, Notice, TextField } from "@flow4tech/design-system";
import { zodResolver } from "@hookform/resolvers/zod";
import { useNavigate } from "@tanstack/react-router";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { aceitarSessao, api, destinoDaSessao, lerSessao, sair } from "../sessao/sessao";

export const SENHA_MINIMO = 10;
export const SENHA_MAXIMO = 256;

export const trocarSenhaSchema = z
  .object({
    senhaAtual: z.string().min(1, "Informe a senha provisória."),
    novaSenha: z
      .string()
      .min(SENHA_MINIMO, `Use pelo menos ${SENHA_MINIMO} caracteres.`)
      .max(SENHA_MAXIMO, `Use no máximo ${SENHA_MAXIMO} caracteres.`),
    confirmacao: z.string().min(1, "Repita a nova senha."),
  })
  .refine((v) => v.novaSenha === v.confirmacao, { path: ["confirmacao"], message: "As senhas não são iguais." });

type Valores = z.infer<typeof trocarSenhaSchema>;

const PROBLEMAS: Record<string, string> = {
  Curta: `Use pelo menos ${SENHA_MINIMO} caracteres.`,
  Longa: `Use no máximo ${SENHA_MAXIMO} caracteres.`,
  IgualAoIdentificador: "A senha não pode ser igual ao seu usuário.",
  IgualASenhaAtual: "A nova senha precisa ser diferente da provisória.",
  Vazia: "Informe a nova senha.",
};

export function TrocarSenhaPage() {
  const navigate = useNavigate();
  const [enviando, setEnviando] = useState(false);
  const [aviso, setAviso] = useState<string | null>(null);
  const form = useForm<Valores>({
    resolver: zodResolver(trocarSenhaSchema),
    defaultValues: { senhaAtual: "", novaSenha: "", confirmacao: "" },
    mode: "onSubmit",
  });

  async function aoEnviar(valores: Valores) {
    setAviso(null);
    setEnviando(true);
    const resposta = await api
      .POST("/sessao/trocar-senha", { body: { senhaAtual: valores.senhaAtual, novaSenha: valores.novaSenha } })
      .catch(() => null);
    setEnviando(false);
    if (resposta === null) {
      setAviso("Sem conexão. Tente de novo.");
      return;
    }

    if (resposta.data) {
      aceitarSessao(resposta.data);
      await navigate({ to: destinoDaSessao(lerSessao()) });
      return;
    }

    const erro = resposta.error as { codigo?: string; problemas?: string[] } | undefined;
    if (erro?.codigo === "senha-atual-invalida") {
      form.setError("senhaAtual", { message: "Senha provisória incorreta." });
    } else if (erro?.codigo === "senha-invalida") {
      form.setError("novaSenha", {
        message: (erro.problemas ?? []).map((p) => PROBLEMAS[p]).find(Boolean) ?? "Escolha outra senha.",
      });
    } else if (erro?.codigo === "senha-vazada") {
      form.setError("novaSenha", { message: "Essa senha apareceu em vazamentos. Escolha outra." });
    } else if (resposta.response.status === 503) {
      setAviso("Não foi possível verificar a senha agora. Tente de novo em instantes.");
    } else {
      setAviso("Não foi possível trocar a senha. Tente de novo.");
    }
  }

  return (
    <form className="entry-form" onSubmit={form.handleSubmit(aoEnviar)} noValidate>
      <h1 className="f4-title">Escolha sua senha</h1>
      <p className="entry-lead">Sua senha é provisória. Crie uma nova para continuar; ninguém mais vai conhecê-la.</p>
      {aviso ? <Notice tone="error">{aviso}</Notice> : null}
      <TextField
        label="Senha provisória"
        type="password"
        autoComplete="current-password"
        error={form.formState.errors.senhaAtual?.message}
        {...form.register("senhaAtual")}
      />
      <TextField
        label="Nova senha"
        type="password"
        autoComplete="new-password"
        hint={`Pelo menos ${SENHA_MINIMO} caracteres. Pode ser uma frase.`}
        error={form.formState.errors.novaSenha?.message}
        {...form.register("novaSenha")}
      />
      <TextField
        label="Repita a nova senha"
        type="password"
        autoComplete="new-password"
        error={form.formState.errors.confirmacao?.message}
        {...form.register("confirmacao")}
      />
      <Button type="submit" loading={enviando} className="entry-submit">
        Salvar senha
      </Button>
      <Button
        type="button"
        variant="secondary"
        className="entry-secondary"
        onClick={() => void sair().then(() => navigate({ to: "/salao/entrar" }))}
      >
        Sair
      </Button>
    </form>
  );
}
