import { describe, expect, it } from "vitest";
import { addDays, formatDate, formatTime, isIsoDate, parseAmount, todayInSaoPaulo } from "./format";

describe("formatTime", () => {
  it("shows the time of day in the Sao Paulo timezone", () => {
    expect(formatTime("2026-11-23T13:42:00Z")).toBe("10:42");
    expect(formatTime("2026-11-23T02:05:00Z")).toBe("23:05");
  });
});

describe("parseAmount", () => {
  it.each([
    ["10", 10],
    ["10,5", 10.5],
    ["1234,56", 1234.56],
    ["1.234,56", 1234.56],
    ["1234.56", 1234.56],
    ["  99,90  ", 99.9],
  ])("accepts %s", (input, expected) => {
    expect(parseAmount(input)).toBe(expected);
  });

  it.each(["", "0", "0,00", "-5", "abc", "10,555", "1,2,3", "10 reais", "1e3"])("rejects %j", (input) => {
    expect(parseAmount(input)).toBeNull();
  });
});

describe("todayInSaoPaulo", () => {
  it("is still the previous day just after UTC midnight", () => {
    expect(todayInSaoPaulo(new Date("2026-11-17T01:30:00Z"))).toBe("2026-11-16");
  });

  it("is the same day once Sao Paulo passes midnight", () => {
    expect(todayInSaoPaulo(new Date("2026-11-17T03:00:00Z"))).toBe("2026-11-17");
  });
});

describe("dates", () => {
  it("adds days across month and year boundaries", () => {
    expect(addDays("2026-03-01", -1)).toBe("2026-02-28");
    expect(addDays("2026-12-31", 1)).toBe("2027-01-01");
  });

  it("validates ISO dates", () => {
    expect(isIsoDate("2026-11-17")).toBe(true);
    expect(isIsoDate("2026-13-40")).toBe(false);
    expect(isIsoDate("17/11/2026")).toBe(false);
    expect(isIsoDate(undefined)).toBe(false);
  });

  it("formats as day/month/year", () => {
    expect(formatDate("2026-11-17")).toBe("17/11/2026");
  });
});
