# 08 — Frontend (Next.js)

Aplicação web do comerciante: entrar, registrar lançamentos, estornar e acompanhar o saldo diário consolidado. Fica em `web/` e sobe junto com o restante em `http://localhost:3000` (ver [05](05-execucao-local.md)).

Usuário de teste local: `merchant1` / `local-dev-only` (definido no *realm* de desenvolvimento; não existe fora do ambiente local).

## Telas

| Rota | O que faz |
|---|---|
| `/entrar` | Entrada pelo Keycloak. A aplicação nunca vê a senha. |
| `/saldo` | Saldo acumulado em destaque (que "conta" até o valor), créditos, débitos e resultado do dia escolhido, proporção entradas × saídas, gráfico do saldo dos últimos dias, o fluxo de lançamentos do dia e a tabela dos últimos 7 dias. Atualiza sozinha a cada 10 s, e o indicador "ao vivo" pulsa a cada atualização. |
| `/lancamentos` | Assistente de IA em destaque, formulário de novo lançamento (os campos "acendem" quando o assistente os preenche), histórico com filtro e paginação, e o botão **Estornar**. |
| `/auditoria` | Só para o Auditor: consulta somente leitura (ver [15](15-papel-de-auditor.md)). |

## Design: "Pulso"

A interface usa um sistema de design próprio, o **Pulso**: visual de sala de controle, **escuro por padrão com tema claro** (alterna pelo botão do topo; a escolha fica no navegador e é aplicada antes da primeira pintura, sem piscar).

- **Variáveis de design** (cores, tipografia, espaçamentos, movimento) em `web/src/app/globals.css`; trocar a aparência é mexer nelas, não nas telas.
- **Sem fontes nem recursos externos:** só fontes do sistema (nada é baixado de terceiros; coerente com a privacidade e com o modo offline).
- **Logo CashFlow:** um "C" em forma de moeda atravessado por uma onda (o caixa e o fluxo), em `web/src/components/logo.tsx`.
- **Movimento com critério:** o valor conta até o saldo, o gráfico se desenha e o indicador pulsa; tudo respeita "reduzir movimento" do sistema operacional.
- **Acessibilidade:** contraste conferido nos dois temas, foco visível, navegação por teclado, gráfico com descrição em texto e valores também escritos (não só em cor).
- **Os dados continuam vindo da API, sem simulação:** nada na tela é inventado. A tela não exibe indicadores como o frescor do saldo ou um selo "trilha íntegra", porque a API ainda não expõe esses dados; ficam como evolução.

O saldo é *eventualmente consistente*: um lançamento novo aparece em poucos segundos (medido em menos de 1 s, ver [07](07-testes-de-carga-e-resiliencia.md)). A tela avisa isso e se atualiza sozinha. Se o serviço de saldo estiver fora do ar, a tela explica que os lançamentos continuam sendo aceitos (RNF-01), em vez de mostrar um erro técnico.

## Como a segurança funciona

```text
Navegador ──cookie de sessão──▶ Next.js (servidor) ──Bearer token──▶ Gateway ──▶ serviços
        ▲                              │
        └── login ◀── Keycloak ────────┘  (authorization code + PKCE, cliente público `cashflow-web`)
```

- **Authorization code com PKCE**, sem segredo no navegador nem na aplicação (cliente público). Os parâmetros `state` e `code_challenge` (S256) são usados em todo login.
- **O navegador nunca chama a API.** Todas as chamadas ao Gateway saem do servidor do Next.js, com o token do usuário. Por isso não há CORS a abrir e nenhum endereço de API exposto à página.
- **Onde fica o token:** em um cookie de sessão **criptografado e `HttpOnly`** (Auth.js). Scripts da página não conseguem lê-lo. A senha vive só no Keycloak. Ao expirar (5 min), o servidor o renova com o *refresh token*; se a renovação falhar, a pessoa volta para `/entrar`.
- **Sair** encerra a sessão da aplicação **e** a do Keycloak; sem isso, o próximo "Entrar" entraria sozinho.
- **Dois endereços do mesmo Keycloak:** o navegador o acessa por `http://localhost:8082` (também o emissor dos tokens), enquanto o servidor, dentro do cluster, o acessa pelo nome do serviço. Por isso os *endpoints* são configurados explicitamente (`web/src/auth.ts`).
- Cabeçalhos de segurança (`X-Frame-Options: DENY`, `nosniff`, `Referrer-Policy`) e sem `X-Powered-By`.

## Proteção contra duplicidade

Cada formulário envia um `Idempotency-Key`. Um clique duplo, uma nova tentativa depois de uma falha de rede ou um reenvio do navegador registram o lançamento **uma única vez**; depois de um sucesso a chave é renovada. A mesma regra vale para o estorno.

## Regras de entrada validadas na tela

Valor maior que zero com até duas casas, aceitando `1.234,56`, `1234,56` e `1234.56`; data válida e nunca no futuro; descrição até 200 caracteres. O servidor valida de novo (a tela é só conveniência). O "hoje" é o dia de São Paulo, a mesma regra da API.

## Testes e execução

```bash
cd web
npm ci
npm test            # 45 testes: leitura de valores e datas, o gráfico, o cliente da API (inclusive como Auditor) e a regra de papéis
npm run typecheck
npm run lint
npm run build
```

Os testes do cliente da API verificam, entre outros pontos, que o token vai no cabeçalho `Authorization`, que **nenhum cabeçalho de comerciante é enviado** (o comerciante vem do token) e que erros no formato RFC 9457 e falhas de rede viram mensagens tratáveis.

Para desenvolver contra o cluster local: `npm run dev` com `GATEWAY_URL=http://localhost:8080`, `KEYCLOAK_ISSUER=http://localhost:8082/realms/cashflow`, `AUTH_SECRET=<qualquer valor longo>` e `AUTH_URL=http://localhost:3000` (a porta 3000 do cluster precisa estar livre; pare o pod `web` ou use outra porta e ajuste a URI de retorno do cliente no *realm*).

## Tela do Auditor

Quem entra sem `merchant_id` e com comerciantes atribuídos (`audit_merchants` no token) é um **Auditor** ([15](15-papel-de-auditor.md)). A interface muda para ele:

- A barra mostra só **Auditoria** e "Auditor (somente leitura)"; as telas de Saldo e Lançamentos o redirecionam para a de Auditoria, porque exigem um comerciante próprio.
- A tela de **Auditoria** tem um seletor com **apenas os comerciantes atribuídos** e um dia; mostra o saldo e os lançamentos do dia daquele comerciante, sem nenhum botão que altere algo.
- O aplicativo pede os escopos de auditoria no login, mas **a decisão de acesso é da API**, a cada chamada: a tela só escolhe o que mostrar. Um comerciante que abrir `/auditoria` à mão é mandado de volta ao Saldo.
- Verificado no navegador contra o cluster local: login como `auditor1`, tela de Auditoria com saldo e lançamentos do comerciante atribuído, `/saldo` redirecionando para a Auditoria; e login como `merchant1` com a navegação de comerciante.

## Limitações atuais

- Não há testes de ponta a ponta automatizados no navegador (Playwright); o fluxo de login, registro, estorno e saída foi verificado manualmente no cluster local.
- HTTP sem TLS e segredo de sessão descartável: apenas para o ambiente local; na nuvem entram TLS na borda e segredo vindo de um gerenciador de segredos.
- A interface está só em português do Brasil.
