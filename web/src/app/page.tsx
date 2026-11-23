import { redirect } from "next/navigation";
import { auth } from "@/auth";
import { isAuditor } from "@/lib/roles";

export const dynamic = "force-dynamic";

export default async function Home() {
  const session = await auth();
  if (!session?.accessToken || session.error) redirect("/entrar");
  redirect(isAuditor(session) ? "/auditoria" : "/saldo");
}
