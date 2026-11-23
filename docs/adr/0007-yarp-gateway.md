# ADR-0007 — YARP como gateway

Status: Aceita
Data: 2026-11-16

## Contexto

É necessário um ponto único de entrada para roteamento, validação de JWT, limitação de taxa e propagação do *correlation ID*, sem acoplar o frontend aos endereços internos dos serviços.

## Decisão

**YARP** (Yet Another Reverse Proxy), da Microsoft, hospedado como aplicação .NET. Rotas: `/v1/entries` → Lançamentos; `/v1/balances` → Consolidação. Funções: validação de JWT, limitação de taxa (limite **acima** do pico legítimo, pois uma resposta 429 conta como perda — premissa P1), cabeçalhos de segurança, correlation ID e telemetria. Os serviços **não** confiam no Gateway: validam o token novamente.

## Alternativas consideradas

| Alternativa | Motivo do descarte |
|---|---|
| Kong | Mais pesado, com banco próprio; excessivo para o escopo. |
| NGINX/Envoy puro | Menos integrado à validação OIDC e à telemetria em .NET; o NGINX permanece apenas como Ingress. |
| Sem gateway | Expõe os serviços diretamente e espalha preocupações transversais. |

## Consequências

- (+) Mesma linguagem e telemetria do restante da solução; configuração versionada; leve.
- (−) Menos recursos prontos que um gateway comercial (portal, planos de consumo). Aceitável no escopo.
