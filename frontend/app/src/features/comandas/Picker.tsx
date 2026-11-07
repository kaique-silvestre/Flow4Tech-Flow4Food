import { Button, formatBRL, Notice, Skeleton, Tab, TabList, TabPanel, Tabs } from "@flow4tech/design-system";
import { useState } from "react";
import { buscarProdutos, type Categoria, type Produto } from "./cardapio";

interface Props {
  produtos: readonly Produto[];
  frequentes: readonly Produto[];
  categorias: readonly Categoria[];
  /** Units staged per product, to show ×N. */
  quantidades: ReadonlyMap<string, number>;
  onAdicionar: (produto: Produto) => void;
  carregando: boolean;
  erro: boolean;
  onTentarDeNovo: () => void;
}

function LinhaDoProduto({
  produto,
  quantidade,
  onAdicionar,
}: {
  produto: Produto;
  quantidade: number;
  onAdicionar: () => void;
}) {
  return (
    <li className="produto-linha">
      <span className="produto-nome">
        {produto.nome}
        <span className="produto-codigo">{produto.codigo}</span>
      </span>
      <span className="produto-preco f4-num">{formatBRL(produto.precoCentavos)}</span>
      <span className="produto-qtd f4-num" aria-hidden={quantidade === 0}>
        {quantidade > 0 ? `×${quantidade}` : ""}
      </span>
      <button type="button" className="produto-mais" aria-label={`Adicionar ${produto.nome}`} onClick={onAdicionar}>
        +
      </button>
    </li>
  );
}

/** Search, category tabs (Frequentes first) and the product rows with ×N and +. Adding is one tap per unit; nothing is sent from here. */
export function Picker({
  produtos,
  frequentes,
  categorias,
  quantidades,
  onAdicionar,
  carregando,
  erro,
  onTentarDeNovo,
}: Props) {
  const [busca, setBusca] = useState("");
  const [aba, setAba] = useState<string | null>(null);

  if (carregando) {
    return <Skeleton rows={4} label="Carregando o cardápio" />;
  }

  if (erro) {
    return (
      <Notice
        tone="error"
        action={
          <Button variant="secondary" onClick={onTentarDeNovo}>
            Tentar novamente
          </Button>
        }
      >
        Não foi possível carregar o cardápio.
      </Notice>
    );
  }

  const abaAtiva = aba ?? (frequentes.length > 0 ? "frequentes" : (categorias[0]?.id ?? "frequentes"));
  const buscando = busca.trim() !== "";
  const resultados = buscarProdutos(produtos, busca);

  const lista = (itens: readonly Produto[]) => (
    <ul className="produto-lista">
      {itens.map((p) => (
        <LinhaDoProduto
          key={p.id}
          produto={p}
          quantidade={quantidades.get(p.id) ?? 0}
          onAdicionar={() => onAdicionar(p)}
        />
      ))}
    </ul>
  );

  return (
    <section className="picker" aria-label="Cardápio">
      <div className="f4-field picker-busca">
        <label htmlFor="busca-produtos" className="f4-visually-hidden">
          Buscar produto
        </label>
        <input
          id="busca-produtos"
          className="f4-input"
          type="search"
          inputMode="search"
          placeholder="Buscar por nome ou código"
          autoComplete="off"
          value={busca}
          onChange={(e) => setBusca(e.target.value)}
        />
      </div>

      {buscando ? (
        resultados.length === 0 ? (
          <p className="comandas-vazio">Nada com “{busca.trim()}”. Tente o código do produto.</p>
        ) : (
          lista(resultados)
        )
      ) : (
        <Tabs value={abaAtiva} onValueChange={setAba}>
          <TabList aria-label="Categorias">
            <Tab value="frequentes">Frequentes</Tab>
            {categorias.map((c) => (
              <Tab key={c.id} value={c.id}>
                {c.nome}
              </Tab>
            ))}
          </TabList>
          <TabPanel value="frequentes">
            {frequentes.length === 0 ? (
              <p className="comandas-vazio">Ainda não há itens frequentes. Use a busca ou uma categoria.</p>
            ) : (
              lista(frequentes)
            )}
          </TabPanel>
          {categorias.map((c) => (
            <TabPanel key={c.id} value={c.id}>
              {lista(produtos.filter((p) => p.categoriaId === c.id))}
            </TabPanel>
          ))}
        </Tabs>
      )}
    </section>
  );
}
