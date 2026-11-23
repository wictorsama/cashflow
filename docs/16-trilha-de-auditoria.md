# 16 — Trilha de auditoria à prova de adulteração

Fecha o item 4 do [modelo de ameaças](10-modelo-de-ameacas.md): *"alguém com acesso ao banco altera dados diretamente"*. A reconciliação ([13](13-reconciliacao.md)) confia no banco de Lançamentos como verdade, então um lançamento alterado ou apagado direto na tabela passava despercebido. Esta trilha faz essa mudança **aparecer**.

Ela não impede que alguém com poder total sobre o banco altere os dados. O que ela garante é que **isso deixa rastro que o sistema encontra sozinho**.

## Como funciona

Cada lançamento registrado e cada estorno acrescenta **um elo** à cadeia do comerciante, na **mesma transação** do lançamento (ou os dois são gravados, ou nenhum):

```
elo n = { comerciante, lançamento, evento, ação, quem fez, quando, tipo, valor, dia, hash do elo n-1 }
hash n = SHA-256( todos esses campos, em forma textual fixa e versionada )
```

- **Encadeamento:** cada elo carrega o hash do anterior. Alterar ou remover qualquer elo muda o seu hash e quebra todos os que vêm depois.
- **Uma cadeia por comerciante.** Escritas do mesmo comerciante esperam a sua vez por um *lock* consultivo do PostgreSQL (`pg_advisory_xact_lock`) até o fim da transação; comerciantes diferentes não esperam um ao outro. Há teste com 25 escritas simultâneas do mesmo comerciante: uma única cadeia, sem dois elos com o mesmo pai.
- **Só cresce:** um *trigger* recusa `UPDATE`, `DELETE` e `TRUNCATE` na tabela `audit_log`. Quem tem poder de administrador pode removê-lo, mas aí a cadeia mostra a alteração.
- **Uma requisição repetida** (mesma `Idempotency-Key`) não cria um segundo elo: testado.

## O que a verificação encontra

| Achado | Significa |
|---|---|
| **Cadeia quebrada** (conteúdo) | Um elo foi **editado** depois de gravado |
| **Cadeia quebrada** (não segue o anterior) | Um elo foi **removido, inserido ou reordenado** |
| **Lançamento sem elo** | Um lançamento foi **inserido direto** na tabela |
| **Elo sem lançamento** | Um lançamento foi **apagado direto** da tabela |
| **Lançamento alterado** | Um lançamento **não diz mais o que a cadeia registrou** (valor, tipo, dia ou comerciante editados) |

Roda **a cada 10 minutos** em uma réplica por vez (lock consultivo), e sob demanda em `GET /v1/audit/verify` (escopo `entries:reconcile`; o Gateway não roteia, só o cluster alcança). Um problema gera a métrica `cashflow_audit_chain_issues`, o log crítico `AUDIT CHAIN BROKEN` e o alerta **`AuditChainBroken`** (crítico); se a verificação parar, **`AuditVerificationStopped`**. Não corrige nada sozinha: o que fazer é decisão de pessoas.

## Provas

- **15 testes de integração** com PostgreSQL real: a cadeia se monta e aponta para o anterior; uma por comerciante; 25 escritas simultâneas; escrita que falha não deixa elo; o *trigger* recusa os três comandos; e cada tipo de adulteração acima é encontrada (valor editado, lançamento apagado, lançamento forjado, elo editado, elo removido do meio); lançamentos anteriores à cadeia são ignorados; o hash muda com cada campo coberto; o tempo é truncado ao microssegundo (como o PostgreSQL guarda).
- **No cluster real:** edição de um valor na tabela → `EntryAltered`; valor restaurado → íntegra; `UPDATE` e `DELETE` no log → `audit_log is append-only`.

## Limitações

- **Quem apaga o lançamento *e* o último elo do comerciante** (desligando o *trigger*) não é visto: a cadeia sozinha não prova o que **foi cortado do fim**. A defesa é **ancorar o hash da ponta da cadeia fora do banco** (um armazenamento só de escrita, de outro responsável) e conferi-lo; fica como evolução.
- **Lançamentos anteriores à trilha** (antes da migração) não estão na cadeia e não são verificados; a data de início fica em `audit_meta`.
- **A verificação lê tudo** a cada rodada: serve para o volume do desafio. Em volume alto, passa a ser incremental (a partir do último elo já verificado).
- **O estorno altera o lançamento original** (marca `reversed_by`); essa marca não está no hash (o original é protegido por valor, tipo, dia e comerciante).
- **As leituras do Auditor** ([15](15-papel-de-auditor.md)) continuam em log e métrica, não nesta cadeia.
