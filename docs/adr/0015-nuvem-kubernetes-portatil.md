# ADR-0015 — Nuvem: Kubernetes portátil, com a AWS como referência de custo

Status: Aceita
Data: 2026-11-22

## Contexto

O enunciado pede arquitetura que possa subir em nuvem, sem fixar qual. Precisamos decidir quanto do sistema se amarra a um provedor.

## Opções consideradas

| | Opção | A favor | Contra |
|---|---|---|---|
| **A** | Kubernetes gerenciado na AWS (EKS) com serviços gerenciados da AWS (RDS, Amazon MQ, ElastiCache) | Mais completo em serviços; a estimativa de custo foi feita aqui; Amazon MQ oferece RabbitMQ gerenciado | Amarra operações ao provedor se usar serviços proprietários |
| **B** | Azure (AKS, Azure Database for PostgreSQL, Azure Cache for Redis) | Integração com Entra ID e ecossistema Microsoft | Sem RabbitMQ gerenciado nativo (o equivalente nativo, Service Bus, é outro protocolo e outro modelo): o broker seria operado no cluster ou contratado fora |
| **C** | Google Cloud (GKE, Cloud SQL, Memorystore) | GKE maduro; bons serviços de dados | Também sem RabbitMQ gerenciado nativo (o equivalente, Pub/Sub, muda o modelo) |
| **D** | Contêineres sem Kubernetes (ECS/Fargate, Cloud Run, Container Apps) | Operação mais simples | Perde a portabilidade do chart, as políticas de rede, os operadores (CloudNativePG) e a mesma topologia do ambiente local |

## Decisão e por quê

**Kubernetes portátil (A, B ou C com o mesmo chart), referência de custo na AWS e infraestrutura como código (Terraform) para o Google Cloud**, que é onde se espera que o sistema seja avaliado ([infra/terraform/gcp](../../infra/terraform/gcp/README.md)). O sistema é entregue como imagens OCI + um chart Helm; o que muda por nuvem é só o `values` (onde estão o banco, o broker, o cache e o Ingress). O chart já tem as chaves `external.*` e `ingress.*` para isso. A estimativa de custo usa a AWS porque é onde os preços unitários foram verificados, **não porque o sistema dependa dela**. A opção D foi descartada para manter a mesma topologia local e em nuvem.

## Quando reavaliar

A escolha final do provedor é de negócio (contratos, nuvem já usada pela empresa, dados residentes). Ver [21 — Nuvem](../21-nuvem.md) para o que está pronto e o que falta.
