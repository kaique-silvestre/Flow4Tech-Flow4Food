import { Button, formatBRL, Notice, Skeleton, Tab, TabList, TabPanel, Tabs, ToneBadge } from "@flow4tech/design-system";
import { type KeyboardEvent, type ReactNode, useEffect, useState } from "react";
import { BUSCA_MAXIMA, buscarProdutos, type Categoria, type Produto, useBusca, useGruposDoProduto } from "./cardapio";
import { exigeEscolha } from "./opcoes";

interface Props {
  produtos: readonly Produto[];
  /** What this waiter launches most (best first). */
  favoritos?: readonly Produto[];
  /** What the unit sells most (best first). */
  frequentes: readonly Produto[];
  categorias: readonly Categoria[];
  /** Units staged per product, to show ×N. */
  quantidades: ReadonlyMap<string, number>;
  onAdicionar: (produto: Produto) => void;
  /** Opens the item sheet (add-ons, note, quantity) for a product that offers add-ons. */
  onOpcoes: (produto: Produto) => void;
  carregando: boolean;
  erro: boolean;
  onTentarDeNovo: () => void;
  /** "Última vez nesta mesa": shown on top of the favorites tab, only when the server had something. */
  topo?: ReactNode;
  /** The search is answered by the server when it can (code first). */
  buscaNoServidor?: boolean;
}

/** The wait before a typed term goes to the server: the local filter answers at once in the meantime. */
const ESPERA_DA_BUSCA_MS = 200;
const SEM_FAVORITOS: readonly Produto[] = [];

function LinhaDoProduto({
  produto,
  quantidade,
  onAdicionar,
  onOpcoes,
}: {
  produto: Produto;
  quantidade: number;
  onAdicionar: () => void;
  onOpcoes: () => void;
}) {
  // The groups are read once per product (cached): they tell the row whether to offer the add-ons button. A failed read just hides the button; the tap decides again.
  const grupos = useGruposDoProduto(produto.id, !produto.indisponivel);
  const temOpcoes = (grupos.data?.length ?? 0) > 0 && !produto.indisponivel;
  return (
    <li
      className="produto-linha"
      data-acabou={produto.indisponivel ? "true" : undefined}
      data-opcoes={temOpcoes ? "true" : undefined}
    >
      <span className="produto-nome">
        {produto.nome}
        <span className="produto-meta">
          <span className="produto-codigo">{produto.codigo}</span>
          {produto.indisponivel ? <ToneBadge tone="warning">Acabou</ToneBadge> : null}
          {temOpcoes && grupos.data && exigeEscolha(grupos.data) ? (
            <span className="produto-escolha">Escolha opções</span>
          ) : null}
        </span>
      </span>
      <span className="produto-preco f4-num">{formatBRL(produto.precoCentavos)}</span>
      <span className="produto-qtd f4-num" aria-hidden={quantidade === 0}>
        {quantidade > 0 ? `×${quantidade}` : ""}
      </span>
      {temOpcoes ? (
        <button type="button" className="produto-opcoes" aria-label={`Opções de ${produto.nome}`} onClick={onOpcoes}>
          ⋯
        </button>
      ) : null}
      {produto.indisponivel ? (
        <span aria-hidden="true" />
      ) : (
        <button type="button" className="produto-mais" aria-label={`Adicionar ${produto.nome}`} onClick={onAdicionar}>
          +
        </button>
      )}
    </li>
  );
}

/** Search, category tabs (Frequentes first) and the product rows with ×N and +. Adding is one tap per unit; nothing is sent from here. */
export function Picker({
  produtos,
  favoritos = SEM_FAVORITOS,
  frequentes,
  categorias,
  quantidades,
  onAdicionar,
  onOpcoes,
  carregando,
  erro,
  onTentarDeNovo,
  topo,
  buscaNoServidor = true,
}: Props) {
  const [busca, setBusca] = useState("");
  const [termo, setTermo] = useState("");
  const [numerico, setNumerico] = useState(false);
  const [aba, setAba] = useState<string | null>(null);
  useEffect(() => {
    const timer = window.setTimeout(() => setTermo(busca), ESPERA_DA_BUSCA_MS);
    return () => window.clearTimeout(timer);
  }, [busca]);
  const noServidor = useBusca(termo, buscaNoServidor && termo.trim() !== "" && termo === busca);

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

  const naAba = favoritos.length > 0 || frequentes.length > 0 || topo !== undefined;
  const abaAtiva = aba ?? (naAba ? "favoritos" : (categorias[0]?.id ?? "favoritos"));
  const buscando = busca.trim() !== "";
  // The server's order (exact code first) once it answered for the text on the screen; until then, and with no signal, the local filter of the loaded menu.
  const resultados = noServidor.data && termo === busca ? noServidor.data : buscarProdutos(produtos, busca);
  const maisVendidos = frequentes.filter((f) => !favoritos.some((v) => v.id === f.id));

  // Typing a short code and pressing Enter launches it: the quickest way to a known item. Only an exact code acts; a name needs the +.
  function aoTeclar(e: KeyboardEvent<HTMLInputElement>) {
    if (e.key !== "Enter") {
      return;
    }

    e.preventDefault();
    const digitado = busca.trim().toLowerCase();
    const exato = resultados.find((p) => p.codigo.toLowerCase() === digitado && !p.indisponivel);
    if (exato) {
      onAdicionar(exato);
      setBusca("");
      setTermo("");
    }
  }

  const lista = (itens: readonly Produto[]) => (
    <ul className="produto-lista">
      {itens.map((p) => (
        <LinhaDoProduto
          key={p.id}
          produto={p}
          quantidade={quantidades.get(p.id) ?? 0}
          onAdicionar={() => onAdicionar(p)}
          onOpcoes={() => onOpcoes(p)}
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
          inputMode={numerico ? "numeric" : "search"}
          enterKeyHint="go"
          placeholder="Buscar por nome ou código"
          autoComplete="off"
          maxLength={BUSCA_MAXIMA}
          value={busca}
          onChange={(e) => setBusca(e.target.value)}
          onKeyDown={aoTeclar}
        />
        <button
          type="button"
          className="picker-codigo"
          aria-pressed={numerico}
          aria-label="123, teclado numérico para o código"
          onClick={() => setNumerico((v) => !v)}
        >
          123
        </button>
      </div>

      {buscando ? (
        resultados.length === 0 ? (
          <p className="comandas-vazio">Nada com “{busca.trim()}”. Tente o código do produto.</p>
        ) : (
          <>
            <p className="f4-visually-hidden" role="status">
              {resultados.length === 1 ? "1 produto encontrado" : `${resultados.length} produtos encontrados`}
            </p>
            {lista(resultados)}
          </>
        )
      ) : (
        <Tabs value={abaAtiva} onValueChange={setAba}>
          <TabList aria-label="Categorias">
            <Tab value="favoritos">Favoritos</Tab>
            {categorias.map((c) => (
              <Tab key={c.id} value={c.id}>
                {c.nome}
              </Tab>
            ))}
          </TabList>
          <TabPanel value="favoritos">
            {topo}
            {favoritos.length === 0 && maisVendidos.length === 0 ? (
              <p className="comandas-vazio">Ainda não há favoritos. Use a busca ou uma categoria.</p>
            ) : null}
            {favoritos.length > 0 ? (
              <>
                <h3 className="picker-secao">Seus favoritos</h3>
                {lista(favoritos)}
              </>
            ) : null}
            {maisVendidos.length > 0 ? (
              <>
                <h3 className="picker-secao">Mais vendidos</h3>
                {lista(maisVendidos)}
              </>
            ) : null}
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
