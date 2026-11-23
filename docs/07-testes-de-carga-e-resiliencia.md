# 07 — Testes de carga e de resiliência

Provam, com medições no cluster local (kind, 4 nós), os requisitos **RNF-01** (isolamento de falha), **RNF-02** (50 req/s com perda ≤ 5%), **RNF-03** (latência), **RNF-04** (frescor) e **RNF-13** (redundância). As ferramentas rodam em Docker (k6), sem instalar nada.

```powershell
./scripts/load-test.ps1 -Scenario balance                 # 50 req/s por 5 min (RNF-02)
./scripts/load-test.ps1 -Scenario stress                  # sobe até 150 req/s
./scripts/resilience-test.ps1 -Scenario pod               # perde um pod de cada serviço
./scripts/resilience-test.ps1 -Scenario consolidation     # derruba a Consolidação
./scripts/resilience-test.ps1 -Scenario database          # mata só o PostgreSQL primário de Lançamentos
./scripts/resilience-test.ps1 -Scenario node              # para um nó inteiro
```

Os scripts k6 ficam em `tests/load/`; os resultados em `results/` (não versionado). Dá para acompanhar tudo ao vivo no painel do Grafana.

## Como o teste é feito

- **Tráfego como em produção:** passa pelo Gateway com token real do Keycloak (renovado a cada 4 min), não direto nos serviços.
- **Taxa de chegada constante** (`constant-arrival-rate`): o k6 mantém 50 req/s mesmo que o servidor fique lento, em vez de esperar a resposta (modelo aberto, o que expõe lentidão de verdade). Se não conseguir manter a taxa, registra `dropped_iterations`.
- **Leitura e escrita juntas:** 50 leituras/s do saldo e 5 escritas/s de lançamentos, de modo que o *cache* está sendo invalidado enquanto é lido.

## Resultados

### Carga: 50 req/s por 5 minutos (RNF-02, RNF-03)

| | Requisições | Falhas | p95 | máx. |
|---|---|---|---|---|
| Leitura de saldo | 15.000 | **0** | 8 ms | 26 ms |
| Escrita de lançamento | 1.500 | **0** | 22 ms | 58 ms |

Perda de **0%** (o enunciado aceita até 5%; a meta do projeto era < 1%). Latência muito abaixo da meta de 500 ms. Iterações não disparadas pelo k6: 0.

**Frescor do saldo (RNF-04):** p50 0,27 s, p95 0,50 s, p99 0,89 s, contra a meta de p95 < 15 s.

### Estresse: rampa até 150 req/s

Leituras de 50 → 100 → 150 req/s (2 min a 150), com 10 escritas/s: **34.499 leituras e 3.000 escritas, 0 falhas**, leitura p95 6,7 ms. Nenhuma resposta 5xx nem 429. O sistema tem margem de mais de 3× sobre o requisito.

### Resiliência (tráfego contínuo: 30 leituras/s + 10 escritas/s)

| Cenário | Leituras | Escritas | Leitura |
|---|---|---|---|
| Perda de **um pod** de cada serviço, um por vez (Gateway, Lançamentos, Consolidação) | 3.285, **0 falhas** | 1.099, **0 falhas** | |
| **Consolidação fora do ar** por 40 s (RNF-01) | 982 falhas de 2.899 (34%); falham em até 5 s | 1.001, **0 falhas** | *esperado*: sem a Consolidação não há saldo |
| **Um nó parado** por 70 s (`cashflow-worker2`) | 10 falhas de 4.501 (0,22%) | 69 falhas de 1.500 (4,6%) | ver abaixo |

**RNF-01 comprovado:** com a Consolidação inteira fora do ar, 100% dos lançamentos foram aceitos, e ao religar os eventos acumulados foram consumidos (ver [05](05-execucao-local.md)).

**Perda de nó:** o nó parado tinha uma réplica de cada serviço, além do Keycloak e do PostgreSQL da Consolidação (deduzido pelos reinícios de pods depois do teste). Esta medição foi feita **antes** das correções de `preStop` e de limite de tempo descritas abaixo e não foi repetida. Não houve indisponibilidade total e o sistema se recuperou sozinho quando o nó voltou, mas **houve perda de 4,6% das escritas**, concentrada nos primeiros segundos. O Kubernetes só marca o nó como fora do ar e retira seus pods do balanceamento após cerca de 40 s (`node-monitor-grace-period`); nesse intervalo, parte das requisições ainda vai a pods que não respondem. A meta de 100% de sucesso na escrita (RNF-01) vale para a falha da Consolidação; para a perda de nó inteiro, esta medição mostra o limite atual e a evolução abaixo.

## O que as medições nos fizeram mudar

Os testes não foram só uma prova: apontaram duas melhorias, já aplicadas e remedidas.

| Achado | Correção | Antes → depois |
|---|---|---|
| Perda de um pod: ~1% de falhas, porque o pod que recebe o sinal de parada ainda fica alguns segundos no balanceamento | `preStop` com 5 s de espera: o pod continua atendendo enquanto o Kubernetes o retira dos Services | 42 falhas de 4.402 (~1%) → **0** |
| Leitura com a Consolidação fora do ar demorava 15 s para falhar | Limite de 5 s por requisição no Gateway (`ActivityTimeout`) | falha em 15 s → 5 s |
| O frescor aparecia como "p95 4,75 s" | Eram os *buckets* padrão do histograma, largos demais; foram definidos *buckets* próprios | valor real: p95 0,50 s |

## Limites desta medição (leia antes de citar os números)

- **Um comerciante e um dia:** o teste lê sempre o mesmo saldo, então ~93% das leituras vêm do *cache*. Com muitos comerciantes e datas o acerto do *cache* cai e a latência sobe, ainda que o PostgreSQL responda bem; o teste de dados espalhados fica como evolução.
- **Máquina única:** cliente, cluster e dependências disputam a mesma máquina; em nuvem, os números absolutos serão outros. A conclusão (folga grande sobre 50 req/s) é robusta; o valor exato de latência não.
- **Aquecimento:** na primeira rodada logo após uma troca de versão, 23 de 15.000 iterações (0,15%) não foram disparadas por uma parada de ~3 s; repetindo com o sistema aquecido, 0. Em produção, a troca de versão usa *rolling update* com *probes*, e esse efeito é absorvido. Visto de novo em 23/11/2026, logo após reiniciar o Docker: 0 requisições com falha, mas `dropped_iterations` = 109 e leitura p99 acima de 1 s (máximo 6,5 s), todos nos **primeiros ~6 s** (o k6 chegou a 151 VUs com quase nada concluído). A rodada seguinte, aquecida, passou em todos os limites (leitura p95 15,8 ms / p99 26 ms, escrita p95 66 ms, 0 descartadas). **Rode o teste de carga com o sistema aquecido** (por exemplo, depois do `smoke-test.ps1`).
- **Máquina compartilhada:** outro contêiner pesado na mesma VM do Docker (500–900% de CPU) causou rajadas de `EOF` a cada ~30 s e 0,54% de falhas, sem nenhum erro nos logs do Gateway nem do Keycloak (a conexão caía antes de chegar neles). Sem essa carga, 0 falhas. Para medir, **deixe o Docker só com o cluster**.
- **Perda de nó com a camada 2 ([14](14-alta-disponibilidade.md)):** a primeira medição (linha acima) parou um nó sem o PostgreSQL de Lançamentos nem o RabbitMQ. Depois da camada 2 o teste passou a parar **o nó que hospeda o PostgreSQL primário de Lançamentos** (o pior caso), por 130 s: **0,13% das leituras e 7,8% das escritas recusadas** (166 de 2.140, cerca de 17 s de janela de failover), nenhum lançamento aceito perdido, fila do RabbitMQ com 3 réplicas idênticas ao final. O resultado só foi esse depois de achar que o operador do PostgreSQL também precisava de duas réplicas (com uma, 52% das escritas falharam).
- **Reenvio de escrita no Gateway (feito):** só para leituras e escritas com `Idempotency-Key`. No failover do banco, as escritas recusadas caíram de 16,0% para 4,4% (4 tentativas). Medições e limites em [14](14-alta-disponibilidade.md).
