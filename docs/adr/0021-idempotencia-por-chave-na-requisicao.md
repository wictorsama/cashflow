# ADR-0021 — Idempotência de escrita por `Idempotency-Key` guardada com o lançamento

Status: Aceita
Data: 2026-11-22

## Contexto

Um clique duplo, uma repetição do cliente ou do Gateway não pode registrar o mesmo lançamento duas vezes, e a resposta repetida deve ser a original.

## Opções consideradas

| | Opção | A favor | Contra |
|---|---|---|---|
| **A** | Chave de idempotência enviada pelo cliente, guardada na mesma transação do lançamento | Funciona com qualquer cliente; a resposta é reproduzida; concorrência resolvida pela chave primária | O cliente precisa enviar a chave |
| **B** | Chave natural única (por exemplo, valor + data + descrição) | Sem cabeçalho novo | Dois lançamentos legítimos iguais seriam recusados |
| **C** | Deduplicação no broker ou só no consumidor | Cobre o consumo | Não protege o registro, que é onde a duplicata nasce |
| **D** | Sem idempotência | Nada a fazer | Duplica dinheiro em qualquer falha de rede |

## Decisão e por quê

**A.** Chave obrigatória no `POST`, comparada com um *hash* do corpo (a mesma chave com outro corpo é erro 422); a resposta fica guardada e é devolvida na repetição, marcada. Do lado do consumo, o evento é deduplicado pelo identificador (registro de eventos). Verificado: 10 requisições simultâneas com a mesma chave criam **um** lançamento.

## Quando reavaliar

Se clientes de terceiros não puderem enviar a chave, oferecer uma chave derivada no Gateway.
