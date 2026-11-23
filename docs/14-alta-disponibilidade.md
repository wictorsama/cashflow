# 14 — Alta disponibilidade das dependências (camada 2)

Na camada 1 os serviços .NET e o Gateway já eram redundantes, mas PostgreSQL e RabbitMQ eram instâncias únicas: perder o nó que os hospedava interrompia o registro. A camada 2 replica essas duas dependências.

| Dependência | Camada 1 | Camada 2 |
|---|---|---|
| PostgreSQL de Lançamentos e de Consolidação | 1 instância cada | **2 instâncias cada** (primário + réplica síncrona), em nós diferentes, com **failover automático** (CloudNativePG) |
| Operador do PostgreSQL | n/a | **2 réplicas** em nós diferentes (quem faz o failover não pode cair junto) |
| RabbitMQ | 1 nó | **3 nós** com ***quorum queues*** (Raft) |
| Redis | 1 instância | **continua 1, de propósito** (ver abaixo) |
| Keycloak | 1 instância | **continua 1** (ver limitações) |

## PostgreSQL: primário, réplica e failover

- **Cada serviço mantém seu banco**, agora um *cluster* de duas instâncias. A aplicação se conecta a `postgres-<serviço>-rw`, um nome que sempre aponta para o primário atual: quando há failover, o nome passa a apontar para a ex-réplica e nada muda na aplicação.
- **Replicação síncrona** (`dataDurability: preferred`): uma gravação só é confirmada depois de estar também na réplica. Um failover **não perde lançamentos confirmados**. Se a réplica estiver fora do ar, o primário continua aceitando escritas: **a disponibilidade do registro vem primeiro** (RNF-01). A escolha oposta (`required`) pararia as escritas até a réplica voltar.
- **Primário e réplica nunca dividem um nó** (*anti-affinity* obrigatória).
- **O operador também é replicado.** Descoberto no teste: com uma única réplica do operador, parar o nó que o hospedava deixava **ninguém para promover a réplica**, e as escritas ficavam fora até o nó voltar (52% de falhas). Com duas réplicas em nós diferentes, o failover aconteceu sozinho.

## RabbitMQ: três nós, filas replicadas

- Cluster de 3 nós, um por nó do Kubernetes, que se encontram por nomes DNS estáveis. Uma partição de rede é resolvida a favor da maioria (`pause_minority`): a minoria para em vez de divergir.
- **Filas *quorum*:** cada mensagem só é confirmada ao publicador depois de gravada na **maioria** (2 de 3) dos nós. Perder um nó não perde mensagens e não interrompe a publicação. Os dois lados (publicador e consumidor) declaram a fila com o mesmo tipo; um teste garante que concordam.
- **Fila criada cedo demais:** uma fila *quorum* nasce com réplicas apenas nos nós que existem naquele instante, e os serviços podem declará-la antes de o cluster fechar. Um *job* do Helm (pós-instalação e pós-atualização) **espalha as filas por todos os nós**.

## Redis fica com uma instância (decisão consciente)

O Redis é só um *cache*: o serviço de Consolidação **captura qualquer falha dele e lê do banco** (o código e um teste existem desde a camada 1). Réplica e *Sentinel* acrescentariam peças para proteger algo cuja perda custa milissegundos de latência, não dados nem disponibilidade. Perder o Redis é um evento degradado, não uma falha.

## Provas no cluster real (3 *workers*)

Teste de nó (`resilience-test.ps1 -Scenario node`): **para o nó que hospeda o PostgreSQL primário de Lançamentos** (o pior caso), junto com um pod de Gateway, um de Lançamentos, um nó do RabbitMQ e o primário da Consolidação, durante 130 s, sob 30 leituras/s + 10 escritas/s.

| Configuração | Leituras com falha | Escritas com falha |
|---|---|---|
| Operador com 1 réplica, nó parado por 60 s (antes de o nó ser sequer declarado morto) | 38% | 42,6% |
| Operador com 1 réplica, nó parado por 130 s (operador no nó parado: sem failover) | 0,13% | 52,3% |
| **Operador com 2 réplicas, nó parado por 130 s** | **0,13%** (10 de 7.488) | **7,8%** (166 de 2.140) |

Leitura quase não sente a perda do nó. A **escrita ainda falha em 7,8% das requisições do teste** (166 de 2.140, cerca de 17 s de indisponibilidade a 10 escritas/s): é a janela do failover, que não é instantâneo (ver abaixo). A carga sustentada de 50 req/s do RNF-02, **sem falha de infraestrutura**, foi medida antes sem nenhuma perda ([07](07-testes-de-carga-e-resiliencia.md)). Nenhum lançamento **aceito** se perdeu; os 7,8% são requisições **recusadas** (o cliente recebe erro e pode tentar de novo com a mesma `Idempotency-Key`).

Depois do teste, no mesmo cluster:

- O primário passou de `postgres-entries-1` para `postgres-entries-2` sozinho.
- A fila `consolidation.entries` tem **3 membros votantes com o mesmo índice de log** (7.511), incluindo o nó que foi parado e voltou; a liderança mudou de nó.
- O teste de fumaça (18 verificações) passa, incluindo **"nenhum saldo diverge do que foi registrado"**: nenhum lançamento aceito se perdeu.

## Reenvio de escrita no Gateway

O Gateway tenta de novo quando um serviço não responde (502, 503 ou 504 por inacessível, tempo esgotado ou indisponível). A nova tentativa passa de novo pelo Service do Kubernetes e costuma cair em outra réplica; e, durante o failover do banco, ela dá ao banco tempo de se recuperar sem o cliente ver o erro.

- **Só repete o que é seguro repetir:** leituras (`GET`, `HEAD`) e escritas **com `Idempotency-Key`**. O serviço responde a uma chave repetida com o resultado original, então repetir não registra o lançamento duas vezes, **mesmo que a primeira tentativa tenha chegado ao banco e só a resposta tenha se perdido**. Escrita sem chave **nunca** é repetida.
- **Só repete falha de disponibilidade:** 4xx (validação, conflito) e 500 voltam como vieram, pois repetir só repetiria o erro.
- **Detalhes que importam:** o corpo (até 64 KB) é guardado em memória e reenviado em cada tentativa; a resposta de cada tentativa fica retida até se saber que é a final; a nova tentativa não consome o limite de taxa do comerciante; espera de 250 ms, 500 ms, 750 ms entre tentativas. Configurável em `Retry:MaxAttempts` (padrão 4; 1 desliga) e `Retry:DelayMilliseconds`. Métrica `cashflow.gateway.retries{outcome=recovered|exhausted}`.
- **O custo é latência:** com tentativas de até 5 s (o limite do Gateway), o pior caso de uma escrita é cerca de 18 s, em vez de um erro rápido. Para escrita de lançamento, esperar é melhor do que o comerciante ter de redigitar; ainda assim o cliente deve ter um limite de tempo maior que isso.

**Medição** (cenário `database`: só o pod do PostgreSQL primário de Lançamentos é morto, sem perda de nó; 30 leituras/s e 10 escritas/s por 90 s):

| Tentativas | Escritas com falha | Pior latência de escrita |
|---|---|---|
| 1 (sem reenvio) | 16,0% (141 de 882) | 5,0 s |
| 3 | 6,5% (55 de 844) | 15,9 s |
| **4 (padrão)** | **4,4% (37 de 838)** | 18,3 s |

Leituras: 0 falhas nas três. Com 4 tentativas a escrita fica abaixo dos 5% do RNF-02 **mesmo durante um failover de banco**. Cada execução com tentativas ainda perde alguns pedidos porque a janela de failover (cerca de 14 s) é maior do que a soma das esperas das primeiras tentativas; mais tentativas reduziriam mais, ao custo de mais latência.

**O que o reenvio não resolve:** a **perda de um nó inteiro**. Nos testes de nó com o reenvio ligado, as escritas falharam em 3,5% e 12,2% e as leituras em 3,8% e 8,5%, variando conforme os pods que estavam no nó parado. A causa dominante está **antes** do Gateway: no cluster local a porta de entrada é um NodePort, e o kube-proxy continua mandando conexões ao pod de Gateway do nó morto até o Kubernetes o retirar (~40 s). Um Gateway morto não pode reenviar nada. Em nuvem, o balanceador de carga com *health check* retira o alvo em segundos; localmente isso é uma limitação do ambiente, não do desenho.

**Medição final, com todas as proteções ligadas** (Auditor, cadeia de auditoria, isolamento de rede e reenvio; 21/11):

| Cenário | Leituras com falha | Escritas com falha |
|---|---|---|
| Carga de 50 req/s por 5 min (5 escritas/s junto) | 0 | 0 (p95 de leitura 9 ms, de escrita 32 ms) |
| Consolidação fora do ar por 40 s | 11,4% (esperado: o saldo está fora) | **0**: 100% dos lançamentos aceitos (RNF-01) |
| Um pod de cada serviço removido, um por vez | 0 | 0 |
| Só o PostgreSQL primário morto | 0 | 0,5% (4 de 781) |
| Nó inteiro (o do primário) parado por 130 s | 0,35% (26 de 7.501) | 9,0% (187 de 2.069) |

Depois de tudo isso, a cadeia de auditoria tinha **7.750 elos íntegros**, o teste de fumaça (25 verificações) passou e a reconciliação não achou divergência: nenhum lançamento aceito se perdeu. A primeira rodada de carga logo após uma troca de versão teve 0,19% de falhas (aquecimento); repetida, 0.

## Por que o failover não é instantâneo

Para dar um nó por morto, o Kubernetes espera cerca de **40 s** sem notícias dele; só então marca os pods como não prontos, tira-os dos serviços e o operador promove a réplica. Durante essa janela, as requisições que caem num pod do nó parado esperam e falham (limite de 5 s no Gateway). Em nuvem, o balanceador de carga retira alvos doentes em segundos e o tempo de detecção do nó pode ser ajustado; localmente o valor padrão foi mantido, para a medição não ser otimista.

## Limitações

- **Keycloak em instância única** (banco embutido): se cair, **não se obtêm tokens novos** e ninguém entra pela tela. Quem já tem token continua usando a API até ele expirar, porque os serviços validam a assinatura localmente. Em produção entra Keycloak com banco externo e várias réplicas; não foi feito aqui.
- **Perda de nó no cluster local:** a porta de entrada (NodePort) continua mandando tráfego ao pod de Gateway do nó parado até o Kubernetes o retirar; só um balanceador com *health check* (nuvem) resolve. Ver acima.
- **Subida inicial mais lenta:** as imagens do PostgreSQL e do operador somam alguns minutos de *download* na primeira vez; o `up.ps1` espera até 10 minutos.
- **Um único cluster físico:** a camada 2 protege contra a perda de **um nó**, não de uma zona ou região. Na nuvem, as réplicas se espalham por zonas de disponibilidade.
