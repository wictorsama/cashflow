import { redirect } from "next/navigation";
import { auth } from "@/auth";
import { isAuditor } from "@/lib/roles";

/** The merchant screens need a merchant of their own: an auditor is sent to the audit screen instead. */
export async function keepMerchantsHere(): Promise<void> {
  const session = await auth();
  if (session && isAuditor(session)) redirect("/auditoria");
}
