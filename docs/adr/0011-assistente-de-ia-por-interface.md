# ADR-0011 — Assistente de IA atrás de uma interface, sem poder de escrita

Status: Aceita e implementada na v1 (fase 4 do [roadmap](../04-roadmap.md); ver [19](../19-assistente-de-ia.md)). Os provedores `rules` e `claude` existem; o modelo local ficou como possibilidade da mesma interface.
Data: 2026-11-16

## Contexto

Queremos, como diferencial, um assistente que ajude o comerciante a preencher lançamentos a partir de linguagem natural. Modelos de linguagem podem errar, ser manipulados por *prompt injection* e depender de um provedor externo (custo, disponibilidade, privacidade). Os desenvolvedores também precisam rodar o projeto sem chave paga, e o assistente **não pode comprometer** o isolamento de falha do núcleo (RNF-01).

## Decisão

- Novo contexto **Assistente (Assistant)**: serviço .NET sem estado e sem banco, fora do caminho de escrita.
- **A IA nunca grava.** Devolve apenas um **rascunho** validado; o usuário confirma e o fluxo normal (`POST /v1/entries`) grava, com o token do usuário.
- **Provedor de LLM atrás de uma interface**, com três implementações: provedor real (padrão: API do Claude), modelo local opcional e *fake* determinístico (testes e desenvolvimento).
- **Saída estruturada** (esquema JSON fixo) e validada pelas regras do domínio; ambiguidade gera pergunta, não palpite.
- **Privacidade:** envio do mínimo (texto digitado), sem `merchantId` nem dados pessoais; sem armazenar prompts.
- **v2 (leitura de saldos):** o Assistente consulta o Consolidado com o **token do próprio usuário**, nunca com credencial de serviço ampla, para evitar o problema do *confused deputy*.
- **Qualidade medida:** *golden set* em português executado no CI, incluindo casos de segurança.

## Alternativas consideradas

| Alternativa | Motivo do descarte |
|---|---|
| IA com poder de gravar lançamentos | Erros do modelo virariam lançamentos financeiros incorretos; amplia a superfície de ataque. |
| Chamar o provedor diretamente do frontend | Expõe chave e prompts; sem validação nem observabilidade no servidor. |
| Acoplar o provedor sem interface | Exige chave paga para desenvolver; dificulta testes e troca de fornecedor. |
| IA embutida no serviço de Lançamentos | Acopla a disponibilidade do núcleo a um serviço externo (viola RNF-01). |

## Consequências

- (+) O núcleo permanece independente da IA; falha do assistente degrada só a conveniência.
- (+) Testável e executável localmente sem chave; fornecedor substituível.
- (+) Risco de erro do modelo contido pela confirmação humana e pela validação de domínio.
- (−) Um serviço e um conjunto de testes de avaliação a mais para manter.
- (−) Custo e latência por chamada; controlados por limites, tempo máximo e métricas.

## Escolha do provedor (opções com prós e contras)

| | Opção | A favor | Contra |
|---|---|---|---|
| **A** | **Regras determinísticas em português** (`rules`) | Sem rede, sem chave, sem custo, mesma resposta sempre; roda local e no CI | Entende só as formas que foram escritas; sem tolerância a frases livres |
| **B** | **API de um modelo hospedado** (Claude, por chamada de ferramenta forçada com esquema fixo) | Entende frases livres; saída estruturada | Custo por chamada, latência, dependência externa, a frase sai do sistema (LGPD) |
| **C** | Outra API hospedada (por exemplo, OpenAI) | Mesmas vantagens de B | Mesmas desvantagens de B; trocar é só implementar a mesma interface |
| **D** | Modelo local (Ollama ou similar) | A frase não sai do ambiente; sem custo por chamada | Exige hardware e operação; qualidade menor em modelos pequenos; não foi construído |

**Decisão:** **A como padrão e B como opção ligável por configuração**, ambos atrás da mesma interface (`IEntryInterpreter`); C e D cabem na mesma interface sem mudar o resto. O padrão `rules` existe para o sistema nunca depender de chave paga (RNF-18), e a segurança não depende do provedor: **toda saída é revalidada pelas regras do domínio**, e a IA não grava. O provedor B não foi exercitado com chave real; ver [19](../19-assistente-de-ia.md#limitações).
