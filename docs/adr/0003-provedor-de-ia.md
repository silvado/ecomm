# ADR-0003 — Provedor de IA

- **Status:** Aceito
- **Data:** 2026-10-07
- **Requisitos:** RF30–RF36

## Contexto

A IA precisa de visão (fotos de peças), saída estruturada (atributos do ML), custo controlado por plano e auditoria completa. O BRIEF define Anthropic Claude, mas exige poder trocar de provedor.

## Alternativas consideradas

| Opção | Prós | Contras |
|---|---|---|
| **A. Claude via SDK oficial atrás de `IAiProvider`** | Visão e tool use/saída estruturada; definido no BRIEF | Dependência de um fornecedor (mitigada pela interface) |
| B. Biblioteca de abstração genérica (ex.: Microsoft.Extensions.AI) como única camada | Troca de provedor "grátis" | Abstração menor que o recurso do provedor; ainda precisamos de nossa porta de domínio |
| C. Modelo self-hosted | Sem custo por token | Inviável na VPS de 8 GB; qualidade inferior em visão |

## Decisão

**Opção A.** Porta de domínio orientada a casos de uso, não a "chat":

```csharp
public interface IAiProvider
{
    Task<PartDraftSuggestion> SuggestPartFromPhotosAsync(PartPhotoInput input, CancellationToken ct);
    Task<BuyerAnswerSuggestion> SuggestBuyerAnswerAsync(BuyerQuestionInput input, CancellationToken ct);
    Task<string> WriteDailySummaryAsync(DailySummaryData data, CancellationToken ct);
    Task<SyncDiagnosis> DiagnoseSyncFailuresAsync(SyncFailureInput input, CancellationToken ct);
}
```

- Implementação `AnthropicAiProvider` com o SDK oficial; `FakeAiProvider` para dev/testes.
- Um **decorador** `AuditedAiProvider` registra toda chamada (RF35) e outro, `QuotaAiProvider`, aplica limites do plano (RF36) — o provedor concreto não conhece nenhum dos dois.
- Modelo por funcionalidade em configuração (ex.: modelo maior com visão para cadastro; modelo menor para classificar perguntas). Ids de modelo nunca fixos no código.
- Saídas estruturadas via tool use/JSON schema e validadas; resposta inválida = sugestão descartada, nunca aplicada.
- A IA recebe apenas os dados necessários (minimização LGPD): sem nome/CPF/endereço de compradores.
- **Sem capacidades de escrita:** as implementações não recebem repositórios nem serviços de comando; só devolvem sugestões (RF35).
- Chave da API da plataforma em variável de ambiente (não por tenant).

## Complemento — fundação implementada (16/10/2026)

Decisões do dono do produto: registro e contador no **banco das lojas com RLS**; só o **cadastro por foto bem-sucedido** conta para o limite do plano; adaptador pelo **SDK oficial** `Anthropic` (C#, mantido pela Anthropic desde a versão 10 — https://platform.claude.com/docs/en/cli-sdks-libraries/sdks/csharp).

- **Porta de baixo nível `IAiCompletion`** (Application): pedido = funcionalidade, instruções, texto, imagens, JSON Schema da resposta e resumo da entrada para o registro; resposta = objeto JSON conferido, modelo e tokens. Os casos de uso (RF30, RF31…) montam seus pedidos sobre ela; registro e limite valem para todos sem repetir código. Os métodos por caso de uso da seção "Decisão" passam a ser serviços da Application que usam esta porta.
- **Saída estruturada** por `output_config.format` (`json_schema`), não por ferramenta forçada: os modelos mais novos recusam `tool_choice` forçado (https://platform.claude.com/docs/en/build-with-claude/structured-outputs). Nenhuma ferramenta é oferecida ao modelo. Recusa (`refusal`), resposta cortada (`max_tokens`) ou JSON inválido = sugestão descartada.
- **Cadeia**: `QuotaAiCompletion` → `AuditedAiCompletion` → provedor (`AnthropicAiCompletion`, `FakeAiCompletion` ou `UnconfiguredAiCompletion`). O provedor é serviço com chave: quem pede `IAiCompletion` sempre passa pelo limite e pelo registro.
- **Registro** (`ai_interactions`, RLS): toda chamada, também as que falham, gravada em escopo próprio; custo estimado em dólares pela tabela `Ai:Prices` (modelo sem preço = zero). **HIPÓTESE:** falha ao gravar o registro não descarta a resposta já paga (log crítico).
- **Limite** (`ai_usage_counters`, RLS): reserva atômica (`INSERT … ON CONFLICT DO UPDATE … WHERE used < limite`) antes de chamar e devolução se a chamada falhar; recusado pelo limite não chama o provedor nem entra no registro.
- **Erros**: 429, 5xx e rede = indisponível com nova tentativa; demais 4xx = indisponível sem nova tentativa; a mensagem do provedor não chega ao usuário (https://platform.claude.com/docs/en/api/errors).
- **Configuração**: `Ai:Provider` (`simulado` | `anthropic` | vazio = desligada; simulado é recusado em produção), `Ai:DefaultModel`, `Ai:Models:<funcionalidade>`, `Ai:Anthropic:ApiKey` só por variável de ambiente.

## Consequências

- Troca de provedor = nova implementação de `IAiProvider` + ajuste de prompts.
- Necessário conjunto de avaliação (perguntas reais anonimizadas + respostas esperadas) para RF31, rodado antes de mudar prompt ou modelo.
- Custo por chamada registrado permite precificar os planos.

## Fontes

- Anthropic — documentação da API (visão, tool use): https://docs.anthropic.com
