# ADR-0008 — Helm e kind para implantação, com entrega em camadas

Status: Aceita
Data: 2026-11-16

## Contexto

O projeto deve subir em contêineres e Kubernetes de forma automática, com redundância, executável localmente por qualquer desenvolvedor e pensado para a nuvem (RNF-12, RNF-13).

## Decisão

- **kind** (Kubernetes in Docker) com 4 nós (1 *control-plane* e 3 *workers*) como cluster local. Reproduzível também no GitHub Actions. Com mais de um nó é possível demonstrar distribuição de réplicas e sobrevivência à perda de um nó.
- **Helm** como empacotamento: um chart por serviço, com `values-local.yaml` e `values-cloud.yaml`. Dependências instaladas por charts oficiais ou operadores.
- **Compose** mantido para o ciclo de desenvolvimento (uma instância de cada).
- **Script único** (`up.ps1`/`up.sh`) que cria o cluster, constrói e carrega as imagens, instala dependências e charts e aguarda a prontidão.
- **Entrega em camadas:** camada 1 com serviços e Gateway redundantes (≥ 2 réplicas, *anti-affinity*, *probes*, *PodDisruptionBudget*, *rolling update*; HPA no chart, desligado localmente) e dependências em instância única; camada 2 (entregue, ver [14](../14-alta-disponibilidade.md)) com PostgreSQL (CloudNativePG) e RabbitMQ (3 nós, *quorum queues*) replicados; Redis fica único por ser só cache.

## Alternativas consideradas

| Alternativa | Motivo do descarte |
|---|---|
| Kustomize | Bom para sobreposições, mas as dependências já vêm em Helm e o Helm é o padrão de mercado. |
| minikube / Kubernetes do Docker Desktop | Menos reproduzíveis em CI e com topologia multi-nó menos direta. |
| Somente Compose | Não demonstra redundância, autocura nem portabilidade para nuvem. |

## Consequências

- (+) O mesmo chart serve local e nuvem; o pipeline prova o ambiente em um cluster efêmero.
- (+) Entregar a camada 1 primeiro prioriza os serviços; a camada 2 fica documentada como evolução.
- (−) Na camada 1, as dependências em instância única são pontos únicos de falha localmente.
- (−) Exige `kind`, `helm` e Docker na máquina; o script verifica os pré-requisitos e orienta a instalação.
