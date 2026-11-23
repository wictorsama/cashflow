import Link from "next/link";
import { BalanceChart } from "@/components/balance-chart";
import { CountUp } from "@/components/count-up";
import { ApiError } from "@/lib/api-client";
import { addDays, formatDate, formatMoney, formatTime, isIsoDate, todayInSaoPaulo } from "@/lib/format";
import { apiForCurrentUser } from "@/lib/gateway";
import { keepMerchantsHere } from "@/lib/roles-server";
import { AutoRefresh } from "../auto-refresh";

export const dynamic = "force-dynamic";

const FEED_SIZE = 6;

const signed = (value: number) => (value < 0 ? "debit" : value > 0 ? "credit" : "");

export default async function BalancePage({ searchParams }: Readonly<{ searchParams: Promise<{ data?: string }> }>) {
  const today = todayInSaoPaulo();
  const { data } = await searchParams;
  const date = isIsoDate(data) && data <= today ? data : today;

  await keepMerchantsHere();
  const api = await apiForCurrentUser();
  let balance;
  let range;
  let feed;
  let failure: ApiError | undefined;
  try {
    [balance, range, feed] = await Promise.all([
      api.getBalance(date),
      api.getBalanceRange(addDays(date, -6), date),
      api.listEntries({ from: date, to: date, page: 1, pageSize: FEED_SIZE }),
    ]);
  } catch (error) {
    if (!(error instanceof ApiError)) throw error;
    failure = error;
  }

  const turnover = balance ? balance.credits + balance.debits : 0;
  const creditShare = turnover > 0 ? (balance!.credits / turnover) * 100 : 0;

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Saldo diário</h1>
          <p className="sub">Consolidado em tempo quase real: um lançamento novo aparece em poucos segundos.</p>
        </div>
        <AutoRefresh seconds={10} />
      </div>

      <form className="inline" method="get" style={{ marginBottom: 14 }}>
        <label>
          Dia
          <input type="date" name="data" defaultValue={date} max={today} />
        </label>
        <button type="submit" className="secondary">Consultar</button>
      </form>

      {failure && (
        <div className="notice err" role="alert">
          {failure.status === 0 || failure.status >= 500
            ? "O saldo está temporariamente indisponível. Seus lançamentos continuam sendo registrados normalmente e o saldo será atualizado quando o serviço voltar."
            : `${failure.title}. ${failure.detail}`}
        </div>
      )}

      {balance && (
        <div className="bento">
          <section className="card hero s7" aria-label="Saldo acumulado">
            <div className="glow" />
            <div className="eyebrow">Saldo acumulado · {formatDate(date)}</div>
            <div className={`amount ${balance.accumulatedBalance < 0 ? "negative" : ""}`}>
              <span className="cur">R$</span>
              <CountUp value={balance.accumulatedBalance} />
            </div>
            {balance.dayResult > 0 && <span className="delta">▲ +{formatMoney(balance.dayResult)} no dia</span>}
            {balance.dayResult < 0 && <span className="delta down">▼ {formatMoney(balance.dayResult)} no dia</span>}
            {balance.dayResult === 0 && <span className="delta flat">● sem variação no dia</span>}
          </section>

          <section className="card s5" aria-label="Entradas e saídas do dia">
            <h2>Entradas × saídas</h2>
            <div className="split" role="img" aria-label={`Créditos ${creditShare.toFixed(0)}% e débitos ${(turnover > 0 ? 100 - creditShare : 0).toFixed(0)}% do movimento`}>
              <i className="c" style={{ width: `${creditShare}%` }} />
              <i className="d" style={{ width: turnover > 0 ? `${100 - creditShare}%` : "0%" }} />
            </div>
            <div className="legend">
              <span className="c">créditos</span>
              <span className="d">débitos</span>
            </div>
            <p className="muted small" style={{ margin: "12px 0 0" }}>
              {turnover > 0 ? `Movimento do dia: ${formatMoney(turnover)}.` : "Nenhum movimento neste dia."}
            </p>
          </section>

          <section className="card kpi s4">
            <h2>Créditos</h2>
            <div className="value credit">{formatMoney(balance.credits)}</div>
            <div className="meter credit"><i style={{ width: `${creditShare}%` }} /></div>
          </section>
          <section className="card kpi s4">
            <h2>Débitos</h2>
            <div className="value debit">{formatMoney(balance.debits)}</div>
            <div className="meter debit"><i style={{ width: turnover > 0 ? `${100 - creditShare}%` : "0%" }} /></div>
          </section>
          <section className="card kpi s4">
            <h2>Resultado do dia</h2>
            <div className={`value ${signed(balance.dayResult)}`}>{formatMoney(balance.dayResult)}</div>
            <div className={`meter ${signed(balance.dayResult)}`}><i style={{ width: turnover > 0 ? `${(Math.abs(balance.dayResult) / turnover) * 100}%` : "0%" }} /></div>
          </section>

          {range && (
            <section className="card s8" aria-label="Evolução do saldo">
              <h2>Saldo acumulado · 7 dias</h2>
              <BalanceChart days={range.days} />
            </section>
          )}

          <section className="card s4" aria-label="Movimento do dia">
            <h2>Fluxo de {formatDate(date)}</h2>
            {feed && feed.items.length > 0 ? (
              <ul className="feed">
                {feed.items.map((entry) => (
                  <li key={entry.id}>
                    <span className={`ico ${entry.type === "Credit" ? "credit" : "debit"}`} aria-hidden="true">{entry.type === "Credit" ? "↗" : "↘"}</span>
                    <span>
                      {entry.description ?? (entry.type === "Credit" ? "Crédito" : "Débito")}
                      <small>
                        {formatTime(entry.registeredAt)}
                        {entry.reversalOf ? " · estorno" : ""}
                        {entry.reversedBy ? " · estornado" : ""}
                      </small>
                    </span>
                    <span className={`amt ${entry.type === "Credit" ? "credit" : "debit"}`}>
                      {entry.type === "Credit" ? "+" : "−"} {formatMoney(entry.amount)}
                    </span>
                  </li>
                ))}
              </ul>
            ) : (
              <p className="empty">Nenhum lançamento neste dia.</p>
            )}
            <p className="small" style={{ margin: "12px 0 0" }}>
              <Link href="/lancamentos">Ver todos os lançamentos →</Link>
            </p>
          </section>
        </div>
      )}

      {range && (
        <section className="card" style={{ marginTop: 14 }} aria-label="Últimos 7 dias">
          <h2>Últimos 7 dias</h2>
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Dia</th>
                  <th className="num">Créditos</th>
                  <th className="num">Débitos</th>
                  <th className="num">Resultado</th>
                  <th className="num">Saldo acumulado</th>
                </tr>
              </thead>
              <tbody>
                {[...range.days].reverse().map((day) => (
                  <tr key={day.date}>
                    <td>{formatDate(day.date)}</td>
                    <td className="num credit">{formatMoney(day.credits)}</td>
                    <td className="num debit">{formatMoney(day.debits)}</td>
                    <td className={`num ${signed(day.dayResult)}`}>{formatMoney(day.dayResult)}</td>
                    <td className={`num ${signed(day.accumulatedBalance)}`}>{formatMoney(day.accumulatedBalance)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <p className="muted small" style={{ marginBottom: 0 }}>
            Saldo no início do período: {formatMoney(range.openingBalance)}.
          </p>
        </section>
      )}
    </>
  );
}
