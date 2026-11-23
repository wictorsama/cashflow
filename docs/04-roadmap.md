# 04 — Roadmap

> Convenção: documentação em português; termos de domínio como *Nome (Name)*; código em inglês.

O roadmap separa o **escopo obrigatório do desafio** das **extensões planejadas**. A ordem das fases é a de entrega; dentro de uma fase, os itens podem andar em paralelo.

## Fases

| Fase | Escopo | Situação |
|---|---|---|
| **0 — Documentação base** | Mapeamento de domínios, requisitos, arquitetura alvo, C4 e ADRs 0001–0011 | Concluída |
| **1 — Núcleo** | Solução .NET; serviço de Lançamentos (domínio, Outbox); serviço de Consolidação (consumidor idempotente, projeção, cache); testes unitários e de integração; CI | **Concluída** (o Compose foi substituído pelo próprio kind, com o mesmo chart da nuvem) |
| **2 — Plataforma** | Segurança (Keycloak, Gateway YARP, escopos); observabilidade (OpenTelemetry, Prometheus, Grafana, Jaeger); Helm + kind, camada 1 (serviços redundantes); script único de subida; teste de carga (k6) e teste de resiliência; frontend Next.js | **Concluída** |
| **3 — Documentos de diferencial** | CI/CD completo; modelo de ameaças (STRIDE, OWASP); estimativa de custos; arquitetura de transição (legado); README final com execução local | **Concluída** ([09](09-ci-cd.md), [10](10-modelo-de-ameacas.md), [11](11-arquitetura-de-transicao.md), [12](12-estimativa-de-custos.md)) |
| **4 — Extensão: Assistente de IA (v1)** | Rascunho de lançamento a partir de texto em linguagem natural, com confirmação do usuário ([19](19-assistente-de-ia.md)) | **Concluída** (v1; o provedor Claude não foi exercitado sem chave) |
| **5 — Evoluções** | Assistente v2 (perguntas sobre saldos); mTLS; GitOps; Terraform para AWS e Azure (o do Google Cloud existe); saldos materializados; multimoeda; categorias; conciliação bancária | Futura |


## Extensão: Assistente de IA (entregue na v1)

Fora do escopo obrigatório do desafio; entrou **depois** do núcleo e dos documentos de diferencial, como um diferencial adicional. Implementação, defesas e medições em [19 — Assistente de IA](19-assistente-de-ia.md).

### Escopo

| Versão | Funcionalidade | Fase |
|---|---|---|
| **v1** | O comerciante descreve o lançamento em linguagem natural ("paguei 150 de luz ontem") e a IA devolve um **rascunho** (tipo, valor, data, descrição). O usuário revisa e **confirma**; só então o fluxo normal (`POST /v1/entries`) grava. | 4 |
| **v2** | Perguntas somente leitura sobre os próprios saldos, usando o token do usuário. | 5 |

Interface: um campo "descreva o lançamento" dentro da tela de novo lançamento, e não um chat separado.

### Princípios

1. **A IA nunca grava.** Só propõe rascunho; a gravação é do fluxo normal, com o token do usuário e a confirmação dele.
2. **Fora do caminho crítico.** O Assistente é um serviço separado e sem banco. Se ele ou o provedor caírem, o formulário comum segue funcionando (RNF-01 preservado).
3. **Funciona sem chave paga.** Provedor de IA atrás de uma interface, com três implementações: provedor real (padrão: API do Claude), modelo local opcional e um *fake* determinístico para testes e desenvolvimento.
4. **Saída não confiável.** Esquema JSON fixo, validado pelas mesmas regras do domínio antes de virar rascunho; defesa contra *prompt injection*; frases ambíguas geram pergunta ao usuário, não palpite.
5. **Privacidade (LGPD).** Envio apenas do texto digitado, sem `merchantId` nem dados pessoais; prompts não são armazenados.
6. **Específico do sistema.** *System prompt* com o glossário do domínio, esquema e ferramentas fixos e um conjunto de testes de referência (*golden set*) em português, executado no CI.

### Requisitos da extensão

Ver [02 — Requisitos, seção 8](02-requisitos.md#8-extensão-planejada-assistente-de-ia) e [ADR-0011](adr/0011-assistente-de-ia-por-interface.md).

### Impacto na documentação (quando a fase 4 começar)

- Contêiner novo no C4 (nível 2) e componentes do Assistente (nível 3).
- Seção de ameaças específicas de IA (OWASP Top 10 para LLMs) no modelo de ameaças.
- Métricas de IA na observabilidade: tokens, latência, taxa de rejeição pela validação, custo estimado.
- Custo de IA na estimativa de custos.
