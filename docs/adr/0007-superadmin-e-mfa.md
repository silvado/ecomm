# ADR-0007 — Superadmin da plataforma: conta, MFA e auditoria

- **Status:** Aceito
- **Data:** 2026-10-13
- **Requisitos:** RF07 CA3, RF37, RF40, RNF01, RNF05

## Contexto

Até aqui as operações de plataforma (criar loja, trocar plano, ligar a loja personalizada) só existem como comandos do migrador, rodados no servidor (Q20). Para operar os primeiros clientes, a equipe da plataforma precisa de um painel web. O RF07 CA3 exige que o superadmin seja um papel separado, com MFA obrigatório, e que todo acesso a dados de uma loja fique em log de auditoria.

As contas de hoje (`user_accounts` + `tenant_memberships`) são de lojistas: o JWT do painel carrega a loja escolhida (`tid`) e as permissões vêm do papel na loja.

## Decisões (aprovadas pelo dono do produto em 2026-10-13)

### 1. Conta de superadmin separada

| Opção | Prós | Contras |
|---|---|---|
| **A. Tabela própria (`platform_admins`), login e token próprios** | Separação forte: vazar a senha de uma loja não dá acesso à plataforma; MFA não afeta lojistas; fácil de auditar | Quem também é lojista tem duas contas; um pouco de código repetido (reaproveita hash de senha e bloqueio) |
| B. Mesma `user_accounts` com marca de superadmin | Um login só | A conta de loja vira alvo de plataforma; MFA teria de valer para a conta toda; mistura permissões |

**Escolhida A.** Login em `/api/plataforma/auth/*`; JWT com **audiência própria** (`ecommerce-plataforma`) e a mesma chave de assinatura. Endpoints de plataforma exigem essa audiência e MFA concluído; os do painel de loja recusam o token de plataforma, e vice-versa. O primeiro superadmin é criado pelo migrador (`criar-superadmin --email`), com senha provisória mostrada uma vez, como no `criar-loja`.

### 2. MFA por TOTP com códigos de recuperação

| Opção | Prós | Contras |
|---|---|---|
| **A. TOTP (RFC 6238) + 10 códigos de recuperação** | Sem serviço externo; funciona com qualquer app autenticador | Cadastro um pouco mais trabalhoso |
| B. Código por e-mail | Simples para o usuário | O e-mail vira o segundo fator (fraco) e depende do provedor de e-mail |
| C. Passkey (WebAuthn) | Melhor proteção contra phishing | Mais complexo; fica como evolução |

**Escolhida A.** Parâmetros padrão da RFC 6238: HMAC-SHA1, 6 dígitos, passo de 30 s, aceita ±1 passo; um código já usado não vale de novo (guarda o último passo aceito). Segredo de 20 bytes, cifrado com a chave-mestra do cofre (`EnvelopeCrypto`, contexto ligado ao id do superadmin). Implementação própria, sem dependência nova, validada contra os vetores de teste do apêndice B da [RFC 6238](https://www.rfc-editor.org/rfc/rfc6238#appendix-B). O cadastro usa o URI `otpauth://totp/...` ([formato de chave do Google Authenticator](https://github.com/google/google-authenticator/wiki/Key-Uri-Format)) mostrado como QR code e como texto. Códigos de recuperação: 10, de uso único, guardados só como hash.

Sem MFA cadastrado, o token de plataforma só libera trocar a senha provisória e cadastrar o MFA. Login: senha → token de etapa (5 min, só para o código) → código TOTP ou de recuperação → token completo.

### 3. Superadmin enxerga só dados da plataforma (nesta etapa)

Lojas, planos, domínios, usuários e vínculos, e status ficam no banco `plataforma`. Peças, pedidos e clientes **não** ficam visíveis ao superadmin. Um modo "entrar como a loja" (somente leitura, auditado) seria um item futuro, com ADR próprio, porque mexe no isolamento do ADR-0001.

## Auditoria

Tabela `platform_audit_log` no banco `plataforma`, somente inclusão: a migração tira `UPDATE`, `DELETE` e `TRUNCATE` do papel da aplicação. Registra: superadmin, quando, ação, loja alvo, detalhes já redigidos, IP e `correlation_id`. Gravam ali:
- login, falha de MFA, uso de código de recuperação e cadastro de MFA;
- toda ação de superadmin (criar loja, trocar plano, suspender, reativar, loja personalizada);
- toda leitura do detalhe de uma loja.

## Consequências

- O papel `app_platform` previsto no ADR-0001 continua adiado (**HIPÓTESE**): a API segue com `app_user` no banco `plataforma`, e a separação é feita pela audiência do token e pela auditoria. Rever quando houver leitura de dados de loja pelo superadmin.
- O painel de plataforma fica no mesmo app `admin`, em `/plataforma`, com login e layout próprios.
- A troca dos limites de cada plano continua por migração; a tela de planos é só leitura.
