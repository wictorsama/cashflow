import { describe, expect, it } from "vitest";
import { chartGeometry } from "./chart";

describe("chartGeometry", () => {
  it("needs at least two values", () => {
    expect(chartGeometry([], 100, 50)).toBeNull();
    expect(chartGeometry([10], 100, 50)).toBeNull();
  });

  it("puts the first and last points at the padding and keeps every point inside the box", () => {
    const g = chartGeometry([1160, 980, 1620, 1930, 2410, 2970, 4280.5], 560, 200)!;

    expect(g.points).toHaveLength(7);
    expect(g.points[0][0]).toBe(12);
    expect(g.points[6][0]).toBe(548);
    for (const [x, y] of g.points) {
      expect(x).toBeGreaterThanOrEqual(0);
      expect(x).toBeLessThanOrEqual(560);
      expect(y).toBeGreaterThanOrEqual(0);
      expect(y).toBeLessThanOrEqual(200);
    }
  });

  it("draws a higher value higher up (smaller y)", () => {
    const g = chartGeometry([100, 500], 200, 100)!;

    expect(g.points[1][1]).toBeLessThan(g.points[0][1]);
  });

  it("draws a flat series in the middle instead of dividing by zero", () => {
    const g = chartGeometry([300, 300, 300], 200, 100)!;

    expect(g.points.every(([, y]) => y === 50)).toBe(true);
    expect(g.line).not.toContain("NaN");
  });

  it("closes the area down to the baseline", () => {
    const g = chartGeometry([1, 2, 3], 200, 100)!;

    expect(g.line.startsWith("M ")).toBe(true);
    expect(g.area.startsWith(g.line)).toBe(true);
    expect(g.area.endsWith("Z")).toBe(true);
    expect(g.area).toContain("L 188 100");
  });
});
