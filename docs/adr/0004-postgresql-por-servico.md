# ADR-0004 — PostgreSQL, um banco por serviço

Status: Aceita
Data: 2026-11-16

## Contexto

Os dados de lançamentos exigem transações e integridade (valores monetários, Outbox atômico). O consolidado precisa de upserts atômicos, agregações por data e uma tabela de idempotência. Os contextos devem ser independentes (mapa de contexto).

## Decisão

**PostgreSQL**, com uma instância (e um esquema) **por serviço**. Nenhum serviço lê o banco do outro. Valores monetários em `numeric(18,2)`. Migrações versionadas com EF Core.

## Alternativas consideradas

| Alternativa | Motivo do descarte |
|---|---|
| Banco compartilhado | Quebra o isolamento entre contextos e o RNF-01. |
| NoSQL (ex.: MongoDB) | Sem ganho para o modelo; perde transações simples e agregações em SQL. |
| SQL Server | Licenciamento e peso maiores sem benefício para o caso. |

## Consequências

- (+) Maturidade, transações, índices adequados (`merchantId`, data), ecossistema .NET maduro.
- (+) Equivalente gerenciado multi-AZ na nuvem; alta disponibilidade local na camada 2 com CloudNativePG.
- (−) Dois bancos para operar; mitigado pelo Helm e pela automação.
