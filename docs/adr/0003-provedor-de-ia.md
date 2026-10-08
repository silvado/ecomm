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

## Consequências

- Troca de provedor = nova implementação de `IAiProvider` + ajuste de prompts.
- Necessário conjunto de avaliação (perguntas reais anonimizadas + respostas esperadas) para RF31, rodado antes de mudar prompt ou modelo.
- Custo por chamada registrado permite precificar os planos.

## Fontes

- Anthropic — documentação da API (visão, tool use): https://docs.anthropic.com
