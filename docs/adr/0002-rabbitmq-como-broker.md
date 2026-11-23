# ADR-0002 — RabbitMQ como broker (e não Kafka), com cliente oficial `RabbitMQ.Client`

Status: Aceita (implementada: filas *quorum* em 3 nós, ver [ADR-0016](0016-alta-disponibilidade-postgresql-e-broker.md))
Data: 2026-11-16, revisada em 2026-11-22

## Contexto

Os lançamentos precisam chegar à Consolidação de forma **durável e assíncrona**, de modo que a Consolidação fora do ar nunca impeça o registro (RNF-01). O desafio pede 50 req/s de leitura na consolidação, e a escrita medida é de ~10 a 50 por segundo: **volume baixo**, um produtor e um consumidor. A ordem de chegada dos eventos não importa (a soma é comutativa). Precisamos de confirmação do publicador, reentrega e fila de mensagens com falha (DLQ).

## Opções consideradas

| | Opção | A favor | Contra |
|---|---|---|---|
| **A** | **RabbitMQ** (*topic exchange*, fila durável, DLX) | Modelo de **fila** é o que o problema pede (entregar cada evento a um consumidor, reentregar, DLQ); simples de operar para o volume; filas *quorum* dão replicação (Raft); roda igual local e em nuvem; cliente oficial, sem licença comercial | Não guarda histórico para reler (uma mensagem consumida sai da fila); escala de vazão menor que o Kafka |
| **B** | **Apache Kafka** | Log distribuído com **retenção e releitura**; vazão enorme; ótimo para *streams* e vários consumidores independentes | **Excessivo** para ~50 eventos/s e um consumidor: partições, *rebalancing*, *offsets*, Zookeeper/KRaft, mais memória e mais operação; ordem por partição não é necessária aqui; o custo de aprender e operar não retorna |
| **C** | Fila gerenciada do provedor (SQS/SNS, Service Bus, Pub/Sub) | Sem operar broker; escala sozinha | Cada nuvem é um modelo e um SDK diferentes; **não roda no ambiente local**; amarra o código ao provedor (fica como equivalente na nuvem, atrás da mesma interface do publicador) |
| **D** | Sem broker: Consolidação lê o banco de Lançamentos (*polling*) ou chamada síncrona | Nada a operar | Acopla os dois bancos ou os dois serviços: viola o isolamento de falha (RNF-01) |

## Decisão e por quê

**A (RabbitMQ).** O problema é **entrega de mensagens**, não *streaming de eventos*: um produtor, um consumidor, volume baixo, ordem irrelevante, reentrega e DLQ necessárias. O RabbitMQ faz isso com a menor operação. O Kafka seria a escolha certa se houvesse **muitos consumidores independentes, necessidade de reler o histórico, ou volume de ordens de grandeza maior**; nenhuma dessas é verdade hoje. A necessidade de reler o histórico, que o Kafka daria, é atendida de outra forma: o **registro de eventos** na Consolidação permite reprocessar o saldo ([ADR-0010](0010-reprocessamento-por-registro-de-eventos.md)).

Detalhes: *exchange* `entries.events` (*topic*), fila `consolidation.entries` com *dead-letter exchange*, **filas *quorum***, mensagens persistentes, **confirmação do publicador** e *ack* manual. Cliente oficial **`RabbitMQ.Client`** com camada fina própria, sem biblioteca de terceiros (a mais usada no ecossistema .NET passou a ter licença comercial). A publicação é pelo **Outbox** ([ADR-0003](0003-padrao-outbox.md)): nenhum evento se perde se o broker cair.

## Consequências

- (+) Simplicidade operacional, sem custo de licença, domínio explícito do padrão; testado com PostgreSQL e RabbitMQ reais.
- (+) A ordem de entrega não importa (soma comutativa e consumidor idempotente), então não são necessárias partições ordenadas.
- (−) Código de infraestrutura próprio (publicação, consumo), coberto por testes de integração e de topologia.
- (−) Sem releitura do histórico no broker (compensada pelo registro de eventos).

## Quando reavaliar

Trocar por Kafka se aparecerem **vários consumidores independentes** (relatórios, antifraude, *data lake*), a necessidade de **reprocessar eventos antigos pelo broker**, ou volume que exceda a capacidade do RabbitMQ. Como o publicador e o consumidor estão atrás de interfaces finas, a troca fica localizada.
