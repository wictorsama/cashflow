# ADR-0017 — Trilha de auditoria com encadeamento de hashes

Status: Aceita
Data: 2026-11-22

## Contexto

O modelo de ameaças apontava que alguém com acesso ao banco podia alterar ou apagar lançamentos sem deixar rastro, e a reconciliação confia no banco de Lançamentos como verdade.

## Opções consideradas

| | Opção | A favor | Contra |
|---|---|---|---|
| **A** | Tabela de auditoria comum (uma linha por operação) | Simples | Quem altera o lançamento altera também a auditoria: nada denuncia |
| **B** | Event sourcing completo (o estado é a soma dos eventos) | Auditoria inerente | Muda todo o modelo e o custo de consulta; mais complexo que o necessário (ver ADR-0010) |
| **C** | Livro-razão gerenciado de um provedor | Imutabilidade como serviço | Específico de um provedor (e alguns foram descontinuados); não roda localmente |
| **D** | Cadeia de hashes por comerciante, gravada na mesma transação, com tabela só de inserção | Qualquer edição, remoção ou inserção fora do serviço quebra a cadeia e é achada por um verificador periódico; portátil; barata | Não impede, só **denuncia**; quem apaga o lançamento *e* o último elo não é visto sem uma âncora externa |

## Decisão e por quê

**D.** É a menor mudança que torna a adulteração **visível e automática**, sem trocar o modelo de dados. Fica explícito o que ela não faz (impedir) e o que falta (âncora externa do hash da ponta). Ver [16](../16-trilha-de-auditoria.md).

## Quando reavaliar

Quando houver exigência regulatória de imutabilidade, ancorar a ponta da cadeia em armazenamento só de escrita de outro responsável, ou adotar o livro-razão do provedor escolhido.
