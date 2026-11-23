# 15 — Papel de Auditor (RF-09)

O Auditor **lê** lançamentos e saldos de comerciantes que não são o seu. Isso é o oposto do que o resto do sistema garante (cada um só vê o que é seu), então a regra tem que ser estreita e deixar rastro.

## A regra

| Quem | O que pode | Como o sistema decide |
|---|---|---|
| **Comerciante** | Ler e escrever só o **próprio** | O `merchant_id` vem do **token**, nunca da requisição. Se pedir outro comerciante, é **recusado (403)**, não respondido em silêncio com os próprios dados (isso esconderia a tentativa). |
| **Auditor** | **Somente leitura** dos comerciantes **atribuídos a ele** | Precisa de **três coisas juntas**: não ter `merchant_id` próprio; ter o escopo `entries:audit` / `balances:audit`; e o comerciante pedido estar na lista **`audit_merchants`** do token. |
| Todos os outros | Nada disso | Escopo de outro recurso, ou sem atribuição, é 403. |

Decisões que valem ser explicadas:

- **O auditor não lê "tudo".** O enunciado deixa a premissa aberta; a atribuição por comerciante é mais estrita e mais segura: um auditor comprometido expõe os comerciantes dele, não a base inteira. Para dar acesso a mais comerciantes, o administrador do Keycloak altera o atributo; o código não muda.
- **A atribuição vive no token** (emitida pelo Keycloak a partir de um atributo do usuário). O chamador não consegue alterá-la: o token é assinado.
- **Cada requisição nomeia o comerciante** (`?merchantId=`), mesmo que haja só um atribuído. Nada de adivinhar: o rastro de auditoria fica exato. Sem o parâmetro: 400.
- **Somente leitura por construção, não por promessa.** Toda escrita (registrar, estornar) exige um comerciante próprio; o auditor não tem. Mesmo um token de auditor com o escopo de escrita é recusado (há teste). Reprocessar e reconciliar exigem escopos administrativos que o auditor não tem.
- **Um token que tem `merchant_id` é comerciante, e só comerciante**: não vira auditor por carregar também a atribuição.

## Como se usa

```
GET /v1/balances/2026-11-17?merchantId=<id>                  scope balances:audit
GET /v1/balances?merchantId=<id>&from=...&to=...             scope balances:audit
GET /v1/entries?merchantId=<id>&from=...&to=...              scope entries:audit
GET /v1/entries/{id}?merchantId=<id>                         scope entries:audit
```

As mesmas rotas do comerciante, com o parâmetro a mais. O Gateway não muda: valida o token como sempre, e cada serviço aplica a regra (defesa em profundidade).

No ambiente local do Keycloak:

- Cliente `auditor-integration` (credenciais de cliente, para ferramentas) e usuário `auditor1` (login pela tela, com os escopos de auditoria **opcionais** e não padrão: um comerciante comum não os recebe no token). Ambos atribuídos ao comerciante de demonstração.

## Tela

A aplicação web tem a tela **Auditoria** (`/auditoria`) para quem é Auditor: seletor só dos comerciantes atribuídos, dia, saldo e lançamentos, tudo somente leitura. Ver [08](08-frontend.md#tela-do-auditor).

## Trilha de auditoria

Toda leitura de um auditor, e toda recusa, gera uma linha de log estruturada com o `sub` do auditor, o comerciante, o recurso, o `TraceId` e o `X-Correlation-Id`:

```
AUDIT access granted: <auditor> read balances of merchant <id>.
AUDIT access denied:  <auditor> asked for balances: This merchant is not assigned to the auditor.
```

E a métrica `cashflow.audit.accesses{resource, outcome}` (sem o comerciante como rótulo, para a cardinalidade não crescer). Leituras do comerciante dele mesmo não são registradas aqui: é o caso normal.

## Provas

- **14 testes unitários** da regra ([CallerAccessTests](../tests/CashFlow.Security.Tests/CallerAccessTests.cs)): comerciante no próprio e em outro, token com `merchant_id` e atribuição, claim malformado ou com GUID vazio, auditor atribuído e não atribuído, sem atribuição, sem informar o comerciante, escopo de outro recurso, casamento exato de escopo.
- **4 testes de API em Lançamentos e 4 em Consolidação**, com PostgreSQL real: o auditor lê o atribuído e só ele; não escreve nem estorna mesmo com o escopo de escrita; não reprocessa nem reconcilia; o comerciante não lê outro nomeando-o.
- **No cluster real**, 7 verificações novas no teste de fumaça: o auditor lê saldo e lançamentos do comerciante atribuído; 400 sem o comerciante; 403 para um não atribuído; 403 ao tentar escrever e ao tentar reprocessar; 403 para o comerciante que nomeia outro.

## Limitações

- **As leituras do Auditor ficam em log e métrica**, não na [cadeia à prova de adulteração](16-trilha-de-auditoria.md) (que cobre escritas). Quem acessa os logs pode apagá-los.
- **A atribuição é estática no token** (vale por até 5 minutos, a vida do token). Revogar uma atribuição vale a partir do próximo token; para corte imediato, revogar a sessão no Keycloak.
- **Não há aprovação nem prazo** para o acesso (acesso temporário, justificativa). Em um ambiente regulado, entraria.
- **Dados pessoais:** os lançamentos têm descrição livre. Mascarar campos para o auditor é decisão de LGPD ainda não tomada.
