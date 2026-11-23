# ADR-0014 — CI/CD com GitHub Actions

Status: Aceita
Data: 2026-11-22

## Contexto

O repositório é público no GitHub e o avaliador precisa ver build, testes, segurança e publicação sem montar infraestrutura própria.

## Opções consideradas

| | Opção | A favor | Contra |
|---|---|---|---|
| **A** | GitHub Actions | Dentro do repositório; sem servidor para manter; ações oficiais para CodeQL, SBOM e proveniência; gratuito em repositório público | Preso à plataforma; executores gratuitos têm pouca memória (afeta o teste e2e em kind) |
| **B** | GitLab CI | Pipeline e registro de imagens no mesmo produto; bom para ambientes fechados | Exigiria espelhar o repositório; o desafio pede GitHub |
| **C** | Jenkins | Máxima flexibilidade e *plugins* | Servidor para operar e atualizar; configuração fora do repositório (a menos de Jenkinsfile); mais superfície de ataque |
| **D** | Azure DevOps Pipelines | Integração com o ecossistema Microsoft e com o AKS | Mais uma plataforma a contratar; não é o que o desafio usa |

## Decisão e por quê

**A (GitHub Actions).** O código já está no GitHub, e os pipelines (build, testes com *gate* de cobertura, CodeQL, gitleaks, Trivy, kubeconform, e2e em kind, publicação com proveniência) ficam versionados junto do código, com ações fixadas por *hash* de *commit*. Os `scripts/*.ps1` rodam igual na máquina do desenvolvedor e no pipeline, então migrar de plataforma custa reescrever o YAML, não os testes.

## Quando reavaliar

Se a empresa padronizar em outra plataforma de CI, os passos se repetem; os scripts e o chart não mudam.
