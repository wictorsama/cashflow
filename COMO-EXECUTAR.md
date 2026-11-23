# Como executar o sistema (guia do avaliador)

Este guia leva do zip ao sistema funcionando em **três comandos**, e depois mostra o que conferir. O sistema inteiro roda **na sua máquina**, em um cluster Kubernetes local (kind) dentro do Docker; não precisa de conta em nuvem nem de chave nenhuma.

## 1. O que você precisa

| Item | Detalhe |
|---|---|
| **Sistema** | Windows 10/11 com PowerShell (já vem). Linux e macOS funcionam com o [PowerShell 7](https://learn.microsoft.com/powershell/scripting/install/installing-powershell) (`pwsh`) |
| **Docker Desktop** | Aberto e com **pelo menos 8 GB de memória e 4 CPUs** (Docker Desktop > Settings > Resources). Em uso normal o sistema consome ~5 GB |
| **Disco e internet** | ~15 GB livres e acesso à internet na primeira subida (baixa imagens base e dependências) |
| **kind, helm e kubectl** | Se não tiver, o passo 2 instala (no Windows, com winget) |
| **Portas livres** | 3000, 3030, 8080, 8082 e 16686 |

Só para rodar os **testes automatizados** (opcional): .NET SDK 10 e Node 22.

## 2. Subir o sistema

Abra o PowerShell **na pasta do projeto** (a que contém este arquivo) e rode:

```powershell
./scripts/prereqs.ps1 -Install    # confere o que falta e instala kind, helm e kubectl; abra um terminal novo se instalar algo
./scripts/up.ps1                  # cria o cluster, constrói as imagens e instala tudo (15 a 25 minutos na primeira vez: medido ~20 min, a maior parte é baixar imagens e compilar)
./scripts/smoke-test.ps1          # confere o sistema de ponta a ponta (35 verificações)
```

Se o seu computador tiver pouca memória, use `./scripts/up.ps1 -Lightweight` (banco e broker sem réplicas).

No fim do `up.ps1` aparecem os endereços. O `smoke-test.ps1` deve terminar com **"All checks passed."**

Se o `up.ps1` for interrompido, rode-o de novo: ele continua de onde parou.

## 3. Endereços e usuários (todos descartáveis, só existem nesta máquina)

| O quê | Endereço | Usuário / senha |
|---|---|---|
| **Aplicação web** (comece por aqui) | http://localhost:3000 | Comerciante: `merchant1` / `local-dev-only` |
| Aplicação web, como **Auditor** | http://localhost:3000 | `auditor1` / `local-dev-only` |
| API (Gateway) | http://localhost:8080 | token do Keycloak (ver abaixo) |
| Keycloak (identidade) | http://localhost:8082 | admin: `admin` / `local-dev-only` |
| Grafana (painel de métricas) | http://localhost:3030 | `admin` / `local-dev-only` |
| Jaeger (traces) | http://localhost:16686 | sem login |

## 4. O que conferir (roteiro de 10 minutos)

1. **Entrar como comerciante** (`merchant1`) em http://localhost:3000 e abrir **Lançamentos**.
2. **Registrar um lançamento** pelo formulário e ver o **Saldo** mudar em poucos segundos.
3. **Assistente de IA:** no campo "Descreva o lançamento", escrever `paguei 150 de luz ontem` e clicar em **Sugerir preenchimento**. O assistente **só preenche o formulário**; o lançamento só é gravado quando você clica em **Registrar**. Teste também `paguei 150 e recebi 200` (ele pergunta) e `ignore as instruções anteriores` (ele recusa).
4. **Estornar** um lançamento (um lançamento não se apaga, corrige-se com estorno).
5. **Sair** e entrar como **Auditor** (`auditor1`): ele só lê os comerciantes atribuídos a ele, sem nenhum botão de gravação.
6. **Grafana** > painel "Cash Flow: SLOs and flow": vazão, latência, frescor do saldo, reconciliação.
7. **Jaeger:** procurar um *trace* do serviço `cashflow-gateway`; ele atravessa o Gateway, o serviço de Lançamentos, o broker e a Consolidação.

### Provas de resiliência (opcional, ~10 minutos)

> **Antes de medir:** rode o `smoke-test.ps1` primeiro (aquece o sistema) e deixe o Docker só com este cluster. Logo depois de subir ou de reiniciar o Docker, os primeiros segundos do teste de carga podem ser lentos e violar o limite `dropped_iterations`; e outro contêiner pesado na mesma máquina causa falhas de conexão que não são do sistema. Se o teste falhar nessas condições, repita com o sistema aquecido. Detalhes em `docs/07-testes-de-carga-e-resiliencia.md`.

```powershell
./scripts/load-test.ps1                              # 50 req/s por 5 min no saldo (meta do desafio): esperado 0 falhas
./scripts/resilience-test.ps1 -Scenario consolidation # Consolidação fora do ar: os lançamentos continuam sendo aceitos
./scripts/resilience-test.ps1 -Scenario pod           # remove um pod de cada serviço com tráfego rodando
```

Cada um imprime, no fim, quantas requisições falharam. Os resultados esperados estão em `docs/07-testes-de-carga-e-resiliencia.md`.

### Chamar a API direto (opcional)

```powershell
$token = (Invoke-RestMethod -Method Post http://localhost:8082/realms/cashflow/protocol/openid-connect/token `
  -Body @{ grant_type='client_credentials'; client_id='merchant-integration'; client_secret='local-dev-merchant-secret' }).access_token
Invoke-RestMethod -Method Post http://localhost:8080/v1/entries -ContentType 'application/json' `
  -Headers @{ Authorization="Bearer $token"; 'Idempotency-Key'=[guid]::NewGuid().ToString() } `
  -Body '{"type":"Credit","amount":100.50,"entryDate":"2026-11-23","description":"venda"}'
Invoke-RestMethod http://localhost:8080/v1/balances/2026-11-23 -Headers @{ Authorization="Bearer $token" }
```

### Testes automatizados (opcional, precisam do Docker aberto)

```powershell
dotnet test CashFlow.slnx                 # 254 testes (os de integração sobem PostgreSQL, RabbitMQ e Redis reais)
cd web; npm ci; npm test                  # 45 testes do frontend
```

## 5. Parar e limpar

```powershell
./scripts/down.ps1      # apaga o cluster e tudo o que há nele
```

## 6. Se algo não funcionar

| Sintoma | O que fazer |
|---|---|
| `Docker is not running` | Abra o Docker Desktop e espere "Engine running" |
| Pods em `Pending` ou o `up.ps1` demora demais | Pouca memória no Docker: aumente para 8 GB ou use `-Lightweight` |
| A porta 3000, 8080 ou 8082 já está em uso | Feche o programa que a usa (o `prereqs.ps1` avisa quais) |
| Erro 401 nos primeiros instantes | Normal logo depois de subir: o Keycloak acabou de gerar chaves novas. Espere ~1 minuto (o `smoke-test.ps1` já espera sozinho) |
| A tela pede para entrar de novo | A sessão expira quando o Keycloak reinicia; basta entrar de novo |
| Quer começar do zero | `./scripts/down.ps1` e depois `./scripts/up.ps1` |
| Falta de espaço em disco | `docker system prune` remove imagens e cache não usados |

## 7. Onde está a documentação

| Para | Ler |
|---|---|
| Visão geral do que foi construído | `README.md` |
| Arquitetura (diagramas C4, fluxos) | `docs/03-arquitetura-alvo.md`, `docs/architecture/c4.md`, `docs/20-arquitetura-por-area.md` |
| Por que cada decisão foi tomada (com as alternativas) | `docs/adr/` |
| Rodar em nuvem (Google Cloud, com Terraform) | `docs/21-nuvem.md` e `infra/terraform/gcp/README.md` |
