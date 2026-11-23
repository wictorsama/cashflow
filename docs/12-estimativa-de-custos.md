# 12 — Estimativa de custos

Quanto custa rodar o sistema, em três cenários: **local** (zero), **piloto na nuvem** e **produção de referência** (as metas de disponibilidade do [02](02-requisitos.md)). A nuvem de referência é a **AWS, região us-east-1**, só porque é a de preços mais publicados; o desenho é portátil ([ADR-0008](adr/0008-helm-e-kind.md)) e os mesmos componentes existem em Azure e GCP.

> **Leia isto antes de citar um número.** É uma **estimativa de ordem de grandeza**, não uma cotação. Os preços unitários vieram de páginas de preço públicas consultadas em 17/11/2026 e **mudam**; cada um está marcado com o nível de confiança. Antes de qualquer decisão financeira, refaça a conta na [calculadora oficial da AWS](https://calculator.aws/). Valores em **dólares por mês**, sob demanda, 730 horas por mês, sem impostos, sem tarifa de suporte e sem a mão de obra da equipe.

## Preços unitários usados

| Item | Preço | Confiança |
|---|---|---|
| EKS, plano de controle | US$ 0,10 por hora = **US$ 73** por mês por cluster ([AWS EKS](https://aws.amazon.com/eks/pricing/)) | Fonte oficial |
| Nó de cálculo `m6i.large` (2 vCPU, 8 GiB) | US$ 0,096/h = **US$ 70,08**; reservado por 1 ano ≈ US$ 0,067/h = US$ 48,91 ([referência](https://calculator.holori.com/aws/ec2/m6i.large/us-east-1)) | Terceiros concordantes |
| RDS PostgreSQL `db.t4g.medium` (2 vCPU, 4 GiB) | Zona única US$ 0,065/h = **US$ 47,45**; **Multi-AZ** US$ 0,130/h = **US$ 94,90** ([RDS](https://aws.amazon.com/rds/postgresql/pricing/)) | Terceiros concordantes |
| Amazon MQ para RabbitMQ, `mq.m5.large` | US$ 0,288/h por nó; *cluster* de 3 nós = **US$ 630,72**, mais armazenamento (exemplo oficial: US$ 60) ([Amazon MQ](https://aws.amazon.com/amazon-mq/pricing/)) | Exemplo oficial da AWS |
| ElastiCache `cache.t4g.small` | Redis OSS US$ 0,0256/h = **US$ 18,69** por nó ([ElastiCache](https://aws.amazon.com/elasticache/pricing/)) | Terceiro (valor varia entre fontes) |
| NAT Gateway | US$ 0,045/h = **US$ 32,85** por unidade, mais US$ 0,045 por GB ([guia](https://cloudburn.io/blog/aws-nat-gateway-pricing)) | Terceiros concordantes |
| Saída de dados para a internet | US$ 0,09 por GB (primeiros 10 TB) | Terceiros |
| Balanceador, observabilidade em disco, segredos, DNS | Valores de ordem de grandeza (linhas marcadas "estimado") | **Não verificado**: confirmar |

## Cenário 1: local

**US$ 0.** Roda em Docker no computador do desenvolvedor, com o `./scripts/up.ps1`. Todo o *software* é de código aberto sem licença (Keycloak, RabbitMQ, PostgreSQL, Redis, Prometheus, Grafana, Jaeger, Next.js, .NET), então não há custo de licença em nenhum cenário.

## Cenário 2: piloto na nuvem (poucos comerciantes, sem alta disponibilidade total)

Para validar com um grupo pequeno. Uma zona de disponibilidade, dependências simples, tudo o mais dentro do cluster.

| Componente | Cálculo | US$/mês |
|---|---|---|
| EKS (plano de controle) | 1 cluster | 73,00 |
| Nós `m6i.large` | 2 × 70,08 | 140,16 |
| PostgreSQL (Lançamentos e Consolidação) | 2 × RDS zona única | 94,90 |
| Keycloak e seu banco | banco no RDS da Consolidação (outra base) | 0,00 |
| Redis | 1 nó `cache.t4g.small` | 18,69 |
| RabbitMQ | dentro do cluster, 1 instância, mais disco (estimado) | 3,00 |
| Balanceador (estimado) | ALB com poucas requisições | 26,00 |
| NAT Gateway | 1 × 32,85 + ~100 GB × 0,045 | 37,35 |
| Observabilidade em disco (estimado) | Prometheus, Grafana, Jaeger no cluster | 20,00 |
| Saída de dados, DNS e segredos (estimado) | | 15,00 |
| **Total** | | **≈ US$ 428** |

Um piloto de 50 comerciantes custaria ≈ US$ 8,50 por comerciante por mês, dominado pelo custo fixo (plano de controle e nós). **Não atende** a disponibilidade de 99,5% (RNF-05): uma falha de zona derruba tudo.

## Cenário 3: produção de referência (alta disponibilidade, RNF-05)

Três zonas, bancos com Multi-AZ, *cache* com réplica, três nós (um por zona). O ponto de decisão é o **RabbitMQ**: gerenciado ou dentro do cluster.

| Componente | Cálculo | US$/mês |
|---|---|---|
| EKS (plano de controle) | 1 cluster | 73,00 |
| Nós `m6i.large` | 3 × 70,08 (um por zona) | 210,24 |
| PostgreSQL Multi-AZ | 3 × 94,90 (Lançamentos, Consolidação e Keycloak, bancos separados) | 284,70 |
| Redis com réplica | 2 × 18,69 | 37,38 |
| Balanceador (estimado) | ALB com tráfego de produção | 26,00 |
| NAT Gateway | 3 × 32,85 + ~100 GB × 0,045 | 103,05 |
| Observabilidade (estimado) | disco e retenção | 28,00 |
| Saída de dados (estimado) | ~500 GB × 0,09 | 45,00 |
| Segredos e DNS (estimado) | | 5,00 |
| **Subtotal (sem broker)** | | **812,37** |

| Opção para o RabbitMQ | Custo adicional | Total/mês |
|---|---|---|
| **B. Dentro do cluster** (3 pods com *quorum queues*, um nó a mais para folga e discos) | 70,08 + 5,00 ≈ 75 | **≈ US$ 887** |
| **A. Gerenciado** (Amazon MQ, 3 nós `mq.m5.large`) | 630,72 + 60,00 = 690,72 | **≈ US$ 1.503** |

**A escolha do broker muda a conta em ~70%.** O gerenciado entrega operação, atualização e recuperação prontas, mas custa quase 10 vezes mais que o próprio cluster absorve. A recomendação deste projeto é a **opção B** enquanto o volume for o de referência, e rever se a equipe não tiver capacidade de operar RabbitMQ. Reservar os nós por 1 ano baixa a opção B para ≈ US$ 803.

### Custo por unidade de negócio (opção B)

Com o volume de referência ([02](02-requisitos.md), P2): 1.000 comerciantes × 200 lançamentos/dia = 6 milhões de lançamentos por mês.

| Indicador | Valor |
|---|---|
| Custo por **1.000 lançamentos** | ≈ US$ 0,15 |
| Custo por **comerciante por mês** (1.000 comerciantes) | ≈ US$ 0,89 |

## O que faz o custo crescer

| Se aumentar... | O que cresce | Observação |
|---|---|---|
| **Leituras de saldo** | Nós (Gateway, Consolidação) e, pouco, Redis | O *cache* serviu 93% das leituras no teste; o banco quase não sente. Escala por mais réplicas (HPA). |
| **Lançamentos** | Banco de Lançamentos e armazenamento; fila | O gargalo previsível é o PostgreSQL de escrita; sobe de classe (`db.t4g.large`) antes de exigir novos nós. |
| **Número de comerciantes** | Armazenamento e saldo no banco | Crescimento linear e lento. |
| **Retenção de logs e traces** | Disco e, se for serviço gerenciado, ingestão | Trocar por amostragem de traces e retenção de 15 dias. |
| **Tráfego de saída** | Saída de dados e NAT | Pouco: as respostas são pequenas. |

Dez vezes o volume de referência tende a aumentar o custo bem menos que dez vezes, porque a maior parte é **custo fixo** (plano de controle, redes, bancos mínimos, observabilidade).

## Assistente de IA

O Assistente é um serviço .NET pequeno (2 réplicas de ~100 MiB, sem banco): cabe nos nós que já existem e não muda o custo fixo. **O único custo variável é o provedor de IA**, e só se for o Claude: tokens de entrada e de saída por rascunho. Estimativa de volume (por contagem de texto, a confirmar com a métrica `cashflow.assistant.tokens`): ~700 tokens de entrada e ~60 de saída por rascunho; **1.000 rascunhos por dia ≈ 0,7 milhão de tokens de entrada e 0,06 milhão de saída**. O valor em dinheiro é esse volume vezes o preço vigente do modelo escolhido (consultar a tabela do provedor; não está nesta estimativa). Com o provedor `rules`, o custo é zero. O limite por comerciante (20 por minuto) e o orçamento monitorado pela métrica de tokens contêm o gasto; ver [19](19-assistente-de-ia.md).

## Como reduzir

- **Nós reservados por 1 ano** (≈ 30% menos nos nós, verificado) e, para bancos, planos equivalentes.
- **Um NAT Gateway em vez de três** no início (economiza ≈ US$ 66, com a troca de resiliência da rede de saída; atende só à zona que o usa).
- **Nós Spot** para o que tolera interrupção (não para bancos nem para o broker).
- **Amostragem de traces** (por exemplo 10%) e retenção curta de métricas.
- **Right-sizing** depois de duas semanas de uso real: os `requests` de recursos do chart foram dimensionados para o ambiente local e devem ser revistos com o consumo observado nos painéis.

## Custos fora desta estimativa

- **Pessoas:** operação do cluster, plantão e evolução do produto costumam superar a infraestrutura.
- **Assistente de IA (fase 4):** custo por chamada = tokens de entrada × preço + tokens de saída × preço do provedor escolhido. É variável e proporcional ao uso, e entra aqui quando o provedor for definido ([ADR-0011](adr/0011-assistente-de-ia-por-interface.md)).
- **Suporte da AWS, impostos, câmbio e licenças** de terceiros (não há nenhuma no desenho atual).
- **Migração do legado** (carga, reconciliação, treinamento): ver [11](11-arquitetura-de-transicao.md).

## Como refazer a conta

1. Abra a [calculadora da AWS](https://calculator.aws/) e reproduza a tabela do cenário escolhido linha a linha.
2. Troque a região e a moeda se necessário (os preços em us-east-1 costumam ser os mais baixos).
3. Atualize os itens marcados "estimado" com o consumo real depois do piloto.
4. Registre a data da consulta junto do resultado.
