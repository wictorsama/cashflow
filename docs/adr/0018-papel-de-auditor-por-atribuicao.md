# ADR-0018 — Papel de Auditor: somente leitura, só os comerciantes atribuídos

Status: Aceita
Data: 2026-11-22

## Contexto

O enunciado e os requisitos preveem um auditor que lê dados de outros comerciantes. Isso quebra a regra central do sistema (cada um só vê o que é seu), então precisa ser estreito e rastreado.

## Opções consideradas

| | Opção | A favor | Contra |
|---|---|---|---|
| **A** | Auditor lê **todos** os comerciantes (premissa original) | Mais simples | Um auditor comprometido expõe a base inteira |
| **B** | Atribuição de comerciantes **no token** (claim `audit_merchants`), cada pedido nomeia um | Estreito; o chamador não altera a atribuição (token assinado); sem consulta extra; trilha exata | Revogar vale só no próximo token (≤ 5 min) |
| **C** | Atribuição consultada em um serviço ou tabela a cada pedido | Revogação imediata | Mais uma dependência no caminho de leitura; mais um ponto de falha |
| **D** | Sem papel: ferramenta separada de relatório | Isola o risco | Duplica dados e regras; foge do requisito |

## Decisão e por quê

**B.** O Auditor vira **somente leitura por construção** (toda escrita exige um comerciante próprio, que ele não tem), vê só o que lhe foi atribuído e cada leitura e recusa são registradas. A premissa do enunciado foi **estreitada de propósito**. Um token que tem `merchant_id` é comerciante e só comerciante. Ver [15](../15-papel-de-auditor.md).

## Quando reavaliar

Se for preciso corte imediato de acesso, passar à opção C ou revogar a sessão no provedor de identidade.
