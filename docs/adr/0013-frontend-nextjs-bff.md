# ADR-0013 — Frontend em Next.js, com o servidor chamando a API (BFF)

Status: Aceita
Data: 2026-11-22

## Contexto

O comerciante usa uma aplicação web autenticada pelo Keycloak. O token de acesso é uma credencial: quanto menos lugares o vissem, melhor. Também queremos o mesmo login para comerciante e auditor.

## Opções consideradas

| | Opção | A favor | Contra |
|---|---|---|---|
| **A** | Next.js (App Router) com *server components* e *server actions*; o servidor chama o Gateway com o token da sessão | O token fica num *cookie* criptografado e `HttpOnly`: scripts da página nunca o veem; nenhuma chamada à API parte do navegador (sem CORS); renderização no servidor; um projeto só | Exige um servidor Node para a interface; a sessão é do servidor |
| **B** | SPA React (Vite) chamando a API direto do navegador | Mais simples de hospedar (arquivos estáticos); sem servidor de interface | O token precisa viver no navegador (memória ou armazenamento): exposto a XSS; exige CORS e *refresh* no cliente |
| **C** | Angular | Estrutura rígida, bom para equipes grandes | Mesmo problema do token na SPA, ou precisa de um BFF à parte; mais cerimônia para uma interface pequena |
| **D** | Blazor / MVC no próprio .NET | Uma linguagem só no projeto inteiro; sessão no servidor também | Menos opções de bibliotecas de interface; mercado de front-end menor; acopla interface e back-end no mesmo ciclo |

## Decisão e por quê

**A (Next.js com o servidor como BFF).** A razão decisiva é de segurança: o token nunca chega ao navegador. As outras opções ou o expõem (B, C) ou trocam de ecossistema sem ganho para o caso (D). A interface é pequena e feita para quem não é técnico; a tela de Auditoria e o assistente reaproveitam o mesmo desenho.

## Quando reavaliar

Se a interface virar um produto com muitas telas e uma equipe de front-end própria, o BFF continua valendo; trocar o *framework* não muda a regra de que o token fica no servidor.
