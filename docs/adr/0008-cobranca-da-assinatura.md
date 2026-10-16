# ADR-0008 — Cobrança da assinatura das lojas

- **Status:** Aceito (gateway real em aberto — Q05)
- **Data:** 2026-10-16
- **Requisitos:** RF38, RF05, RF37, RNF05

## Contexto

O RF38 pede cobrança da mensalidade e da taxa de implantação pelo gateway **da plataforma** (não o da loja, que recebe dos compradores — RF15), com bloqueio gradual por atraso e volta automática ao pagar. O gateway ainda não foi escolhido (Q05: Asaas, Iugu, Mercado Pago assinaturas…), e não há tabela de preços oficial.

## Decisões

### 1. Gateway atrás de `ISubscriptionBilling`, com adaptador simulado

Mesma forma do `IPaymentGateway` (regra 6 do CLAUDE.md): criar cobrança (devolve a página de pagamento), cancelar, conferir a assinatura do webhook e **consultar** a cobrança. Hoje só existe `FakeSubscriptionBilling` (sem estado: o id da cobrança carrega fatura, valor e situação), ligado por `Billing:UseFakeGateway`. A API **recusa subir em produção** com ele. Sem gateway configurado, o painel não oferece "Pagar" e a equipe registra os pagamentos à mão (Pix, transferência). O adaptador real entra quando Q05 for decidida, citando a documentação oficial (regra 8), com credenciais da plataforma em variáveis de ambiente (regra 3).

### 2. Dados no banco `plataforma`

Assinatura e faturas são da relação plataforma–loja, como plano e domínio: `subscriptions`, `subscription_invoices`, `subscription_events` e `plans.monthly_price` (ver modelo de dados). A loja só lê as próprias faturas, sempre filtradas por `tenant_id` (o banco `plataforma` não tem RLS — ADR-0001).

### 3. Quando cobra (decisões do dono do produto em 2026-10-16)

- **Preço por plano**, editado pelo superadmin; vale para as faturas geradas depois. Zero = sem mensalidade.
- **Começa na publicação** (RF05): a 1ª mensalidade vence no dia da publicação; as seguintes, no mesmo dia dos meses seguintes (até o dia 28). Lojas já publicadas antes desta entrega começam no dia da primeira rodada do job.
- **Taxa de implantação** lançada pelo superadmin (valor e vencimento), uma por loja.
- **HIPÓTESES:** fatura gerada 10 dias antes do vencimento (`Billing:InvoiceLeadDays`); sem cobrança retroativa (competência vencida sem fatura fica de fora); a cobrança no gateway é criada só quando o Dono clica em Pagar e é reaproveitada (dois cliques simultâneos: a segunda é cancelada no gateway); pago a menor não quita (log crítico, a equipe resolve); pago depois de cancelada conta como paga.

### 4. Webhook pela fila, sem confiar no aviso

`POST /api/webhooks/assinatura/{gateway}` confere a assinatura, grava em `subscription_events` com chave única (gateway + idempotência; reentrega não duplica), responde 200 e publica `ProcessSubscriptionEvent` (regra 7). O handler consulta a cobrança no gateway e só então dá baixa na fatura indicada pela referência (id da fatura). Gravar e publicar não estão na mesma transação (bancos diferentes do outbox do Wolverine): o job reenfileira avisos parados há mais de 10 minutos; processar duas vezes é inofensivo.

### 5. Job de hora em hora no Worker

`SubscriptionBillingJob`: inicia assinaturas que faltam, gera mensalidades e reenfileira avisos parados. Tudo idempotente (índice único por competência). A régua de inadimplência (RF38 CA2/CA3) entra no mesmo job, na etapa seguinte.

## Consequências

- Trocar o gateway é escrever um adaptador; nada muda nas telas, nas faturas nem na régua.
- Enquanto Q05 estiver aberta, produção funciona só com pagamento registrado pela equipe.
