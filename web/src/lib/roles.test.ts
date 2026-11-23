import { describe, expect, it } from "vitest";
import { chosenMerchant, isAuditor, shortId } from "./roles";

const A = "11111111-1111-1111-1111-111111111111";
const B = "22222222-2222-2222-2222-222222222222";

describe("isAuditor", () => {
  it("is an auditor when there is no merchant of its own but there are assigned merchants", () => {
    expect(isAuditor({ auditMerchants: [A] })).toBe(true);
  });

  it("is a merchant when the token has a merchant, even if it also carries an assignment", () => {
    expect(isAuditor({ merchantId: A, auditMerchants: [B] })).toBe(false);
  });

  it("is nobody special without a merchant and without an assignment", () => {
    expect(isAuditor({})).toBe(false);
    expect(isAuditor({ auditMerchants: [] })).toBe(false);
  });
});

describe("chosenMerchant", () => {
  it("takes the requested merchant only when it is assigned", () => {
    expect(chosenMerchant(B, [A, B])).toBe(B);
  });

  it("falls back to the first assigned merchant for an unassigned or missing request", () => {
    expect(chosenMerchant("33333333-3333-3333-3333-333333333333", [A, B])).toBe(A);
    expect(chosenMerchant(undefined, [A, B])).toBe(A);
  });

  it("has nothing to choose when no merchant is assigned", () => {
    expect(chosenMerchant(A, [])).toBeUndefined();
  });
});

describe("shortId", () => {
  it("shows the first eight characters", () => {
    expect(shortId(A)).toBe("11111111");
  });
});
