import { randomUUID } from "node:crypto";
import { ApiError } from "@/lib/api-client";
import { addDays, formatDate, formatMoney, isIsoDate, todayInSaoPaulo } from "@/lib/format";
import { apiForCurrentUser } from "@/lib/gateway";
import { keepMerchantsHere } from "@/lib/roles-server";
import { reverseEntryAction } from "./actions";
import { EntryForm } from "./entry-form";

export const dynamic = "force-dynamic";

const PAGE_SIZE = 20;

export default async function EntriesPage({
  searchParams,
}: Readonly<{ searchParams: Promise<{ de?: string; ate?: string; pagina?: string }> }>) {
  const today = todayInSaoPaulo();
  const query = await searchParams;
  const to = isIsoDate(query.ate) && query.ate <= today ? query.ate : today;
  const from = isIsoDate(query.de) && query.de <= to ? query.de : addDays(to, -6);
  const page = Math.max(1, Number.parseInt(query.pagina ?? "1", 10) || 1);

  await keepMerchantsHere();
  const api = await apiForCurrentUser();
  let list;
  let failure: ApiError | undefined;
  try {
    list = await api.listEntries({ from, to, page, pageSize: PAGE_SIZE });
  } catch (error) {
    if (!(error instanceof ApiError)) throw error;
    failure = error;
  }

  const pages = list ? Math.max(1, Math.ceil(list.totalCount / PAGE_SIZE)) : 1;
  const link = (target: number) => `/lancamentos?de=${from}&ate=${to}&pagina=${target}`;

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Lançamentos</h1>
          <p className="sub">Registre entradas e saídas. Um lançamento não se apaga: corrige-se com um estorno.</p>
        </div>
      </div>

      <div className="stack">
        <EntryForm today={today} />

        <div className="card">
          <h2>Histórico</h2>
          <form className="inline" method="get" style={{ marginBottom: 14 }}>
            <label>
              De
              <input type="date" name="de" defaultValue={from} max={today} />
            </label>
            <label>
              Até
              <input type="date" name="ate" defaultValue={to} max={today} />
            </label>
            <button type="submit" className="secondary">Filtrar</button>
          </form>

          {failure && (
            <div className="notice err" role="alert">
              {failure.status === 0 || failure.status >= 500
                ? "A lista está temporariamente indisponível. Tente novamente em instantes."
                : `${failure.title}. ${failure.detail}`}
            </div>
          )}

          {list && list.items.length === 0 && <p className="empty">Nenhum lançamento neste período.</p>}

          {list && list.items.length > 0 && (
            <>
              <div className="table-wrap">
                <table>
                  <thead>
                    <tr>
                      <th>Data</th>
                      <th>Tipo</th>
                      <th className="num">Valor</th>
                      <th>Descrição</th>
                      <th>Situação</th>
                      <th />
                    </tr>
                  </thead>
                  <tbody>
                    {list.items.map((entry) => (
                      <tr key={entry.id}>
                        <td>{formatDate(entry.entryDate)}</td>
                        <td>{entry.type === "Credit" ? "Crédito" : "Débito"}</td>
                        <td className={`num ${entry.type === "Credit" ? "credit" : "debit"}`}>
                          {entry.type === "Credit" ? "+" : "−"} {formatMoney(entry.amount)}
                        </td>
                        <td className="wrap">{entry.description ?? <span className="muted">—</span>}</td>
                        <td>
                          {entry.reversalOf && <span className="badge">Estorno</span>}
                          {entry.reversedBy && <span className="badge">Estornado</span>}
                        </td>
                        <td>
                          {!entry.reversalOf && !entry.reversedBy && (
                            <form action={reverseEntryAction}>
                              <input type="hidden" name="id" value={entry.id} />
                              <input type="hidden" name="idempotencyKey" value={randomUUID()} />
                              <button type="submit" className="link">Estornar</button>
                            </form>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              <p className="muted small" style={{ marginBottom: 0 }}>
                {list.totalCount} lançamento(s) · página {page} de {pages}
                {page > 1 && <> · <a href={link(page - 1)}>anterior</a></>}
                {page < pages && <> · <a href={link(page + 1)}>próxima</a></>}
              </p>
            </>
          )}
        </div>
      </div>
    </>
  );
}
