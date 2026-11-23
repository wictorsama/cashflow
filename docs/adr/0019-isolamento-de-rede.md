# ADR-0019 — Isolamento de rede com NetworkPolicy (entrada negada por padrão)

Status: Aceita
Data: 2026-11-22

## Contexto

Sem regras, qualquer pod conversa com qualquer pod: um pod comprometido alcançaria banco, broker e cache.

## Opções consideradas

| | Opção | A favor | Contra |
|---|---|---|---|
| **A** | Nada (confiar na rede interna) | Zero esforço | Um pod comprometido alcança tudo |
| **B** | NetworkPolicy do Kubernetes: entrada negada por padrão e caminhos abertos explicitamente | Nativa, sem componente novo; verificada com um pod intruso | Só entrada; não cifra nem autentica; precisa de plugin de rede que a aplique |
| **C** | Malha de serviço com mTLS (Istio/Linkerd) | Cifra e autentica serviço a serviço, com identidade | Muito mais peças e consumo; excessivo para dois serviços; fica como evolução |
| **D** | Segurança só nos grupos de segurança da nuvem | Já existe na nuvem | Não vale no ambiente local; granularidade por nó, não por pod |

## Decisão e por quê

**B agora, C como evolução.** Resolve o risco principal (alcance lateral) com custo mínimo. A saída (*egress*) e o mTLS ficam documentados como próximos passos. Ver [17](../17-isolamento-de-rede.md).

## Quando reavaliar

Ao exigir cifra entre serviços (conformidade) ou ao crescer o número de serviços, adotar a malha com mTLS.
