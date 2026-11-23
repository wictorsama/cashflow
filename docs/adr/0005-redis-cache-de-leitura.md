# ADR-0005 — Redis como cache de leitura com *fallback*

Status: Aceita
Data: 2026-11-16, revisada em 2026-11-22

## Contexto

A Consolidação recebe picos de 50 req/s, em sua maioria leituras do saldo do **mesmo dia**. O cache reduz carga e latência, mas **não pode ser ponto único de falha**: se cair, a leitura continua pelo banco.

## Opções consideradas

| | Opção | A favor | Contra |
|---|---|---|---|
| **A** | **Redis** (chave por comerciante e dia, TTL curto, versão por comerciante) | Compartilhado entre réplicas; invalidação simples; padrão de mercado; equivalente gerenciado em toda nuvem | Mais uma peça; defasagem até a invalidação |
| **B** | Cache em memória do processo | Sem peça nova; rápido | **Inconsistente entre réplicas**; invalidar o dia afetado em todas é difícil |
| **C** | Sem cache (o banco responde) | Nada a operar | Funciona na carga atual, mas sem margem para o teste de estresse nem para crescimento |
| **D** | Memcached ou saldo materializado em outra tabela | Memcached é simples; a tabela de saldo já existe | Memcached não ganha nada sobre o Redis aqui; o saldo materializado **já é** a tabela de leitura, e o cache evita o banco nas leituras repetidas |

## Decisão e por quê

**A (Redis).** É a única opção que dá cache **compartilhado e invalidável** entre réplicas sem acoplar os serviços. O consumidor **invalida** a chave do dia ao atualizar o saldo; se o Redis estiver fora, o serviço **cai para o PostgreSQL** sem erro ao cliente (a falha do cache degrada o desempenho, não a disponibilidade; há teste). Proteção contra *cache stampede* por TTL curto e chamada única por chave.

Medido: o cache serviu cerca de 93% das leituras na carga de 50 req/s, e o banco quase não sentiu.

**Por isso o Redis fica em instância única** (sem réplica nem *Sentinel*): protegê-lo custaria mais peças do que a perda dele custa (milissegundos de latência, não dados).

## Consequências

- (+) Latência baixa e folga de capacidade (RNF-02, RNF-03).
- (+) Falha do cache não derruba o serviço.
- (−) Defasagem possível até a invalidação; aceitável dentro da meta de frescor (RNF-04).

## Quando reavaliar

Se o banco passar a sentir as leituras mesmo com o cache, escalar réplicas de leitura do banco antes de complicar o cache. Em nuvem, usar o Redis gerenciado (já suportado por `external.redis`).
