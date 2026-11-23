import { chartGeometry } from "@/lib/chart";
import { formatDate, formatMoney } from "@/lib/format";

const WIDTH = 560;
const HEIGHT = 200;

/** Accumulated balance over the days of the range, as a smooth line that draws itself. Rendered on the server. */
export function BalanceChart({ days }: Readonly<{ days: ReadonlyArray<{ date: string; accumulatedBalance: number }> }>) {
  const geometry = chartGeometry(days.map((d) => d.accumulatedBalance), WIDTH, HEIGHT);
  if (!geometry) return <p className="empty">Sem dados suficientes para o gráfico.</p>;

  const first = days[0];
  const last = days[days.length - 1];
  const [lastX, lastY] = geometry.points[geometry.points.length - 1];
  const middle = days[Math.floor(days.length / 2)];

  return (
    <>
      <svg
        className="chart"
        viewBox={`0 0 ${WIDTH} ${HEIGHT}`}
        role="img"
        aria-label={`Saldo acumulado de ${formatDate(first.date)} a ${formatDate(last.date)}: de ${formatMoney(first.accumulatedBalance)} para ${formatMoney(last.accumulatedBalance)}`}
      >
        <defs>
          <linearGradient id="chart-fill" x1="0" x2="0" y1="0" y2="1">
            <stop offset="0" stopColor="var(--acc)" stopOpacity="0.3" />
            <stop offset="1" stopColor="var(--acc)" stopOpacity="0" />
          </linearGradient>
        </defs>
        <g className="grid" aria-hidden="true">
          {[0.25, 0.5, 0.75].map((f) => (
            <line key={f} x1="0" x2={WIDTH} y1={HEIGHT * f} y2={HEIGHT * f} />
          ))}
        </g>
        <path d={geometry.area} fill="url(#chart-fill)" />
        <path className="line draw" d={geometry.line} pathLength={1} />
        <circle className="dot" cx={lastX} cy={lastY} r="6" />
      </svg>
      <div className="axis" aria-hidden="true">
        <span>{formatDate(first.date).slice(0, 5)}</span>
        {days.length > 2 && <span>{formatDate(middle.date).slice(0, 5)}</span>}
        <span>{formatDate(last.date).slice(0, 5)}</span>
      </div>
    </>
  );
}
