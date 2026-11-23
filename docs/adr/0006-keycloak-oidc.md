# ADR-0006 — Keycloak (OIDC/JWT) para identidade

Status: Aceita
Data: 2026-11-16

## Contexto

É preciso autenticar comerciantes e auditores, autorizar por papel e escopo, e proteger a integração entre serviços (diferencial do desafio). Construir identidade própria é risco de segurança sem valor de negócio (capacidade genérica).

## Decisão

**Keycloak** como provedor OIDC.

- Usuários: *authorization code + PKCE*, conduzido pelo servidor do Next.js; o token fica na sessão do servidor.
- Máquina a máquina: *client credentials* com escopos mínimos (`entries:write`, `entries:read`, `balances:read`, `balances:rebuild`).
- Papéis: `merchant`, `auditor`, `service`. O `merchantId` é um *claim* do token.
- O Gateway valida o JWT e **cada serviço valida novamente** (defesa em profundidade).
- Localmente, o *realm* é importado de um arquivo versionado, sem passos manuais.

## Alternativas consideradas

| Alternativa | Motivo do descarte |
|---|---|
| ASP.NET Identity próprio | Reimplementa um padrão; mais risco, menos recursos. |
| Autenticação sem padrão (chaves estáticas) | Não demonstra boas práticas de segurança de integração. |
| Serviço gerenciado (Auth0, Entra ID) | Bom na nuvem; dificulta a execução local autônoma. |

## Consequências

- (+) Padrão de mercado, com papéis e escopos prontos; troca por serviço gerenciado na nuvem apenas com configuração.
- (−) Um contêiner pesado a mais na execução local.
- (−) Segredos e usuários de teste do *realm* local devem ser claramente de desenvolvimento e nunca reutilizados.
