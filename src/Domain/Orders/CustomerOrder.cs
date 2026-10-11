using System.Net.Mail;
using Ecommerce.Domain.Common;
using Ecommerce.Domain.Shipping;

namespace Ecommerce.Domain.Orders;

public enum OrderOrigin
{
    /// <summary>Loja virtual do tenant (RF14).</summary>
    Site,
}

public enum OrderStatus
{
    /// <summary>Peças reservadas até o prazo de pagamento (RF12).</summary>
    PendingPayment,
    Paid,
    Canceled,
    /// <summary>Pagamento que a loja precisa resolver à mão (pago depois de a peça ser vendida, valor diferente, estorno).</summary>
    NeedsReview,
    /// <summary>Entregue à transportadora (RF17).</summary>
    Shipped,
    Delivered,
    /// <summary>Retirado pelo comprador na loja.</summary>
    PickedUp,
}

/// <summary>Quem cancelou: o prazo (sem pagamento) ou a loja. Pagamento que chega depois só reativa o cancelado pelo prazo.</summary>
public enum CancellationSource
{
    PaymentExpired,
    Store,
}

public enum DeliveryMethod
{
    Shipping,
    Pickup,
}

/// <summary>Dados do comprador convidado, gravados só no pedido (sem cadastro de cliente até o RF16).</summary>
public sealed record BuyerDetails(string Name, string Email, string Phone, string Cpf);

public sealed record DeliveryAddress(string PostalCode, string Street, string Number, string? Complement, string District, string City, string State);

/// <summary>Opção de frete escolhida, com o preço cotado pelo servidor.</summary>
public sealed record ShippingChoice(string ServiceId, string Carrier, string Service, decimal Price, int DeliveryDays);

/// <summary>Item com preço e título do momento da compra e a reserva que segura a peça.</summary>
public sealed record OrderLine(Guid PartId, string InternalCode, string Title, decimal UnitPrice, int Quantity, Guid ReservationId);

/// <summary>
/// Pedido (RF14/RF17). Nasce aguardando pagamento, com as peças reservadas; preços, títulos, comprador e endereço são
/// cópias do momento da compra — mudar a peça depois não muda o pedido.
/// </summary>
public sealed class CustomerOrder : ITenantOwned
{
    public const int MaxLines = 50;
    public const int NameMaxLength = 120;
    public const int EmailMaxLength = 254;
    public const int CancelReasonMaxLength = 200;
    public const int TrackingCodeMaxLength = 60;
    public const int ExternalIdMaxLength = 100;

    private static readonly HashSet<string> States =
    [
        "AC", "AL", "AP", "AM", "BA", "CE", "DF", "ES", "GO", "MA", "MT", "MS", "MG", "PA", "PB", "PR", "PE", "PI", "RJ", "RN",
        "RS", "RO", "RR", "SC", "SP", "SE", "TO",
    ];

    private readonly List<OrderItem> _items = [];

    private CustomerOrder() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>Sequencial por loja, mostrado ao comprador e ao lojista.</summary>
    public long Number { get; private set; }

    public OrderOrigin Origin { get; private set; }

    /// <summary>Id do pedido no canal de origem (Mercado Livre, OLX); único por loja e origem (RF17 CA2). Nulo no site.</summary>
    public string? ExternalId { get; private set; }

    public OrderStatus Status { get; private set; }

    /// <summary>
    /// SHA-256 do token do link de acompanhamento do comprador (o token nunca é gravado). O navegador sorteia o token a
    /// cada checkout, então ele também é a chave de idempotência: o mesmo envio repetido não cria outro pedido.
    /// </summary>
    public byte[] AccessTokenHash { get; private set; } = [];

    public string BuyerName { get; private set; } = string.Empty;
    public string BuyerEmail { get; private set; } = string.Empty;
    public string BuyerPhone { get; private set; } = string.Empty;
    public string BuyerCpf { get; private set; } = string.Empty;

    public DeliveryMethod DeliveryMethod { get; private set; }
    public string? DeliveryPostalCode { get; private set; }
    public string? DeliveryStreet { get; private set; }
    public string? DeliveryNumber { get; private set; }
    public string? DeliveryComplement { get; private set; }
    public string? DeliveryDistrict { get; private set; }
    public string? DeliveryCity { get; private set; }
    public string? DeliveryState { get; private set; }
    public string? ShippingServiceId { get; private set; }
    public string? ShippingCarrier { get; private set; }
    public string? ShippingService { get; private set; }
    public int? ShippingDays { get; private set; }

    /// <summary>Endereço de retirada informado ao comprador no momento da compra.</summary>
    public string? PickupAddress { get; private set; }

    public decimal ItemsTotal { get; private set; }
    public decimal ShippingTotal { get; private set; }
    public decimal Total { get; private set; }
    public DateTimeOffset PlacedAt { get; private set; }

    /// <summary>Fim da reserva: sem pagamento até aqui, o pedido é cancelado e as peças voltam ao estoque.</summary>
    public DateTimeOffset PaymentDeadline { get; private set; }

    public DateTimeOffset? CanceledAt { get; private set; }
    public string? CancelReason { get; private set; }
    public CancellationSource? CanceledBy { get; private set; }
    public DateTimeOffset? PaidAt { get; private set; }
    public string? TrackingCode { get; private set; }
    public DateTimeOffset? ShippedAt { get; private set; }

    /// <summary>Entregue ao comprador (pela transportadora ou na retirada).</summary>
    public DateTimeOffset? DeliveredAt { get; private set; }

    /// <summary>Por que o pedido precisa da loja (<see cref="OrderStatus.NeedsReview"/>).</summary>
    public string? ReviewReason { get; private set; }

    public IReadOnlyList<OrderItem> Items => _items;

    public DeliveryAddress? Address => DeliveryMethod == DeliveryMethod.Shipping
        ? new DeliveryAddress(DeliveryPostalCode!, DeliveryStreet!, DeliveryNumber!, DeliveryComplement, DeliveryDistrict!, DeliveryCity!, DeliveryState!)
        : null;

    /// <summary>Normaliza e valida os dados do comprador (lança <see cref="ArgumentException"/> com a mensagem ao usuário).</summary>
    public static BuyerDetails NormalizeBuyer(BuyerDetails buyer)
    {
        var name = Collapse(buyer.Name);
        if (name.Length is < 3 or > NameMaxLength) throw new ArgumentException("Informe o nome completo.", nameof(buyer));

        var email = (buyer.Email ?? string.Empty).Trim().ToLowerInvariant();
        if (email.Length > EmailMaxLength || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email || !parsed.Host.Contains('.', StringComparison.Ordinal))
            throw new ArgumentException("Informe um e-mail válido.", nameof(buyer));

        var phone = Digits(buyer.Phone);
        if (phone.Length is < 10 or > 11 || phone[0] == '0') throw new ArgumentException("Informe o telefone com DDD.", nameof(buyer));

        if (!Orders.Cpf.TryParse(buyer.Cpf, out var cpf)) throw new ArgumentException("CPF inválido.", nameof(buyer));
        return new BuyerDetails(name, email, phone, cpf!.Value);
    }

    /// <summary>Normaliza e valida o endereço de entrega.</summary>
    public static DeliveryAddress NormalizeAddress(DeliveryAddress address)
    {
        if (!PostalCode.TryParse(address.PostalCode, out var postalCode)) throw new ArgumentException("CEP inválido: informe os 8 dígitos.", nameof(address));
        var street = Required(address.Street, 120, "Informe a rua.");
        var number = Required(address.Number, 20, "Informe o número (ou \"s/n\").");
        var complement = Collapse(address.Complement);
        if (complement.Length > 60) throw new ArgumentException("Complemento com no máximo 60 caracteres.", nameof(address));
        var district = Required(address.District, 60, "Informe o bairro.");
        var city = Required(address.City, 60, "Informe a cidade.");
        var state = Collapse(address.State).ToUpperInvariant();
        if (!States.Contains(state)) throw new ArgumentException("Informe a UF.", nameof(address));
        return new DeliveryAddress(postalCode.Value, street, number, complement.Length == 0 ? null : complement, district, city, state);
    }

    /// <summary>
    /// Cria o pedido aguardando pagamento. Entrega exige endereço e frete cotado; retirada exige o endereço da loja.
    /// As reservas já precisam existir (cada linha aponta para a sua).
    /// </summary>
    public static CustomerOrder Place(Guid tenantId, long number, byte[] accessTokenHash, BuyerDetails buyer,
        DeliveryAddress? address, ShippingChoice? shipping, string? pickupAddress, IReadOnlyList<OrderLine> lines,
        DateTimeOffset now, DateTimeOffset paymentDeadline)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        if (accessTokenHash.Length != 32) throw new ArgumentException("Hash do token de acesso inválido.", nameof(accessTokenHash));
        if (lines.Count is 0 or > MaxLines) throw new ArgumentException($"O pedido precisa ter de 1 a {MaxLines} peças.", nameof(lines));
        if (lines.Select(l => l.PartId).Distinct().Count() != lines.Count) throw new ArgumentException("Peça repetida no pedido.", nameof(lines));
        if (lines.Any(l => l.Quantity <= 0 || l.UnitPrice <= 0)) throw new ArgumentException("Quantidade e preço precisam ser positivos.", nameof(lines));
        if (paymentDeadline <= now) throw new ArgumentException("O prazo de pagamento precisa estar no futuro.", nameof(paymentDeadline));

        var order = new CustomerOrder
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Number = number,
            Origin = OrderOrigin.Site,
            Status = OrderStatus.PendingPayment,
            AccessTokenHash = accessTokenHash,
            PlacedAt = now,
            PaymentDeadline = paymentDeadline,
        };

        var normalizedBuyer = NormalizeBuyer(buyer);
        (order.BuyerName, order.BuyerEmail, order.BuyerPhone, order.BuyerCpf) =
            (normalizedBuyer.Name, normalizedBuyer.Email, normalizedBuyer.Phone, normalizedBuyer.Cpf);

        if (shipping is not null)
        {
            if (address is null) throw new ArgumentException("Informe o endereço de entrega.", nameof(address));
            if (shipping.Price < 0 || string.IsNullOrWhiteSpace(shipping.ServiceId)) throw new ArgumentException("Frete inválido.", nameof(shipping));
            var a = NormalizeAddress(address);
            order.DeliveryMethod = DeliveryMethod.Shipping;
            (order.DeliveryPostalCode, order.DeliveryStreet, order.DeliveryNumber, order.DeliveryComplement, order.DeliveryDistrict, order.DeliveryCity, order.DeliveryState) =
                (a.PostalCode, a.Street, a.Number, a.Complement, a.District, a.City, a.State);
            (order.ShippingServiceId, order.ShippingCarrier, order.ShippingService, order.ShippingDays) =
                (shipping.ServiceId, shipping.Carrier, shipping.Service, shipping.DeliveryDays);
            order.ShippingTotal = shipping.Price;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(pickupAddress)) throw new ArgumentException("Esta loja não oferece retirada.", nameof(pickupAddress));
            order.DeliveryMethod = DeliveryMethod.Pickup;
            order.PickupAddress = pickupAddress;
        }

        foreach (var line in lines) order._items.Add(new OrderItem(order, line));
        order.ItemsTotal = order._items.Sum(i => i.UnitPrice * i.Quantity);
        order.Total = order.ItemsTotal + order.ShippingTotal;
        return order;
    }

    /// <summary>
    /// Pagamento aprovado e peças baixadas. Vale também para pedido cancelado por falta de pagamento: o pagamento chegou
    /// depois do prazo, mas as peças ainda estavam disponíveis (RF12 CA3).
    /// </summary>
    public void MarkPaid(DateTimeOffset now)
    {
        if (!AcceptsPayment) throw new InvalidOperationException($"Pedido {Number} não aguarda pagamento.");
        Status = OrderStatus.Paid;
        PaidAt = now;
    }

    /// <summary>Aguardando pagamento, ou cancelado só porque o prazo acabou (pagamento atrasado ainda vale — RF12 CA3).</summary>
    public bool AcceptsPayment =>
        Status == OrderStatus.PendingPayment || (Status == OrderStatus.Canceled && CanceledBy is null or CancellationSource.PaymentExpired);

    public void FlagForReview(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Status = OrderStatus.NeedsReview;
        ReviewReason = reason.Length > 300 ? reason[..300] : reason;
    }

    /// <summary>A loja confirma a venda em análise (peças já baixadas pelo chamador). Volta a "pago".</summary>
    public void ApproveReview(DateTimeOffset now)
    {
        Require(Status == OrderStatus.NeedsReview, "não está em análise");
        Status = OrderStatus.Paid;
        PaidAt ??= now;
        ReviewReason = null;
    }

    /// <summary>Situações em que a loja pode cancelar (RF17): antes da entrega ou retirada.</summary>
    public bool CanBeCanceledByStore =>
        Status is OrderStatus.PendingPayment or OrderStatus.NeedsReview or OrderStatus.Paid or OrderStatus.Shipped;

    /// <summary>Cancelamento pela loja, com motivo. Peças: o chamador libera as reservas ou devolve ao estoque o que foi baixado.</summary>
    public void CancelByStore(string reason, DateTimeOffset now)
    {
        Require(CanBeCanceledByStore, "não pode mais ser cancelado");
        var text = Collapse(reason);
        if (text.Length is 0 or > CancelReasonMaxLength)
            throw new ArgumentException($"Informe o motivo do cancelamento (até {CancelReasonMaxLength} caracteres).", nameof(reason));
        Status = OrderStatus.Canceled;
        CanceledBy = CancellationSource.Store;
        CancelReason = text;
        CanceledAt = now;
    }

    public void MarkShipped(string? trackingCode, DateTimeOffset now)
    {
        Require(Status == OrderStatus.Paid && DeliveryMethod == DeliveryMethod.Shipping, "não está pago para entrega");
        var code = Collapse(trackingCode);
        if (code.Length > TrackingCodeMaxLength)
            throw new ArgumentException($"Código de rastreio com no máximo {TrackingCodeMaxLength} caracteres.", nameof(trackingCode));
        Status = OrderStatus.Shipped;
        TrackingCode = code.Length == 0 ? null : code;
        ShippedAt = now;
    }

    public void MarkDelivered(DateTimeOffset now)
    {
        Require(Status == OrderStatus.Shipped, "não foi enviado");
        Status = OrderStatus.Delivered;
        DeliveredAt = now;
    }

    public void MarkPickedUp(DateTimeOffset now)
    {
        Require(Status == OrderStatus.Paid && DeliveryMethod == DeliveryMethod.Pickup, "não está pago para retirada");
        Status = OrderStatus.PickedUp;
        DeliveredAt = now;
    }

    private void Require(bool condition, string state)
    {
        if (!condition) throw new InvalidOperationException($"O pedido {Number} {state}.");
    }

    private static string Required(string? value, int max, string message)
    {
        var text = Collapse(value);
        return text.Length is 0 || text.Length > max ? throw new ArgumentException(message, nameof(value)) : text;
    }

    private static string Collapse(string? value) =>
        string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Digits(string? value) => new((value ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
}

public sealed class OrderItem : ITenantOwned
{
    private OrderItem() { }

    internal OrderItem(CustomerOrder order, OrderLine line)
    {
        Id = Guid.CreateVersion7();
        TenantId = order.TenantId;
        OrderId = order.Id;
        PartId = line.PartId;
        InternalCode = line.InternalCode;
        Title = line.Title;
        UnitPrice = line.UnitPrice;
        Quantity = line.Quantity;
        ReservationId = line.ReservationId;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid PartId { get; private set; }
    public string InternalCode { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public decimal UnitPrice { get; private set; }
    public int Quantity { get; private set; }
    public Guid ReservationId { get; private set; }
}
