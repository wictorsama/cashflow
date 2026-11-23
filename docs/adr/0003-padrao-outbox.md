# ADR-0003 — Padrão Outbox para publicação de eventos

Status: Aceita
Data: 2026-11-16

## Contexto

Ao registrar um lançamento, é preciso gravar no banco **e** publicar um evento. Gravar e depois publicar diretamente deixa uma janela em que o banco confirma e o broker falha (evento perdido), ou o contrário (evento sem dado). Não existe transação distribuída entre PostgreSQL e RabbitMQ.

## Decisão

O serviço de Lançamentos grava o `Entry` e a mensagem na tabela `outbox` **na mesma transação**. Um *relay* em segundo plano lê as mensagens pendentes, publica no RabbitMQ com confirmação e marca como publicadas. A entrega é *at least once*; o consumidor é idempotente (ver ADR-0010 e a seção 6 da arquitetura).

## Alternativas consideradas

| Alternativa | Motivo do descarte |
|---|---|
| Publicar direto após o commit | Perde evento se o broker falhar. |
| Publicar antes do commit | Pode publicar evento de lançamento que não foi gravado. |
| Transação distribuída (2PC) | Não suportada de forma prática entre PostgreSQL e RabbitMQ. |
| Captura de mudanças (CDC, ex.: Debezium) | Mais uma peça operacional; reservado como evolução. |

## Consequências

- (+) Nenhum lançamento aceito deixa de gerar evento; a escrita não depende do broker (RNF-01, RNF-06).
- (−) Latência adicional pequena entre o commit e a publicação.
- (−) Duplicatas possíveis; tratadas por idempotência no consumidor.
- (−) A tabela `outbox` precisa de limpeza periódica das mensagens já publicadas.
