import { redirect } from "next/navigation";
import { auth } from "@/auth";
import { CashFlowApi } from "@/lib/api-client";

/**
 * The API client for the signed-in merchant. Only server code can call this: the token never reaches the browser.
 * Without a session, or when the session can no longer be renewed, the person goes back to the sign-in page.
 */
export async function apiForCurrentUser(): Promise<CashFlowApi> {
  const session = await auth();
  if (!session?.accessToken || session.error) redirect("/entrar");

  return new CashFlowApi(process.env.GATEWAY_URL ?? "http://localhost:8080", session.accessToken);
}
