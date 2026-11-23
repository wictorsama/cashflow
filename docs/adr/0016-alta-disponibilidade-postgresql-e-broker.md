# ADR-0016 — Alta disponibilidade: CloudNativePG para o PostgreSQL e *quorum queues* no RabbitMQ

Status: Aceita
Data: 2026-11-22

## Contexto

O teste de perda de nó mostrou que, com uma instância única de banco e de broker, perder o nó que os hospeda interrompia o registro. Precisamos de réplica e *failover* automáticos, localmente e em nuvem.

## Opções consideradas

| | Opção | A favor | Contra |
|---|---|---|---|
| **A** | CloudNativePG (operador Kubernetes): primário + réplica síncrona, *failover* automático | Nativo do Kubernetes; um cluster por serviço; réplica síncrona sem perda no *failover*; o mesmo operador vale local e em nuvem | Mais uma peça (o operador), que também precisa ser replicado (o teste achou isso) |
| **B** | Patroni (ou operador Zalando) | Muito usado e testado | Mais peças de coordenação (etcd/Consul ou a API do Kubernetes); mais configuração para o mesmo resultado |
| **C** | Banco gerenciado multi-AZ (RDS, Cloud SQL, Azure Database) | Menos operação; *failover* gerenciado | Não roda no ambiente local; é o equivalente na nuvem (já suportado por `external.postgres`) |
| **D** | Instância única com *backup* | Simples | Tempo de recuperação alto; foi exatamente a limitação medida |

## Decisão e por quê

**A (CloudNativePG) localmente e na nuvem que não tenha banco gerenciado; C quando houver.** Para o broker: **filas *quorum*** (Raft) em 3 nós, em vez de filas clássicas espelhadas (descontinuadas no RabbitMQ 4). Medido: o *failover* do banco acontece sozinho, e a fila mantém 3 réplicas idênticas após perder e recuperar um nó. O operador precisou de 2 réplicas, senão não havia quem promovesse a réplica quando o nó dele caía.

## Quando reavaliar

Ao ir para a nuvem, preferir o banco gerenciado multi-AZ (menos operação) e manter o RabbitMQ em cluster ou gerenciado (Amazon MQ).
