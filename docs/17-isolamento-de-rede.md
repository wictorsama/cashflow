# 17 — Isolamento de rede entre os pods

Sem regras, qualquer pod do cluster conversa com qualquer outro: um pod comprometido (ou um que não faz parte do sistema) poderia abrir conexão direto com o banco, o broker ou o cache. As `NetworkPolicy` do chart ([networkpolicy.yaml](../deploy/helm/cashflow/templates/networkpolicy.yaml)) fecham isso.

## A regra

**Toda entrada é negada por padrão** no namespace; cada caminho que o sistema usa é aberto **explicitamente**:

| Destino | Quem pode chegar |
|---|---|
| Gateway, aplicação web, Keycloak, Grafana, Jaeger | Qualquer origem (são os pontos de entrada públicos) |
| Lançamentos | Gateway e Consolidação (esta lê os totais da reconciliação) |
| Consolidação | Gateway |
| PostgreSQL de Lançamentos / de Consolidação | O próprio serviço, as réplicas do mesmo banco e o operador do PostgreSQL |
| RabbitMQ | Os dois serviços, os próprios nós e o *job* que espalha as filas |
| Redis | Consolidação |
| Prometheus | Gateway, os dois serviços e Grafana |

A saída (*egress*) **não** é restringida: os serviços chamam Keycloak, broker e bancos por nome, e fechar a saída também é um passo seguinte.

## Prova no cluster real

Um pod sem relação com o sistema, dentro do mesmo namespace, tentou abrir conexão com cada destino:

```
BLOCKED postgres-entries-rw:5432
BLOCKED postgres-consolidation-rw:5432
BLOCKED rabbitmq:5672
BLOCKED redis:6379
BLOCKED entries:80
BLOCKED consolidation:80
OPEN    gateway:80
OPEN    keycloak:80
```

Só os pontos públicos respondem. Com as regras ligadas, o teste de fumaça (25 verificações), a replicação do PostgreSQL, o *cluster* do RabbitMQ (3 nós) e a reconciliação continuam funcionando: o sistema só usa os caminhos abertos.

## Limitações

- **Só entrada.** Um pod comprometido ainda pode **sair** para qualquer destino.
- **Precisa de um plugin de rede que aplique a regra.** O kind local aplica (verificado acima); em outro cluster, sem esse plugin, os objetos são aceitos e **não fazem nada**. O chart tem `networkPolicy.enabled` para ligar e desligar.
- **Sem mTLS:** a regra diz *quem* pode falar, não cifra nem autentica a conversa. mTLS entre serviços segue como evolução.
- **Os pontos públicos aceitam qualquer origem.** Quem os protege é a autenticação (JWT) e o limite de taxa do Gateway, não a rede.
