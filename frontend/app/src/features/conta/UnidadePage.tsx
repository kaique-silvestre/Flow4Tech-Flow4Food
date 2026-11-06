import { Button, Notice, Skeleton } from "@flow4tech/design-system";
import { useNavigate } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { api, definirUnidadeAtiva, destinoDaSessao, lerSessao, sair } from "../sessao/sessao";

export function UnidadePage() {
  const navigate = useNavigate();
  const [escolhendo, setEscolhendo] = useState<string | null>(null);
  const [erro, setErro] = useState<string | null>(null);
  const unidades = useQuery({
    queryKey: ["unidades"],
    queryFn: async () => {
      const { data } = await api.GET("/unidades");
      if (!data) {
        throw new Error("unidades");
      }

      return data;
    },
  });

  async function escolher(id: string) {
    setErro(null);
    setEscolhendo(id);
    try {
      const { data } = await api.POST("/sessao/unidade-ativa", { body: { unidadeDestinoId: id } });
      if (!data) {
        setErro("Não foi possível entrar nesta unidade.");
        return;
      }

      definirUnidadeAtiva(id);
      await navigate({ to: destinoDaSessao(lerSessao()) });
    } catch {
      setErro("Sem conexão. Tente de novo.");
    } finally {
      setEscolhendo(null);
    }
  }

  async function aoSair() {
    await sair();
    await navigate({ to: "/salao/entrar" });
  }

  if (unidades.isPending) {
    return (
      <section className="entry-form">
        <h1 className="f4-title">Escolha a unidade</h1>
        <Skeleton rows={2} label="Carregando unidades" />
      </section>
    );
  }

  if (unidades.isError) {
    return (
      <section className="entry-form">
        <h1 className="f4-title">Escolha a unidade</h1>
        <Notice
          tone="error"
          action={
            <Button variant="secondary" onClick={() => void unidades.refetch()}>
              Tentar novamente
            </Button>
          }
        >
          Não foi possível carregar as unidades.
        </Notice>
        <Button variant="secondary" className="entry-secondary" onClick={() => void aoSair()}>
          Sair
        </Button>
      </section>
    );
  }

  if (unidades.data.length === 0) {
    return (
      <section className="entry-form">
        <h1 className="f4-title">Sem unidade</h1>
        <p className="entry-lead">Você não está vinculado a nenhuma unidade. Fale com o responsável para liberar o seu acesso.</p>
        <Button variant="secondary" className="entry-secondary" onClick={() => void aoSair()}>
          Sair
        </Button>
      </section>
    );
  }

  return (
    <section className="entry-form">
      <h1 className="f4-title">Escolha a unidade</h1>
      <p className="entry-lead">Você trabalha em mais de uma. Dá para trocar depois.</p>
      {erro ? <Notice tone="error">{erro}</Notice> : null}
      <ul className="unit-list">
        {unidades.data.map((unidade) => (
          <li key={unidade.id}>
            <Button variant="secondary" className="unit-option" loading={escolhendo === unidade.id} disabled={escolhendo !== null} onClick={() => void escolher(unidade.id)}>
              {unidade.nome}
            </Button>
          </li>
        ))}
      </ul>
      <Button variant="secondary" className="entry-secondary" onClick={() => void aoSair()}>
        Sair
      </Button>
    </section>
  );
}
