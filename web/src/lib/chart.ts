export interface ChartGeometry {
  /** SVG path of the smooth line. */
  line: string;
  /** The same path closed down to the baseline, for the soft fill under the line. */
  area: string;
  /** The points, in SVG coordinates. */
  points: ReadonlyArray<readonly [number, number]>;
}

const round = (n: number) => Math.round(n * 10) / 10;

/**
 * Builds a smooth curve through the values (Catmull-Rom converted to cubic Béziers), scaled to fit width x height.
 * Needs at least two values; a flat series is drawn in the middle instead of dividing by zero.
 */
export function chartGeometry(values: readonly number[], width: number, height: number, pad = 12): ChartGeometry | null {
  if (values.length < 2) return null;

  const min = Math.min(...values);
  const max = Math.max(...values);
  const span = max - min;
  const stepX = (width - 2 * pad) / (values.length - 1);
  const points = values.map((value, index): readonly [number, number] => [
    round(pad + index * stepX),
    round(span === 0 ? height / 2 : height - pad - ((value - min) / span) * (height - 2 * pad)),
  ]);

  let line = `M ${points[0][0]} ${points[0][1]}`;
  for (let i = 0; i < points.length - 1; i++) {
    const p0 = points[i - 1] ?? points[i];
    const p1 = points[i];
    const p2 = points[i + 1];
    const p3 = points[i + 2] ?? p2;
    const c1x = round(p1[0] + (p2[0] - p0[0]) / 6);
    const c1y = round(p1[1] + (p2[1] - p0[1]) / 6);
    const c2x = round(p2[0] - (p3[0] - p1[0]) / 6);
    const c2y = round(p2[1] - (p3[1] - p1[1]) / 6);
    line += ` C ${c1x} ${c1y}, ${c2x} ${c2y}, ${p2[0]} ${p2[1]}`;
  }

  const last = points[points.length - 1];
  const first = points[0];

  return { line, area: `${line} L ${last[0]} ${height} L ${first[0]} ${height} Z`, points };
}
