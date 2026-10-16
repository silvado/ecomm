namespace Ecommerce.Domain.Billing;

/// <summary>Estágio da régua de inadimplência (RF38 CA2), do mais leve ao mais grave.</summary>
public enum DunningStage
{
    None,
    /// <summary>D+3: aviso no painel.</summary>
    Warned,
    /// <summary>D+10: painel somente leitura; a loja continua vendendo (Q13, decisão do usuário).</summary>
    ReadOnly,
    /// <summary>D+20: vitrine fora do ar e anúncios dos canais pausados.</summary>
    Suspended,
    /// <summary>D+60: marcada para encerramento (nada é apagado automaticamente).</summary>
    Closing,
}

/// <summary>Prazos da régua contados do vencimento da fatura em aberto mais antiga. HIPÓTESE de prazos (Q13).</summary>
public static class DunningPolicy
{
    public const int WarnAfterDays = 3;
    public const int ReadOnlyAfterDays = 10;
    public const int SuspendAfterDays = 20;
    public const int CloseAfterDays = 60;

    /// <param name="oldestOverdueDueDate">Vencimento da fatura em aberto mais antiga já vencida; nulo = em dia.</param>
    public static DunningStage StageFor(DateOnly? oldestOverdueDueDate, DateOnly today)
    {
        if (oldestOverdueDueDate is not { } due || due >= today) return DunningStage.None;
        var days = today.DayNumber - due.DayNumber;
        return days switch
        {
            >= CloseAfterDays => DunningStage.Closing,
            >= SuspendAfterDays => DunningStage.Suspended,
            >= ReadOnlyAfterDays => DunningStage.ReadOnly,
            >= WarnAfterDays => DunningStage.Warned,
            _ => DunningStage.None,
        };
    }
}
