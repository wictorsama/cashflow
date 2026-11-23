/**
 * Who the signed-in person is, from what the token says. A merchant has a merchant of its own; an auditor has none and a
 * list of merchants assigned to it. The API enforces this again on every call: this only decides what the screens show.
 */
export interface RoleInfo {
  merchantId?: string;
  auditMerchants?: string[];
}

export const isAuditor = (session: RoleInfo): boolean => !session.merchantId && (session.auditMerchants?.length ?? 0) > 0;

/** The merchant an auditor asked to see, but only if it is one of the assigned ones; otherwise the first assigned. */
export function chosenMerchant(requested: string | undefined, assigned: string[]): string | undefined {
  if (requested && assigned.includes(requested)) return requested;

  return assigned[0];
}

export const shortId = (id: string): string => id.slice(0, 8);
