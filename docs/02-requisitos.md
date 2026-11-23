# 02 — Requisitos Funcionais e Não Funcionais

> Convenção: documentação em português; termos de domínio como *Nome (Name)*; código em inglês. Ver o [glossário](01-mapeamento-de-dominios.md#7-glossário-linguagem-ubíqua).

## 1. Premissas

| # | Premissa |
|---|---|
| P1 | **Perda de requisição** é qualquer resposta que não seja sucesso: erro 5xx, timeout ou 429. A meta de perda é medida sobre esse total. |
| P2 | **Volume de referência:** 1.000 comerciantes × 200 lançamentos por dia ≈ 200 mil lançamentos/dia, com picos de ~20 req/s de escrita. |
| P3 | **Pico do Consolidado:** 50 req/s (dado do enunciado), predominantemente leituras. |
| P4 | Moeda única (BRL); `decimal(18,2)`; saldo inicial igual a zero. |
| P5 | O "dia" do saldo segue `entryDate` no fuso do comerciante (padrão `America/Sao_Paulo`). |
| P6 | As metas de SLO (RNF-03 a RNF-06) são **suposições deste projeto**, não vêm do enunciado. Foram definidas de forma folgada e serão revisitadas. |

## 2. Requisitos funcionais (RF)

| ID | Contexto | Requisito |
|---|---|---|
| RF-01 | Lançamentos (Entries) | Registrar lançamento: tipo (Débito/Crédito), valor, `entryDate`, descrição. |
| RF-02 | Lançamentos | Estornar lançamento (gera lançamento compensatório); um lançamento só pode ser estornado uma vez. |
| RF-03 | Lançamentos | Listar lançamentos por dia ou período, com paginação. |
| RF-04 | Lançamentos | Consultar um lançamento por identificador. |
| RF-05 | Consolidação (Consolidation) | Consultar o saldo diário: total de créditos, total de débitos, líquido e saldo acumulado. |
| RF-06 | Consolidação | Consultar saldos por período (limite de 366 dias por consulta). |
| RF-07 | Consolidação | Atualizar o saldo automaticamente ao receber `EntryRegistered` e `EntryReversed`, de forma idempotente. |
| RF-08 | Consolidação | Reconstruir o saldo de um dia ou período a partir do histórico (uso administrativo). |
| RF-09 | Acesso (Access) | Autenticar e autorizar por papel (ver seção 3). |
| RF-10 | Lançamentos | Registro idempotente via cabeçalho `Idempotency-Key`: repetir a chamada não duplica o lançamento. |

### Fora de escopo (evoluções futuras)

Multimoeda, categorias de lançamento, conciliação bancária, impostos, edição ou exclusão de lançamentos.

## 3. Papéis e autorização (RF-09)

| Papel (Role) | Permissões |
|---|---|
| Comerciante (Merchant) | Registrar, estornar e consultar os **próprios** lançamentos e saldos. |
| Auditor (Auditor) | **Somente leitura** de lançamentos e saldos **dos comerciantes atribuídos a ele** (atribuição no token; cada requisição nomeia o comerciante), com trilha de cada acesso. Ver [15](15-papel-de-auditor.md). |
| Serviço (Service) | Credencial de máquina para a comunicação entre serviços, com escopos mínimos. |

Reconstrução de saldo (RF-08) é uma operação administrativa e exige escopo próprio.

## 4. Requisitos não funcionais (RNF)

| ID | Atributo | Meta | Origem |
|---|---|---|---|
| RNF-01 | **Isolamento de falha** | Com o Consolidado fora do ar, o registro de lançamentos continua com 100% de sucesso. Verificado derrubando o contêiner do Consolidado. | Enunciado |
| RNF-02 | **Carga do Consolidado** | 50 req/s sustentados por 5 min com perda ≤ 5% (P1). Meta de projeto: < 1%. Teste de estresse a 150 req/s para demonstrar a margem. | Enunciado |
| RNF-03 | **Latência** | Consulta de saldo: p95 < 500 ms e p99 < 1 s a 50 req/s. Registro de lançamento: p95 < 500 ms. | Suposição (P6) |
| RNF-04 | **Frescor do consolidado** | Lançamento refletido no saldo em p95 < 15 s e p99 < 60 s (consistência eventual). | Suposição (P6) |
| RNF-05 | **Disponibilidade** | Lançamentos: 99,5% ao mês (~3,6 h de indisponibilidade). Consolidação: 99% ao mês (~7,3 h). | Suposição (P6) |
| RNF-06 | **Durabilidade e recuperação** | Nenhum lançamento aceito se perde (Outbox, entrega *at least once*). RPO ≤ 15 min; RTO ≤ 1 h. | Suposição (P6) |
| RNF-07 | **Correção** | O saldo nunca diverge da soma dos lançamentos; um job de reconciliação verifica isso periodicamente. | Derivado |
| RNF-08 | **Segurança** | OIDC/JWT, TLS, escopos por serviço, rate limiting, OWASP API Top 10, segredos fora do código, trilha de auditoria, LGPD. | Diferencial |
| RNF-09 | **Observabilidade** | Logs, métricas e traces com correlation ID; painéis dos SLOs; alertas de taxa de erro e de atraso na fila. | Diferencial |
| RNF-10 | **Escalabilidade** | Serviços sem estado, escala horizontal; partição lógica por `merchantId`. | Enunciado |
| RNF-11 | **Testabilidade** | Testes unitários, de integração (Testcontainers), de contrato do evento e de carga (k6); cobertura ≥ 80% em domínio e aplicação. | Obrigatório |
| RNF-12 | **Portabilidade e execução local** | Dois modos a partir das mesmas imagens: (1) `docker compose up` para desenvolvimento; (2) um único script sobe o ambiente em Kubernetes local (kind, 3 nós) com Helm, de forma automática, sem passos manuais. Os mesmos charts são reutilizáveis na nuvem trocando apenas os `values`. | Obrigatório / requisito do projeto |
| RNF-13 | **Redundância** | Serviços .NET e gateway com ≥ 2 réplicas distribuídas entre nós, *probes*, *PodDisruptionBudget* e *rolling update*. A perda de 1 pod ou de 1 nó não interrompe o serviço. Dependências em alta disponibilidade conforme a [entrega em camadas](#7-entrega-em-camadas). | Requisito do projeto |

## 5. Rastreabilidade

| Requisito do enunciado | Coberto por |
|---|---|
| Serviço de controle de lançamentos | RF-01 a RF-04, RF-10 |
| Serviço do consolidado diário | RF-05 a RF-08 |
| Lançamentos não indisponível se o consolidado cair | RNF-01 |
| 50 req/s no consolidado, perda ≤ 5% | RNF-02, P1, P3 |
| Segurança no consumo de serviços (diferencial) | RF-09, RNF-08 |
| Monitoramento e observabilidade (diferencial) | RNF-09 |
| Execução local por qualquer desenvolvedor (README) | RNF-12 |
| Implantação automática, redundante e pensada para nuvem | RNF-12, RNF-13 |

## 6. Pontos em aberto

- Revisar as metas de SLO (P6) depois da medição real no teste de carga.
- Política de reprocessamento (RF-08): detalhar em ADR.

## 7. Entrega em camadas

A implantação em Kubernetes é entregue em duas camadas, para priorizar os serviços e não o tempo gasto em operadores.

| Camada | Escopo | Situação |
|---|---|---|
| **1 — Serviços redundantes** | Serviços .NET e gateway com redundância completa (RNF-13); PostgreSQL, RabbitMQ e Redis em instância única; empacotamento em Helm; script único de subida (RNF-12); teste de resiliência do RNF-01 no cluster. | Entrega do desafio |
| **2 — Dependências em alta disponibilidade** | PostgreSQL com primário e réplica (CloudNativePG, failover automático); RabbitMQ em 3 nós com *quorum queues*; Redis com réplica. | Documentada como evolução; implementada se houver tempo |

Decisões já tomadas: **Helm** como empacotamento; **kind** com 3 nós como cluster local; modo Compose mantido para desenvolvimento.

## 8. Extensão: Assistente de IA (v1 entregue, [19](19-assistente-de-ia.md))

Fora do escopo obrigatório do desafio; entrou depois do núcleo. RF-11 e RNF-14 a RNF-18 estão atendidos pela v1 (RF-12, a v2, segue como evolução). Ver [04 — Roadmap](04-roadmap.md) e [ADR-0011](adr/0011-assistente-de-ia-por-interface.md)).

| ID | Tipo | Requisito |
|---|---|---|
| RF-11 | Funcional | Assistente (Assistant) v1: a partir de texto em linguagem natural, propor um **rascunho** de lançamento (tipo, valor, `entryDate`, descrição) para revisão e confirmação do usuário. O assistente nunca grava. |
| RF-12 | Funcional | Assistente v2 (evolução): responder perguntas somente leitura sobre os saldos do próprio usuário, usando o token dele. |
| RNF-14 | Degradação graciosa | A indisponibilidade do Assistente ou do provedor de IA não afeta o registro nem a consulta (RNF-01 preservado); o formulário comum continua disponível. |
| RNF-15 | Latência | Rascunho: p95 < 5 s, com tempo máximo e mensagem clara em caso de estouro. |
| RNF-16 | Acurácia | Meta inicial de ≥ 90% de acerto nos campos principais do *golden set* em português, executado no CI, incluindo casos de injeção, ambiguidade e valores absurdos. |
| RNF-17 | Privacidade e custo | Envio apenas do texto digitado, sem `merchantId` nem dados pessoais; prompts não armazenados; limite de requisições por usuário e orçamento de custo monitorado. |
| RNF-18 | Execução local | Funciona sem chave paga: provedor real, modelo local opcional e *fake* determinístico atrás de uma interface. |
