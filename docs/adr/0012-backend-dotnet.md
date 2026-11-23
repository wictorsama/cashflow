# ADR-0012 — Backend em .NET 10 (C#)

Status: Aceita
Data: 2026-11-22

## Contexto

Os dois serviços e o gateway precisam de tipagem forte para o domínio financeiro (valores, datas, estados), bom suporte a transações, mensageria, OpenTelemetry e testes com contêineres reais. O desafio não impõe linguagem.

## Opções consideradas

| | Opção | A favor | Contra |
|---|---|---|---|
| **A** | .NET 10 / C# (ASP.NET Core, EF Core, YARP) | Tipagem forte e *records* para o domínio; EF Core e Npgsql maduros; YARP, OpenTelemetry e Testcontainers com suporte de primeira; um único runtime para serviços, gateway e testes; desempenho sobrando para 50 req/s | Imagens maiores que Go; comunidade de mensageria com mudanças de licença (evitadas com cliente oficial) |
| **B** | Java 21 / Spring Boot | Ecossistema vastíssimo; Spring Data e Spring Cloud Gateway equivalentes | Mais memória e tempo de partida; mais configuração para o mesmo resultado; sem vantagem decisiva aqui |
| **C** | Node.js / NestJS (TypeScript) | Mesma linguagem do frontend; partida rápida | Tipagem estrutural, não nominal, é mais fraca para dinheiro e datas; `Number` não é decimal; ORMs menos maduros para transações e *outbox* |
| **D** | Go | Binários pequenos, ótimo desempenho e consumo | Mais código para o que um ORM e um *framework* dão pronto; modelagem de domínio mais verbosa; ecossistema OIDC/Outbox mais manual |

## Decisão e por quê

**A (.NET 10).** Java e .NET são tecnicamente equivalentes para este caso; a decisão pesa a maturidade das peças que o projeto usa (EF Core, YARP, OpenTelemetry), a tipagem nominal para dinheiro e datas, e a rapidez de ter serviços, gateway e testes no mesmo toolchain. Não é uma escolha de desempenho: qualquer das quatro sustentaria a carga.

## Quando reavaliar

Se a equipe que vai operar o sistema tiver outra plataforma consolidada (por exemplo, uma base grande de engenheiros Java), trocar tem custo de reescrita, mas a arquitetura (eventos, Outbox, idempotência, contratos) não depende da linguagem.
