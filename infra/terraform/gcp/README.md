# Infraestrutura no Google Cloud (Terraform)

Cria, em um projeto do Google Cloud, tudo o que o sistema precisa para rodar na nuvem. **Está validado (`terraform validate` e `terraform fmt`) mas nunca foi aplicado**: não havia conta de nuvem. Antes de usar de verdade, leia "O que ainda não foi testado".

## O que é criado

| Recurso | Para quê | Arquivo |
|---|---|---|
| **Rede privada (VPC)**, sub-rede, **Cloud NAT** | Nada do sistema fica exposto à internet, exceto o que o Ingress publica; os nós saem pela NAT | `network.tf` |
| **GKE Autopilot** (nós privados) | O Kubernetes onde roda o chart; o Autopilot aplica a `NetworkPolicy` e cuida dos nós | `gke.tf` |
| **Cloud SQL para PostgreSQL**, **uma instância por serviço**, alta disponibilidade regional | Os dois bancos (Lançamentos e Consolidação), com IP privado, TLS obrigatório e backup com recuperação em ponto no tempo | `sql.tf` |
| **Memorystore (Redis)** | O cache de leitura do saldo | `redis.tf` |
| **Artifact Registry** (opcional) | Um registro privado de imagens | `registry.tf` |
| **Secret Manager** | Guarda **um arquivo de valores do Helm com todas as senhas geradas** e os endereços dos serviços gerenciados | `secrets.tf` |

O **RabbitMQ não é criado aqui**: o Google Cloud não tem RabbitMQ gerenciado. Ele roda **dentro do cluster** (3 nós, filas *quorum*), pelo próprio chart, como no ambiente local ([ADR-0002](../../../docs/adr/0002-rabbitmq-como-broker.md), [21](../../../docs/21-nuvem.md)).

## Como usar

Pré-requisitos: `gcloud` autenticado (`gcloud auth application-default login`), `terraform` 1.6 ou mais novo, `kubectl` e `helm`, e um projeto com **cobrança ativa**.

```bash
cd infra/terraform/gcp
cp terraform.tfvars.example terraform.tfvars   # preencha project_id
terraform init
terraform plan                                  # revise o que será criado (e o custo, no console do Google)
terraform apply
```

Depois, para instalar o sistema no cluster novo:

```bash
# 1. aponta kubectl e helm para o cluster
eval "$(terraform output -raw get_credentials)"

# 2. os valores gerados (endereços dos bancos e do cache, e todas as senhas). Arquivo secreto: não vai para o Git.
eval "$(terraform output -raw helm_values_command)"

# 3. a porta de entrada e o certificado (uma vez por cluster)
helm repo add ingress-nginx https://kubernetes.github.io/ingress-nginx
helm install ingress-nginx ingress-nginx/ingress-nginx -n ingress-nginx --create-namespace
helm repo add jetstack https://charts.jetstack.io
helm install cert-manager jetstack/cert-manager -n cert-manager --create-namespace --set crds.enabled=true
# crie um ClusterIssuer chamado "letsencrypt" (ver a documentação do cert-manager) e aponte o DNS dos três hosts para o IP do ingress-nginx

# 4. o sistema (troque os três hosts em values-gcp.yaml, as linhas "CHANGE ME")
helm upgrade --install cashflow ../../../deploy/helm/cashflow -n cashflow --create-namespace \
  -f ../../../deploy/helm/cashflow/values-gcp.yaml -f values-gcp-generated.yaml \
  --set image.tag=0.1.0 --wait --timeout 15m
```

As imagens vêm do registro público do GitHub (`ghcr.io/wictorsama/cashflow/...`), **publicadas pelo workflow de release quando se cria a tag `v0.1.0`**. Se preferir o registro privado do Google, use a saída `artifact_registry` em `image.registry` e envie as imagens para lá.

## Custo (leia antes de aplicar)

**Isto cria recursos que cobram enquanto existirem**, principalmente: duas instâncias de Cloud SQL em alta disponibilidade, o cluster GKE Autopilot (cobrado pelo que os pods pedem), o Memorystore e a NAT. **O custo no Google Cloud não foi estimado** (a estimativa do projeto, em [12](../../../docs/12-estimativa-de-custos.md), é da AWS). Para um piloto barato: `sql_high_availability = "ZONAL"`, `redis_tier = "BASIC"` e `deletion_protection = false`, e **destrua tudo ao terminar** (`terraform destroy`).

## O que ainda não foi testado

- **Nunca foi aplicado** em um projeto real. O que foi verificado é a sintaxe e a coerência interna (`validate`); a primeira aplicação pode pedir ajustes (cotas, nomes de versão, APIs que demoram a habilitar).
- **O sistema nunca rodou no Google Cloud.** As lacunas listadas em [21](../../../docs/21-nuvem.md) valem aqui: Keycloak em modo de desenvolvimento (uma instância, banco embutido), migrações com o usuário da aplicação e testes de carga só no ambiente local.
- **O Redis está sem criptografia em trânsito** (só dentro da rede privada, com senha); para produção, ligar o TLS e entregar a CA ao serviço (comentário em `redis.tf`).
- **As duas bases compartilham a mesma senha** de aplicação, porque o chart recebe uma só (`postgres.password`).
- **O estado do Terraform** fica na máquina de quem aplica; para uma equipe, configurar o *backend* em um bucket (comentário em `versions.tf`).
