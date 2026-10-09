using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Store;

public enum ContrastWarning
{
    /// <summary>Texto sobre o fundo abaixo de 4,5:1 (WCAG AA, texto normal).</summary>
    TextOnBackground,
    /// <summary>Cor principal (links, bordas, ícones) sobre o fundo abaixo de 3:1 (WCAG AA, componentes).</summary>
    PrimaryOnBackground,
}

/// <summary>
/// Identidade visual e textos institucionais da loja (RF01). O tema vira CSS custom properties no SSR (ADR-0006):
/// mudar aqui reflete na loja sem deploy (CA3). Contraste ruim gera aviso, nunca bloqueio (CA4).
/// </summary>
public sealed class StoreBranding : ITenantOwned
{
    public const int LongTextMaxLength = 4000;
    public const int FooterMaxLength = 500;
    public const double MinTextContrast = 4.5;
    public const double MinComponentContrast = 3.0;

    private StoreBranding() { }

    /// <summary>Padrão de loja recém-criada: legível (texto escuro sobre branco, azul com contraste AA).</summary>
    public static StoreBranding Default(Guid tenantId, DateTimeOffset now) => new()
    {
        TenantId = tenantId,
        PrimaryColor = "#1F5FBF",
        BackgroundColor = "#FFFFFF",
        TextColor = "#1D2330",
        UpdatedAt = now,
    };

    public Guid TenantId { get; private set; }
    public string PrimaryColor { get; private set; } = string.Empty;
    public string BackgroundColor { get; private set; } = string.Empty;
    public string TextColor { get; private set; } = string.Empty;
    public string About { get; private set; } = string.Empty;
    public string ReturnPolicy { get; private set; } = string.Empty;
    public string Footer { get; private set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Cor do texto de botões sobre a cor principal: calculada, para nunca ficar ilegível.</summary>
    public string OnPrimaryColor => HexColor.Parse(PrimaryColor).ReadableTextColor().Value;

    public void Update(string primaryColor, string backgroundColor, string textColor,
        string? about, string? returnPolicy, string? footer, DateTimeOffset now)
    {
        var primary = HexColor.Parse(primaryColor);
        var background = HexColor.Parse(backgroundColor);
        var text = HexColor.Parse(textColor);
        about = Normalize(about, LongTextMaxLength, nameof(about));
        returnPolicy = Normalize(returnPolicy, LongTextMaxLength, nameof(returnPolicy));
        footer = Normalize(footer, FooterMaxLength, nameof(footer));

        PrimaryColor = primary.Value;
        BackgroundColor = background.Value;
        TextColor = text.Value;
        About = about;
        ReturnPolicy = returnPolicy;
        Footer = footer;
        UpdatedAt = now;
    }

    public IReadOnlyList<ContrastWarning> ContrastWarnings()
    {
        var background = HexColor.Parse(BackgroundColor);
        var warnings = new List<ContrastWarning>();
        if (HexColor.Parse(TextColor).ContrastWith(background) < MinTextContrast) warnings.Add(ContrastWarning.TextOnBackground);
        if (HexColor.Parse(PrimaryColor).ContrastWith(background) < MinComponentContrast) warnings.Add(ContrastWarning.PrimaryOnBackground);
        return warnings;
    }

    private static string Normalize(string? text, int maxLength, string field)
    {
        var value = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (value.Length > maxLength) throw new ArgumentException($"Texto acima de {maxLength} caracteres.", field);
        return value;
    }
}
