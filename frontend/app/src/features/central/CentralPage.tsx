import { Button, Chip, formatBRL, Notice, Skeleton, StatusBadge, TextField } from "@flow4tech/design-system";
import { getRouteApi, Link } from "@tanstack/react-router";
import { useEffect, useState } from "react";
import { useAgora } from "../comandas/agora";
import { useMesas } from "../comandas/consultas";
import { useFluxoDeComandas } from "../comandas/fluxo";
import { duracao, estadoDaComanda, minutosAberta, tempoAberta } from "../comandas/lista";
import { type Comanda, nomeExibido } from "../comandas/tipos";
import { useOnline } from "../fila/conexao";
import { usePermissoes } from "../sessao/permissoes";
import "./central.css";
import { useCentral } from "./consultas";
import {
  type FiltrosDaCentral,
  haFiltro,
  juntarGarcons,
  lerFiltros,
  type OpcaoDeGarcom,
  paraBusca,
  paraConsulta,
  soDigitos,
  textoDoTotal,
} from "./filtros";

const rota = getRouteApi("/app/gestao/comandas");

function Cartoes({ comandas, agora }: { comandas: readonly Comanda[]; agora: number }) {
  return (
    <ul className="comanda-list central-cartoes" aria-label="Comandas abertas">
      {comandas.map((comanda) => {
        const estado = estadoDaComanda(comanda, agora);
        return (
          <li key={comanda.id}>
            <Link to="/salao/comanda/$comandaId" params={{ comandaId: comanda.id }} className="comanda-card">
              <span className="comanda-card-nome">
                {nomeExibido(comanda)}
                <span className="f4-visually-hidden"> {comanda.numeroTexto}</span>
              </span>
              {comanda.consumoCentavos !== null ? (
                <span className="comanda-card-valor f4-num">{formatBRL(comanda.consumoCentavos)}</span>
              ) : (
                <span />
              )}
              <span className="comanda-card-meta f4-num">
                {tempoAberta(minutosAberta(comanda, agora))}
                {comanda.garcomNome ? ` · ${comanda.garcomNome}` : ""}
              </span>
              <StatusBadge status={estado.status} detail={estado.detalhe} icon />
            </Link>
          </li>
        );
      })}
    </ul>
  );
}

function Tabela({ comandas, agora }: { comandas: readonly Comanda[]; agora: number }) {
  return (
    <div className="central-rolagem">
      <table className="central-tabela">
        <caption className="f4-visually-hidden">Comandas abertas</caption>
        <thead>
          <tr>
            <th scope="col">Comanda</th>
            <th scope="col">Mesa</th>
            <th scope="col">Garçom</th>
            <th scope="col" className="central-num">
              Valor
            </th>
            <th scope="col">Tempo aberta</th>
            <th scope="col">Situação</th>
          </tr>
        </thead>
        <tbody>
          {comandas.map((comanda) => {
            const estado = estadoDaComanda(comanda, agora);
            return (
              <tr key={comanda.id}>
                <th scope="row">
                  <Link to="/salao/comanda/$comandaId" params={{ comandaId: comanda.id }}>
                    {comanda.numeroTexto}
                    <span className="f4-visually-hidden"> {comanda.identificacao}</span>
                  </Link>
                </th>
                <td>{comanda.mesaId === null ? comanda.identificacao : nomeExibido(comanda)}</td>
                <td>{comanda.garcomNome ?? ""}</td>
                <td className="central-num f4-num">
                  {comanda.consumoCentavos === null ? "" : formatBRL(comanda.consumoCentavos)}
                </td>
                <td className="f4-num">{duracao(minutosAberta(comanda, agora))}</td>
                <td>
                  <StatusBadge status={estado.status} detail={estado.detalhe} icon />
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

/**
 * The central de comandas of the manager at /gestao/comandas: every open comanda of the unit, live, with the filters waiter, table, minimum time open and number, as cards or as a table.
 * Visual reference: .scratch/S-00-novo-sistema/prototipos/index.html#m=web (cards) and #m=web&view=table (table). The map view is not part of this screen yet.
 */
export function CentralPage() {
  const permissoes = usePermissoes();
  const pode = permissoes.carregado && permissoes.pode("comanda:ver");
  const navigate = rota.useNavigate();
  const filtros = lerFiltros(rota.useSearch());
  const central = useCentral(paraConsulta(filtros), pode);
  const aoVivoCaiu = useFluxoDeComandas(pode);
  const mesas = useMesas(pode && permissoes.pode("mesa:ver"));
  const agora = useAgora();
  const online = useOnline();
  // Remounts the two typed fields when the filters are cleared (they hold their text themselves).
  const [rodada, setRodada] = useState(0);
  const [garcons, setGarcons] = useState<OpcaoDeGarcom[]>([]);
  const linhas = central.data?.comandas;
  useEffect(() => {
    if (linhas !== undefined) {
      setGarcons((antes) => juntarGarcons(antes, linhas, filtros.garcom));
    }
  }, [linhas, filtros.garcom]);

  if (permissoes.carregado && !permissoes.pode("comanda:ver")) {
    return <Notice tone="info">Você não tem permissão para ver as comandas.</Notice>;
  }

  const mudar = (parcial: Partial<FiltrosDaCentral>) =>
    void navigate({ search: paraBusca({ ...filtros, ...parcial }), replace: true });
  const limpar = () => {
    setRodada((r) => r + 1);
    mudar({ garcom: "", mesa: "", tempo: "", numero: "" });
  };
  const opcoesDeMesa = mesas.data ?? [];
  const dados = central.data;
  const filtrado = haFiltro(filtros);

  return (
    <section className="central" aria-labelledby="central-titulo">
      <h2 id="central-titulo" className="central-titulo">
        Central de comandas
      </h2>

      {!online ? <Notice tone="info">Sem conexão. A lista pode estar desatualizada.</Notice> : null}
      {online && aoVivoCaiu ? (
        <Notice tone="info">Reconectando ao vivo. A lista continua aqui e se atualiza sozinha.</Notice>
      ) : null}

      <form
        key={rodada}
        className="central-filtros"
        aria-label="Filtros da central"
        onSubmit={(e) => {
          e.preventDefault();
          const dado = new FormData(e.currentTarget);
          mudar({ tempo: soDigitos(String(dado.get("tempo"))), numero: soDigitos(String(dado.get("numero"))) });
        }}
      >
        <div className="f4-field">
          <label htmlFor="central-garcom" className="f4-field-label">
            Garçom
          </label>
          <div className="f4-field-control">
            <select
              id="central-garcom"
              className="f4-input"
              value={filtros.garcom}
              onChange={(e) => mudar({ garcom: e.target.value })}
            >
              <option value="">Todos</option>
              {garcons.map((g) => (
                <option key={g.id} value={g.id}>
                  {g.nome}
                </option>
              ))}
            </select>
          </div>
        </div>
        {opcoesDeMesa.length > 0 ? (
          <div className="f4-field">
            <label htmlFor="central-mesa" className="f4-field-label">
              Mesa
            </label>
            <div className="f4-field-control">
              <select
                id="central-mesa"
                className="f4-input"
                value={filtros.mesa}
                onChange={(e) => mudar({ mesa: e.target.value })}
              >
                <option value="">Todas</option>
                {opcoesDeMesa.map((m) => (
                  <option key={m.id} value={m.id}>
                    {m.identificacao}
                  </option>
                ))}
              </select>
            </div>
          </div>
        ) : null}
        <TextField
          name="tempo"
          label="Aberta há (min)"
          placeholder="Aberta há (min)"
          inputMode="numeric"
          autoComplete="off"
          defaultValue={filtros.tempo}
          onBlur={(e) => mudar({ tempo: soDigitos(e.target.value) })}
        />
        <TextField
          name="numero"
          label="Número"
          placeholder="Número"
          inputMode="numeric"
          autoComplete="off"
          defaultValue={filtros.numero}
          onBlur={(e) => mudar({ numero: soDigitos(e.target.value) })}
        />
        <button type="submit" className="f4-visually-hidden">
          Filtrar
        </button>
      </form>

      <div className="central-barra">
        <p className="central-total" role="status">
          {dados ? textoDoTotal(dados.total, dados.comandas.length) : ""}
        </p>
        <div className="central-vista" role="group" aria-label="Visualização">
          <Chip selected={filtros.vista === "cartoes"} onClick={() => mudar({ vista: "cartoes" })}>
            Cartões
          </Chip>
          <Chip selected={filtros.vista === "tabela"} onClick={() => mudar({ vista: "tabela" })}>
            Tabela
          </Chip>
        </div>
      </div>

      {central.isPending && pode ? <Skeleton rows={4} label="Carregando comandas" /> : null}

      {central.isError && dados === undefined ? (
        <Notice
          tone="error"
          action={
            <Button variant="secondary" onClick={() => void central.refetch()}>
              Tentar novamente
            </Button>
          }
        >
          Não foi possível carregar as comandas.
        </Notice>
      ) : null}

      {dados && dados.comandas.length === 0 ? (
        <div className="comandas-nada">
          <p className="comandas-vazio">
            {filtrado ? "Nenhuma comanda aberta com estes filtros." : "Nenhuma comanda aberta."}
          </p>
          {filtrado ? (
            <Button variant="secondary" onClick={limpar}>
              Limpar filtros
            </Button>
          ) : null}
        </div>
      ) : null}

      {dados && dados.comandas.length > 0 ? (
        filtros.vista === "tabela" ? (
          <Tabela comandas={dados.comandas} agora={agora} />
        ) : (
          <Cartoes comandas={dados.comandas} agora={agora} />
        )
      ) : null}
    </section>
  );
}
