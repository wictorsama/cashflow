# 21 — Nuvem: AWS, Azure ou GCP? O que está pronto e o que falta

**Resposta curta:** o sistema é **portátil entre as três** (é Kubernetes + um chart Helm + imagens OCI + OIDC padrão), mas **nunca foi implantado em nenhuma nuvem**. Há infraestrutura como código (Terraform) **para o Google Cloud**, validada e nunca aplicada, e não há automação própria de certificados (usa cert-manager). Está **preparado no desenho, não validado na prática**. Esta página diz o que cada afirmação significa.

## Alvo provável: Google Cloud (GCP)

Quem vai avaliar deve usar o **Google Cloud**, então o código de infraestrutura é para ele: [infra/terraform/gcp](../infra/terraform/gcp/README.md) cria a rede privada, o **GKE Autopilot**, dois **Cloud SQL para PostgreSQL** (um por serviço, alta disponibilidade regional, IP privado, TLS), o **Memorystore (Redis)**, o **Artifact Registry** e guarda **todas as senhas geradas** no **Secret Manager**. O chart tem um arquivo de valores próprio, [values-gcp.yaml](../deploy/helm/cashflow/values-gcp.yaml).

| Ponto específico do GCP | Como está tratado |
|---|---|
| **Não há RabbitMQ gerenciado** | O RabbitMQ roda **no cluster** (3 nós, filas *quorum*), pelo chart, como no ambiente local; o Terraform não cria broker |
| Banco | Cloud SQL em vez do CloudNativePG (`external.postgres`); o operador do banco **não** é instalado |
| Cache | Memorystore (`external.redis`), sem criptografia em trânsito dentro da rede privada (limite documentado) |
| Porta de entrada | NGINX Ingress + cert-manager, instalados por `helm` (passos no README do Terraform) |
| Senhas | Geradas pelo Terraform e lidas do Secret Manager para um arquivo que **não** vai para o Git |
| Estado | Validado com `terraform validate` e `fmt` (e no CI); **nunca aplicado**, sem conta de nuvem; **custo no GCP não estimado** |

## O que está pronto

| Pronto | Evidência |
|---|---|
| **Imagens OCI** dos 5 serviços (Lançamentos, Consolidação, Assistente, Gateway, Web), não-root, sistema de arquivos somente leitura | Dockerfiles; publicação com proveniência e SBOM ([09](09-ci-cd.md)) |
| **Chart Helm sem nada fixo de um provedor** | `values.yaml` neutro; `values-local.yaml` só para o kind |
| **Dependências trocáveis por serviços gerenciados** | `external.postgres`, `external.rabbitmq`, `external.redis` ([values](../deploy/helm/cashflow/values.yaml)); renderizado no CI com [values-cloud-example.yaml](../deploy/helm/cashflow/values-cloud-example.yaml) |
| **Entrada pública por Ingress com TLS** | `ingress.*` no chart; hosts, classe e segredo de TLS por `values` |
| **Identidade padrão OIDC/JWT** | Os serviços só pedem emissor, audiência e escopos; trocar o Keycloak por outro provedor OIDC não muda código (ver abaixo) |
| **Operação em produção**: réplicas, anti-afinidade, probes, PodDisruptionBudget, HPA, atualização sem indisponibilidade, política de rede | Verificado no cluster local, incluindo perda de pod e de nó |
| **Configuração por variáveis de ambiente**, segredos fora da imagem | Chart; nenhuma credencial no repositório |

## O que NÃO está pronto (dito sem rodeios)

| Lacuna | Consequência | Como fechar |
|---|---|---|
| **Nunca implantado em uma nuvem real** | Os valores `external.*` e o Ingress só foram **renderizados e validados contra o esquema do Kubernetes**, não aplicados | Um ambiente de piloto (ver "Caminho") |
| **Terraform só para o Google Cloud, e nunca aplicado** | Está validado (`validate`, `fmt`, CI), mas a primeira aplicação real pode pedir ajustes; AWS e Azure não têm Terraform | Aplicar em um projeto de piloto; copiar a estrutura para outro provedor se preciso |
| **Certificados e segredos** | Não há cert-manager nem integração com gerenciador de segredos (External Secrets) | Instalar cert-manager e o operador de segredos do provedor |
| **Keycloak em modo de desenvolvimento** (banco embutido, uma instância) | Não serve para produção | Keycloak com banco externo e réplicas, **ou** um provedor OIDC gerenciado (abaixo) |
| **Migrações com o usuário da aplicação** (`Database__MigrateOnStartup`) | Exige permissão de DDL na aplicação | Job de migração separado com outro usuário |
| **Réplicas por zona** | A distribuição é por nó (`hostname`), não por zona de disponibilidade | Acrescentar `topology.kubernetes.io/zone` nas restrições |
| **Assinatura de imagens e política de admissão** | Imagens sem assinatura verificada no cluster | cosign e uma política de admissão ([10](10-modelo-de-ameacas.md)) |
| **Testes de carga e de falha em nuvem** | Os números medidos são do cluster local (kind) | Repetir `load-test` e `resilience-test` no piloto |

## Equivalências por provedor

> Nomes e disponibilidade **conforme a documentação de cada provedor na data de uso: confirmar antes de decidir**. O que importa para o sistema está na coluna "o que o sistema exige".

| Peça | O que o sistema exige | AWS | Azure | Google Cloud |
|---|---|---|---|---|
| Kubernetes | versão recente, Ingress, NetworkPolicy aplicada (o plugin de rede precisa aplicá-la) | EKS | AKS | GKE |
| PostgreSQL | 17 (ou compatível), TLS, usuário com DDL | RDS / Aurora PostgreSQL (Multi-AZ) | Azure Database for PostgreSQL | Cloud SQL / AlloyDB |
| RabbitMQ | **filas *quorum*** (RabbitMQ ≥ 3.8; usamos o 4) | **Amazon MQ para RabbitMQ** | **sem serviço RabbitMQ nativo**: rodar no cluster ou contratar fora; *Service Bus* é outro modelo | **sem serviço RabbitMQ nativo**: idem; *Pub/Sub* é outro modelo |
| Redis | 6+, TLS | ElastiCache for Redis | Azure Managed Redis (ou Azure Cache for Redis) | Memorystore for Redis |
| Identidade (OIDC) | emissor, JWKS, escopos, claims `sub`, `merchant_id`, `audit_merchants` | Keycloak, ou Cognito | Keycloak, ou Microsoft Entra ID | Keycloak, ou Identity Platform |
| Balanceador / TLS | Ingress HTTP com TLS | ALB / NLB | Application Gateway / Front Door | Cloud Load Balancing |
| Segredos | Secrets do Kubernetes alimentados de fora | Secrets Manager | Key Vault | Secret Manager |
| Registro de imagens | OCI | ECR (ou GHCR) | ACR (ou GHCR) | Artifact Registry (ou GHCR) |
| Observabilidade | OTLP (traces e métricas) | CloudWatch / Managed Prometheus e Grafana | Azure Monitor / Managed Grafana | Cloud Operations / Managed Prometheus |

**O ponto mais sensível é o RabbitMQ.** Só a AWS tem um serviço gerenciado do próprio RabbitMQ. Em Azure e GCP, o caminho é operar o RabbitMQ no cluster (como no ambiente local, com as filas *quorum* já configuradas) ou trocar o publicador e o consumidor pelo serviço nativo, atrás da mesma interface ([ADR-0002](adr/0002-rabbitmq-como-broker.md)). Isso é uma razão a mais para a **referência de custo estar na AWS** ([ADR-0015](adr/0015-nuvem-kubernetes-portatil.md)).

### Trocar o Keycloak por outro provedor OIDC

Os serviços validam um JWT por **emissor**, **audiência** (`cashflow-api`) e **escopos**, e leem três claims: `sub` (autor), `merchant_id` (comerciante) e `audit_merchants` (atribuição do Auditor). Qualquer provedor que consiga emitir isso serve; o trabalho é configurar o provedor (por exemplo, um *pre-token trigger* no Cognito ou *optional claims* no Entra ID), e não mexer no código. Mudam apenas `auth.issuer` e `auth.metadataAddress` do chart.

## Caminho para um piloto em nuvem (checklist)

1. Escolher o provedor (decisão de negócio: contrato, região e residência de dados).
2. Criar o cluster, as redes, o banco gerenciado e o cache (à mão ou com Terraform).
3. Decidir o broker: Amazon MQ (AWS) ou RabbitMQ no cluster (Azure/GCP).
4. Instalar o controlador de Ingress, o cert-manager e o operador de segredos.
5. Publicar as imagens (a *release* já as envia ao GHCR) e preencher um `values-<nuvem>.yaml` a partir do [exemplo](../deploy/helm/cashflow/values-cloud-example.yaml).
6. Decidir a identidade (Keycloak com banco externo, ou OIDC gerenciado) e ajustar `auth.*`.
7. `helm install`; rodar `smoke-test.ps1` apontando para o Gateway da nuvem.
8. Repetir os testes de carga e de resiliência, e conferir os custos reais contra a [estimativa](12-estimativa-de-custos.md).

## Custo

A estimativa ([12](12-estimativa-de-custos.md)) usa preços da AWS verificados em 17/11/2026, com confiança marcada por item. **Azure e GCP não foram orçados.**
