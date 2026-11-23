import { redirect } from "next/navigation";
import { auth } from "@/auth";
import { CountUp } from "@/components/count-up";
import { ApiError } from "@/lib/api-client";
import { addDays, formatDate, formatMoney, isIsoDate, todayInSaoPaulo } from "@/lib/format";
import { apiForCurrentUser } from "@/lib/gateway";
import { chosenMerchant, isAuditor, shortId } from "@/lib/roles";

export const dynamic = "force-dynamic";

const PAGE_SIZE = 20;

const signed = (value: number) => (value < 0 ? "debit" : value > 0 ? "credit" : "");

/**
 * Read-only view of the merchants assigned to the signed-in auditor. Every read is recorded by the API with who asked,
 * which merchant and which resource. There is nothing to edit here: an auditor cannot register, reverse or rebuild.
 */
export default async function AuditPage({
  searchParams,
}: Readonly<{ searchParams: Promise<{ comerciante?: string; data?: string; pagina?: string }> }>) {
  const session = await auth();
  if (!session || !isAuditor(session)) redirect("/saldo"); // a merchant has no audit screen

  const assigned = session.auditMerchants ?? [];
  const query = await searchParams;
  const merchantId = chosenMerchant(query.comerciante, assigned)!;
  const today = todayInSaoPaulo();
  const date = isIsoDate(query.data) && query.data <= today ? query.data : today;
  const page = Math.max(1, Number.parseInt(query.pagina ?? "1", 10) || 1);

  const api = await apiForCurrentUser();
  let balance;
  let list;
  let failure: ApiError | undefined;
  try {
    [balance, list] = await Promise.all([
      api.getBalance(date, merchantId),
      api.listEntries({ from: date, to: date, page, pageSize: PAGE_SIZE, merchantId }),
    ]);
  } catch (error) {
    if (!(error instanceof ApiError)) throw error;
    failure = error;
  }

  const pages = list ? Math.max(1, Math.ceil(list.totalCount / PAGE_SIZE)) : 1;
  const link = (target: number) => `/auditoria?comerciante=${merchantId}&data=${date}&pagina=${target}`;

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Auditoria</h1>
          <p className="sub">
            Consulta somente leitura dos comerciantes atribuídos a você. Cada consulta fica registrada, com quem consultou,
            qual comerciante e qual informação.
          </p>
        </div>
        <span className="chip">
          <span className="led" /> consulta registrada
        </span>
      </div>

      <form className="inline" method="get" style={{ marginBottom: 14 }}>
        <label>
          Comerciante
          <select name="comerciante" defaultValue={merchantId}>
            {assigned.map((id) => (
              <option key={id} value={id}>{shortId(id)}…</option>
            ))}
          </select>
        </label>
        <label>
          Dia
          <input type="date" name="data" defaultValue={date} max={today} />
        </label>
        <button type="submit">Consultar</button>
      </form>

      {failure && (
        <div className="notice err" role="alert">
          {failure.status === 403
            ? "Você não tem acesso a este comerciante."
            : failure.status === 0 || failure.status >= 500
              ? "A consulta está temporariamente indisponível. Tente novamente em instantes."
              : `${failure.title}. ${failure.detail}`}
        </div>
      )}

      <div className="stack">
        {balance && (
          <div className="bento">
            <section className="card kpi s4">
              <h2>Créditos · {formatDate(date)}</h2>
              <div className="value credit">{formatMoney(balance.credits)}</div>
            </section>
            <section className="card kpi s4">
              <h2>Débitos</h2>
              <div className="value debit">{formatMoney(balance.debits)}</div>
            </section>
            <section className="card kpi s4">
              <h2>Resultado do dia</h2>
              <div className={`value ${signed(balance.dayResult)}`}>{formatMoney(balance.dayResult)}</div>
            </section>
            <section className="card hero s12">
              <div className="glow" />
              <div className="eyebrow">Saldo acumulado</div>
              <div className={`amount ${balance.accumulatedBalance < 0 ? "negative" : ""}`} style={{ marginBottom: 0 }}>
                <span className="cur">R$</span>
                <CountUp value={balance.accumulatedBalance} />
              </div>
            </section>
          </div>
        )}

        {list && (
          <div className="card">
            <h2>Lançamentos de {formatDate(date)}</h2>
            {list.items.length === 0 ? (
              <p className="empty">Nenhum lançamento neste dia.</p>
            ) : (
              <div className="table-wrap">
                <table>
                  <thead>
                    <tr>
                      <th>Registrado em</th>
                      <th>Tipo</th>
                      <th className="num">Valor</th>
                      <th>Descrição</th>
                      <th>Registrado por</th>
                      <th>Situação</th>
                    </tr>
                  </thead>
                  <tbody>
                    {list.items.map((entry) => (
                      <tr key={entry.id}>
                        <td>{new Date(entry.registeredAt).toLocaleString("pt-BR", { timeZone: "America/Sao_Paulo" })}</td>
                        <td>{entry.type === "Credit" ? "Crédito" : "Débito"}</td>
                        <td className={`num ${entry.type === "Credit" ? "credit" : "debit"}`}>{formatMoney(entry.amount)}</td>
                        <td className="wrap">{entry.description ?? "—"}</td>
                        <td title={entry.createdBy}>{shortId(entry.createdBy)}</td>
                        <td>{entry.reversalOf ? "Estorno" : entry.reversedBy ? "Estornado" : "Ativo"}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            {pages > 1 && (
              <p className="muted small" style={{ marginBottom: 0 }}>
                Página {page} de {pages}.{" "}
                {page > 1 && <a href={link(page - 1)}>Anterior</a>} {page < pages && <a href={link(page + 1)}>Próxima</a>}
              </p>
            )}
            <p className="muted small" style={{ marginBottom: 0 }}>
              Dia anterior: <a href={`/auditoria?comerciante=${merchantId}&data=${addDays(date, -1)}`}>{formatDate(addDays(date, -1))}</a>
            </p>
          </div>
        )}
      </div>
    </>
  );
}
