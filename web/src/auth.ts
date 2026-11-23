import NextAuth from "next-auth";
import type { JWT } from "next-auth/jwt";

/**
 * Sign-in with Keycloak using authorization code + PKCE (public client, no secret in the browser or in this server).
 *
 * Two addresses for the same Keycloak: the browser must reach it at its public address (also the token issuer),
 * while this server, inside the cluster, reaches it by its service name. Hence the explicit endpoints below.
 *
 * The tokens live in an encrypted, HttpOnly session cookie: scripts in the page never see them, and every call to the
 * API is made by this server, never by the browser.
 */
const issuer = process.env.KEYCLOAK_ISSUER ?? "http://localhost:8082/realms/cashflow";
const internal = process.env.KEYCLOAK_INTERNAL_URL ?? issuer;
const clientId = process.env.KEYCLOAK_CLIENT_ID ?? "cashflow-web";

const endpoint = (base: string, name: string) => `${base}/protocol/openid-connect/${name}`;

/** Renews the access token (valid 5 minutes) shortly before it expires. */
async function refresh(token: JWT): Promise<JWT> {
  try {
    const response = await fetch(endpoint(internal, "token"), {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams({
        grant_type: "refresh_token",
        client_id: clientId,
        refresh_token: String(token.refreshToken),
      }),
    });
    const tokens = await response.json();
    if (!response.ok) throw new Error(tokens.error ?? `HTTP ${response.status}`);

    return {
      ...token,
      accessToken: tokens.access_token,
      refreshToken: tokens.refresh_token ?? token.refreshToken,
      idToken: tokens.id_token ?? token.idToken,
      expiresAt: Math.floor(Date.now() / 1000) + tokens.expires_in,
      error: undefined,
    };
  } catch {
    return { ...token, error: "RefreshTokenError" };
  }
}

/** Reads a claim from the access token only to show it on screen; the API validates the token on every call. */
/** Reads a claim that holds a list (for example the merchants assigned to an auditor). */
export function claimListOf(accessToken: string | undefined, name: string): string[] {
  if (!accessToken) return [];
  try {
    const payload = JSON.parse(Buffer.from(accessToken.split(".")[1], "base64url").toString("utf8"));
    const value = payload[name];
    if (Array.isArray(value)) return value.filter((v): v is string => typeof v === "string");

    return typeof value === "string" ? [value] : [];
  } catch {
    return [];
  }
}

export function claimOf(accessToken: string | undefined, name: string): string | undefined {
  if (!accessToken) return undefined;
  try {
    const payload = JSON.parse(Buffer.from(accessToken.split(".")[1], "base64url").toString("utf8"));
    return typeof payload[name] === "string" ? payload[name] : undefined;
  } catch {
    return undefined;
  }
}

export const { handlers, auth, signIn, signOut } = NextAuth({
  trustHost: true,
  session: { strategy: "jwt" },
  pages: { signIn: "/entrar" },
  providers: [
    {
      id: "keycloak",
      name: "Keycloak",
      type: "oidc",
      issuer,
      clientId,
      client: { token_endpoint_auth_method: "none" },
      checks: ["pkce", "state"],
      authorization: {
        url: endpoint(issuer, "auth"),
        params: { scope: "openid entries:write entries:read balances:read entries:audit balances:audit assistant:draft" },
      },
      token: endpoint(internal, "token"),
      userinfo: endpoint(internal, "userinfo"),
      jwks_endpoint: endpoint(internal, "certs"),
    },
  ],
  callbacks: {
    async jwt({ token, account }) {
      if (account) {
        // First sign-in: keep what the API calls need.
        return {
          ...token,
          accessToken: account.access_token,
          refreshToken: account.refresh_token,
          idToken: account.id_token,
          expiresAt: account.expires_at,
        };
      }
      const expiresAt = typeof token.expiresAt === "number" ? token.expiresAt : 0;
      if (Date.now() / 1000 < expiresAt - 30) return token;

      return refresh(token);
    },
    async session({ session, token }) {
      session.accessToken = token.accessToken as string | undefined;
      session.idToken = token.idToken as string | undefined;
      session.error = token.error as string | undefined;
      session.merchantId = claimOf(session.accessToken, "merchant_id");
      session.auditMerchants = claimListOf(session.accessToken, "audit_merchants");

      return session;
    },
  },
});

export const keycloakLogoutUrl = (idToken: string | undefined, postLogoutRedirect: string) => {
  const url = new URL(endpoint(issuer, "logout"));
  url.searchParams.set("client_id", clientId);
  url.searchParams.set("post_logout_redirect_uri", postLogoutRedirect);
  if (idToken) url.searchParams.set("id_token_hint", idToken);

  return url.toString();
};
