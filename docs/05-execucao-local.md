# 05 — Execução local (Kubernetes com kind)

Sobe o sistema inteiro em um cluster Kubernetes local com **um comando**, usando os mesmos Dockerfiles e o mesmo chart Helm pensados para a nuvem ([ADR-0008](adr/0008-helm-e-kind.md)).

## Pré-requisitos

| Ferramenta | Instalação (Windows) |
|---|---|
| Docker Desktop (em execução, ≥ 6 GB de memória) | https://www.docker.com/products/docker-desktop |
| kind | `winget install Kubernetes.kind` |
| Helm | `winget install Helm.Helm` |
| kubectl | já vem com o Docker Desktop |

## Subir e derrubar

```powershell
./scripts/up.ps1      # cria o cluster, constrói as imagens, instala o chart e aguarda a prontidão
./scripts/down.ps1    # apaga o cluster e os dados
```

O script é repetível: se o cluster já existe, ele reaproveita o cluster e atualiza a aplicação (cada execução usa uma *tag* de imagem nova, o que provoca um *rolling update*).

## O que é criado (camada 1)

| Componente | Réplicas | Acesso |
|---|---|---|
| Aplicação web (Next.js) | 2 | `http://localhost:3000`, **onde o comerciante usa o sistema** (ver [08](08-frontend.md)) |
| Gateway (YARP) | 2 | `http://localhost:8080`, **única porta pública da API** |
| Keycloak | 1 | `http://localhost:8082`, emissor dos tokens (realm `cashflow`) |
| Lançamentos (Entries) | 2 | apenas dentro do cluster |
| Consolidação (Consolidation) | 2 | apenas dentro do cluster |
| PostgreSQL de Lançamentos | 2 (primário + réplica) | banco próprio, failover automático |
| PostgreSQL de Consolidação | 2 (primário + réplica) | banco próprio, failover automático |
| RabbitMQ | 3 | filas *quorum* replicadas |
| Redis | 1 | apenas *cache*, sem persistência |

O cluster tem 4 nós (1 *control-plane* e 3 *workers*). Os serviços .NET têm *topology spread* entre nós, *rolling update* sem indisponibilidade, *probes* de inicialização, prontidão e vivacidade, *PodDisruptionBudget*, contêiner sem privilégios e sistema de arquivos somente leitura, e esperam o PostgreSQL ficar pronto antes de iniciar (*init container*), em vez de reiniciar em laço. O HPA existe no chart, desligado localmente (exige *metrics-server*; ligar em `values-cloud.yaml`).

PostgreSQL (CloudNativePG) e RabbitMQ rodam replicados (**camada 2**, [14](14-alta-disponibilidade.md)); o `up.ps1` instala o operador do PostgreSQL antes do chart. A primeira subida baixa imagens maiores e demora mais.

## Segurança na borda e nos serviços

```text
cliente --token--> Gateway (valida JWT, limita taxa, correlation id) --token--> serviço (valida de novo + escopo)
              \--> Keycloak (emite o token)
```

- **Todas as rotas exigem JWT** (exceto `/health`). O Gateway rejeita token ausente, expirado, de outro emissor/público ou com assinatura falsa antes de qualquer serviço ser acionado; **cada serviço valida de novo** e confere o escopo (defesa em profundidade, [ADR-0006](adr/0006-keycloak-oidc.md)).
- **O comerciante vem do *claim* `merchant_id` do token**, nunca de cabeçalho ou parâmetro. Um cabeçalho `X-Merchant-Id` enviado pelo cliente é ignorado (há teste para isso).
- **Escopos:** `entries:write`, `entries:read`, `balances:read`, `balances:rebuild`. Token sem o escopo recebe 403; token válido sem `merchant_id` também recebe 403.
- **Clientes do realm** (arquivo versionado em `deploy/helm/cashflow/files/cashflow-realm.json`; segredos vêm de variáveis, não do arquivo):

| Cliente | Fluxo | Escopos | Uso |
|---|---|---|---|
| `merchant-integration` | *client credentials* | `entries:write`, `entries:read`, `balances:read` | integração de um comerciante (demo: `11111111-…`) |
| `ops-admin` | *client credentials* | `balances:rebuild` | reprocessar saldos; nada além disso |
| `cashflow-web` | *authorization code + PKCE* | leitura e escrita | aplicação web ([08](08-frontend.md)) |

- **Limite de taxa** por comerciante: 200 req/s (o pico legítimo é 50 req/s; um 429 conta como perda, então o limite só barra abuso). **Corpo** máximo de 64 KB. **Cabeçalhos** de segurança em toda resposta e sem cabeçalho `Server`.
- Os serviços **não têm porta publicada**: só o Gateway e o Keycloak são acessíveis do *host*.

## Experimentando

A forma mais simples: abra `http://localhost:3000` e entre com `merchant1` / `local-dev-only`. Para usar a API diretamente:

```powershell
# 1) token (client credentials; o segredo abaixo é descartável, só do ambiente local)
$token = (Invoke-RestMethod -Method Post http://localhost:8082/realms/cashflow/protocol/openid-connect/token `
    -Body @{ grant_type='client_credentials'; client_id='merchant-integration'; client_secret='local-dev-merchant-secret' }).access_token

# 2) registra um crédito
Invoke-RestMethod -Method Post http://localhost:8080/v1/entries -Headers @{ Authorization="Bearer $token"; 'Idempotency-Key'='demo-1' } `
    -ContentType application/json -Body '{"type":"Credit","amount":100.00,"entryDate":"2026-11-17","description":"first sale"}'

# 3) consulta o saldo consolidado do dia
Invoke-RestMethod http://localhost:8080/v1/balances/2026-11-17 -Headers @{ Authorization="Bearer $token" }
```

Teste de isolamento (requisito central): derrube a Consolidação e veja que os lançamentos continuam sendo aceitos; ao religar, o saldo se atualiza sozinho.

```bash
kubectl -n cashflow scale deploy/consolidation --replicas=0
# ... registre lançamentos: continuam respondendo 201 ...
kubectl -n cashflow scale deploy/consolidation --replicas=2
```

## Verificado neste ambiente

- Com a Consolidação em 0 réplicas, 5 lançamentos foram aceitos (201); ao religar, o saldo refletiu todos eles.
- Pelo Gateway, com tokens reais do Keycloak: sem token 401; token de integração registra e lê (201/200); o mesmo token tentando reprocessar recebe 403; o token `ops-admin` reprocessa (200) mas não lê saldo (403); rota inexistente 404; porta direta do serviço fechada.
- Ao apagar os pods de Lançamentos, um pedido falhou na janela em que os dois pods eram removidos ao mesmo tempo e os seguintes foram aceitos assim que os novos pods ficaram prontos. O cenário de perda de **um** pod por vez será medido no teste de resiliência.

## Credenciais

`values-local.yaml` contém senhas e segredos de cliente descartáveis, só para o cluster local. O chart exige que cada um seja informado (`required`), sem valor padrão; na nuvem, virão de um gerenciador de segredos.

## Depois de reiniciar o Keycloak

No modo de desenvolvimento, o Keycloak local **gera chaves de assinatura novas a cada partida** (o realm é reimportado). Os serviços guardam as chaves que viram e só as renovam ao receber um token assinado por uma chave desconhecida, no máximo a cada 15 s; por isso, **logo depois de instalar ou trocar a versão, os primeiros pedidos podem receber 401** por alguns segundos, e as sessões abertas na tela expiram (basta entrar de novo). O teste de fumaça espera os serviços aceitarem as chaves novas antes de julgar (até 90 s). Em produção o Keycloak mantém as chaves entre partidas, e esse efeito não ocorre.

## Limitações atuais

- O papel **Auditor** ([15](15-papel-de-auditor.md)) tem API e tela (`auditor1` / `local-dev-only` no Keycloak local).
- Keycloak em modo de desenvolvimento (banco embutido, instância única) e HTTP sem TLS: aceitável localmente; na nuvem entram TLS na borda, banco externo e `requireHttpsMetadata: true`.
- O isolamento de rede (`NetworkPolicy`, [17](17-isolamento-de-rede.md)) cobre só a **entrada** dos pods; a saída segue aberta.
- A rota `/health` verifica apenas o processo; a prontidão real das dependências entra com a observabilidade.
