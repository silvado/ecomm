using System.Net.Mail;
using Ecommerce.Domain.Common;
using Ecommerce.Domain.Platform;
using Ecommerce.Domain.Shipping;

namespace Ecommerce.Domain.Orders;

public enum OrderOrigin
{
    /// <summary>Loja virtual do tenant (RF14).</summary>
    Site,
    /// <summary>Venda de balcão lançada no painel (RF18).</summary>
    Counter,
    /// <summary>Venda no Mercado Livre importada pela integração (RF21); o envio é do ML.</summary>
    MercadoLivre,
}

/// <summary>Como o cliente pagou no balcão (RF18).</summary>
public enum CounterPaymentMethod
{
    Cash,
    Pix,
    DebitCard,
    CreditCard,
    Other,
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
    /// <summary>Todas as peças voltaram (RF20); devolução parcial mantém a situação anterior.</summary>
    Returned,
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

/// <summary>Cliente opcional da venda de balcão: tudo pode ficar em branco; documento é CPF ou CNPJ.</summary>
public sealed record CounterCustomer(string? Name, string? Document, string? Phone);

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
    public const int ExternalShipmentIdMaxLength = 40;

    /// <summary>Nome mostrado na venda de balcão sem cliente informado.</summary>
    public const string WalkInCustomer = "Consumidor final";

    /// <summary>Local de "retirada" da venda de balcão: as peças saem na hora.</summary>
    public const string CounterPickup = "Venda no balcão";

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

    /// <summary>Envio do pedido no canal (Mercado Envios) — o canal avisa as mudanças por ele.</summary>
    public string? ExternalShipmentId { get; private set; }

    public void LinkShipment(string? shipmentId)
    {
        var id = shipmentId?.Trim();
        if (id is { Length: > ExternalShipmentIdMaxLength }) throw new ArgumentException("Id do envio no canal grande demais.", nameof(shipmentId));
        ExternalShipmentId = string.IsNullOrEmpty(id) ? null : id;
    }

    /// <summary>
    /// SHA-256 do token do link de acompanhamento do comprador (o token nunca é gravado). O navegador sorteia o token a
    /// cada checkout, então ele também é a chave de idempotência: o mesmo envio repetido não cria outro pedido.
    /// </summary>
    public byte[] AccessTokenHash { get; private set; } = [];

    public string BuyerName { get; private set; } = string.Empty;
    public string BuyerEmail { get; private set; } = string.Empty;
    public string BuyerPhone { get; private set; } = string.Empty;
    public string BuyerCpf { get; private set; } = string.Empty;

    /// <summary>Cliente pessoa jurídica (oficina) na venda de balcão; o site só aceita CPF.</summary>
    public string? BuyerCnpj { get; private set; }

    /// <summary>Só na venda de balcão (RF18).</summary>
    public CounterPaymentMethod? PaymentMethod { get; private set; }

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

    public DeliveryAddress? Address => DeliveryMethod == DeliveryMethod.Shipping && DeliveryPostalCode is not null
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
        ValidateLines(lines);
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
    /// Venda de balcão (RF18): já paga e retirada — as peças saem na hora (baixas já feitas pelo chamador, cada linha com a
    /// sua reserva convertida). Sem prazo nem link do comprador; o hash é a chave de idempotência do formulário do painel.
    /// </summary>
    public static CustomerOrder PlaceCounterSale(Guid tenantId, long number, byte[] idempotencyHash, CounterCustomer? customer,
        CounterPaymentMethod paymentMethod, IReadOnlyList<OrderLine> lines, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        if (idempotencyHash.Length != 32) throw new ArgumentException("Hash de idempotência inválido.", nameof(idempotencyHash));
        if (!Enum.IsDefined(paymentMethod)) throw new ArgumentException("Escolha a forma de pagamento.", nameof(paymentMethod));
        ValidateLines(lines);

        var (name, cpf, cnpj, phone) = NormalizeCounterCustomer(customer);
        var order = new CustomerOrder
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Number = number,
            Origin = OrderOrigin.Counter,
            Status = OrderStatus.PickedUp,
            AccessTokenHash = idempotencyHash,
            PlacedAt = now,
            PaymentDeadline = now,
            PaidAt = now,
            DeliveredAt = now,
            PaymentMethod = paymentMethod,
            BuyerName = name,
            BuyerEmail = string.Empty,
            BuyerPhone = phone,
            BuyerCpf = cpf,
            BuyerCnpj = cnpj,
            DeliveryMethod = DeliveryMethod.Pickup,
            PickupAddress = CounterPickup,
        };
        foreach (var line in lines) order._items.Add(new OrderItem(order, line));
        order.ItemsTotal = order._items.Sum(i => i.UnitPrice * i.Quantity);
        order.Total = order.ItemsTotal;
        return order;
    }

    /// <summary>
    /// Venda num canal (Mercado Livre — RF21): chega já paga, com as peças baixadas pelo chamador (linha sem saldo tem a
    /// reserva "liberada": nada a devolver). <paramref name="reviewReason"/> preenchido (conflito de estoque, anúncio que não
    /// é da plataforma) deixa o pedido em análise para a loja decidir (Q01). Endereço e frete ficam no canal.
    /// </summary>
    public static CustomerOrder PlaceChannelOrder(Guid tenantId, long number, OrderOrigin origin, string externalId, string buyerName,
        IReadOnlyList<OrderLine> lines, decimal shippingTotal, string? reviewReason, DateTimeOffset paidAt, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        if (origin is OrderOrigin.Site or OrderOrigin.Counter) throw new ArgumentException("Origem de canal inválida.", nameof(origin));
        var id = externalId?.Trim() ?? string.Empty;
        if (id.Length is 0 or > ExternalIdMaxLength) throw new ArgumentException("Id do pedido no canal inválido.", nameof(externalId));
        ValidateLines(lines);
        if (shippingTotal < 0) throw new ArgumentException("Frete inválido.", nameof(shippingTotal));
        var name = Collapse(buyerName);
        var order = new CustomerOrder
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Number = number,
            Origin = origin,
            ExternalId = id,
            Status = OrderStatus.Paid,
            // Chave única do pedido (o índice do hash vale para todas as origens): canal + id do pedido lá.
            AccessTokenHash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{origin}:{id}")),
            PlacedAt = now,
            PaymentDeadline = now,
            PaidAt = paidAt,
            BuyerName = name.Length == 0 ? "Comprador do canal" : (name.Length > NameMaxLength ? name[..NameMaxLength] : name),
            BuyerEmail = string.Empty,
            BuyerPhone = string.Empty,
            BuyerCpf = string.Empty,
            DeliveryMethod = DeliveryMethod.Shipping,
            ShippingCarrier = origin == OrderOrigin.MercadoLivre ? "Mercado Envios" : null,
            ShippingTotal = shippingTotal,
        };
        foreach (var line in lines) order._items.Add(new OrderItem(order, line));
        order.ItemsTotal = order._items.Sum(i => i.UnitPrice * i.Quantity);
        order.Total = order.ItemsTotal + order.ShippingTotal;
        if (!string.IsNullOrWhiteSpace(reviewReason)) order.FlagForReview(reviewReason);
        return order;
    }

    /// <summary>Cliente do balcão: nome (ou <see cref="WalkInCustomer"/>), CPF ou CNPJ válido e telefone com DDD, se informados.</summary>
    public static (string Name, string Cpf, string? Cnpj, string Phone) NormalizeCounterCustomer(CounterCustomer? customer)
    {
        var name = Collapse(customer?.Name);
        if (name.Length > NameMaxLength) throw new ArgumentException($"Nome com no máximo {NameMaxLength} caracteres.", nameof(customer));
        if (name.Length is > 0 and < 3) throw new ArgumentException("Informe o nome do cliente (ou deixe em branco).", nameof(customer));

        // CNPJ pode ser alfanumérico (IN RFB 2.229/2024): cada tipo valida o próprio formato.
        var document = customer?.Document;
        var cpf = string.Empty;
        string? cnpj = null;
        if (Orders.Cpf.TryParse(document, out var parsedCpf)) cpf = parsedCpf!.Value;
        else if (Cnpj.TryParse(document, out var parsedCnpj)) cnpj = parsedCnpj!.Value;
        else if (!string.IsNullOrWhiteSpace(document)) throw new ArgumentException("CPF ou CNPJ inválido.", nameof(customer));

        var phone = Digits(customer?.Phone);
        if (phone.Length > 0 && (phone.Length is < 10 or > 11 || phone[0] == '0'))
            throw new ArgumentException("Informe o telefone com DDD (ou deixe em branco).", nameof(customer));

        return (name.Length == 0 ? WalkInCustomer : name, cpf, cnpj, phone);
    }

    private static void ValidateLines(IReadOnlyList<OrderLine> lines)
    {
        if (lines.Count is 0 or > MaxLines) throw new ArgumentException($"O pedido precisa ter de 1 a {MaxLines} peças.", nameof(lines));
        if (lines.Select(l => l.PartId).Distinct().Count() != lines.Count) throw new ArgumentException("Peça repetida no pedido.", nameof(lines));
        if (lines.Any(l => l.Quantity <= 0 || l.UnitPrice <= 0)) throw new ArgumentException("Quantidade e preço precisam ser positivos.", nameof(lines));
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

    /// <summary>
    /// Situações em que a loja pode cancelar (RF17): antes da entrega ou retirada. A venda de balcão já nasce retirada e
    /// pode ser cancelada para corrigir um lançamento errado (RF18); devolução de verdade é o RF20.
    /// </summary>
    public bool CanBeCanceledByStore =>
        !HasReturns
        && (Status is OrderStatus.PendingPayment or OrderStatus.NeedsReview or OrderStatus.Paid or OrderStatus.Shipped
            || (Origin == OrderOrigin.Counter && Status == OrderStatus.PickedUp));

    /// <summary>Já teve devolução (RF20): não pode mais ser cancelado (as peças voltariam duas vezes).</summary>
    public bool HasReturns { get; private set; }

    public DateTimeOffset? ReturnedAt { get; private set; }

    /// <summary>Entregue ou retirado (e não devolvido por inteiro): aceita devolução.</summary>
    public bool AcceptsReturn => Status is OrderStatus.Delivered or OrderStatus.PickedUp;

    /// <summary>Nome que fica no lugar do comprador anonimizado sem nota fiscal (RNF04).</summary>
    public const string AnonymizedName = "Titular anonimizado";

    /// <summary>Quando os dados do comprador foram anonimizados a pedido dele (RNF04). Nulo = dados originais.</summary>
    public DateTimeOffset? AnonymizedAt { get; private set; }

    /// <summary>Em andamento: anonimizar agora impediria entregar, cobrar ou resolver o pedido.</summary>
    public bool IsInProgress => Status is OrderStatus.PendingPayment or OrderStatus.Paid or OrderStatus.NeedsReview or OrderStatus.Shipped;

    /// <summary>
    /// Anonimização a pedido do titular (RNF04, LGPD art. 18). Apaga contato e endereço de entrega (fica só cidade e UF).
    /// Com nota fiscal (<paramref name="hasInvoice"/>), nome e CPF/CNPJ ficam: a lei fiscal obriga a guardar por 5 anos e
    /// são eles que ligam o pedido à nota. Pedido, peças e valores sempre ficam.
    /// </summary>
    public void AnonymizeBuyer(bool hasInvoice, DateTimeOffset now)
    {
        if (IsInProgress) throw new InvalidOperationException($"Pedido {Number} em andamento: conclua ou cancele antes de anonimizar.");
        BuyerEmail = string.Empty;
        BuyerPhone = string.Empty;
        DeliveryPostalCode = null;
        DeliveryStreet = null;
        DeliveryNumber = null;
        DeliveryComplement = null;
        DeliveryDistrict = null;
        if (!hasInvoice)
        {
            BuyerName = AnonymizedName;
            BuyerCpf = string.Empty;
            BuyerCnpj = null;
        }
        AnonymizedAt ??= now;
    }

    /// <summary>
    /// Devolução de peças (RF20, Q35): parcial ou total, nunca acima do que foi vendido menos o já devolvido. Quando tudo
    /// volta, o pedido fica "Devolvido". Estoque: o chamador devolve as linhas com <see cref="OrderReturnLine.Restock"/>.
    /// </summary>
    public OrderReturn RegisterReturn(IReadOnlyList<ReturnRequestLine> request, IReadOnlyList<OrderReturn> previous, ReturnReason reason,
        string? note, decimal? refundAmount, RefundMethod? refundMethod, Guid userId, DateTimeOffset now)
    {
        Require(AcceptsReturn, "não está entregue ou retirado");
        if (request.Count == 0 || request.All(l => l.Quantity == 0)) throw new ArgumentException("Escolha as peças devolvidas.", nameof(request));
        if (request.Select(l => l.PartId).Distinct().Count() != request.Count) throw new ArgumentException("Peça repetida na devolução.", nameof(request));
        if (!Enum.IsDefined(reason)) throw new ArgumentException("Escolha o motivo da devolução.", nameof(reason));
        if (refundMethod is { } method && !Enum.IsDefined(method)) throw new ArgumentException("Forma de reembolso inválida.", nameof(refundMethod));
        if (refundAmount is { } amount && (amount < 0 || amount > Total || decimal.Round(amount, 2) != amount))
            throw new ArgumentException($"Reembolso entre R$ 0,00 e o total do pedido, com até 2 casas decimais.", nameof(refundAmount));
        var text = Collapse(note);
        if (text.Length > OrderReturn.NoteMaxLength) throw new ArgumentException($"Observação com no máximo {OrderReturn.NoteMaxLength} caracteres.", nameof(note));

        var lines = new List<OrderReturnLine>();
        foreach (var line in request.Where(l => l.Quantity != 0))
        {
            var item = _items.SingleOrDefault(i => i.PartId == line.PartId) ?? throw new ArgumentException("Peça não está no pedido.", nameof(request));
            var available = item.Quantity - ReturnedQuantity(previous, item.PartId);
            if (line.Quantity < 0 || line.Quantity > available)
                throw new ArgumentException($"{item.InternalCode}: só {available} unidade(s) podem ser devolvidas.", nameof(request));
            lines.Add(new OrderReturnLine(item.PartId, item.InternalCode, item.Title, line.Quantity, line.Restock));
        }

        var registered = new OrderReturn(this, lines, reason, text.Length == 0 ? null : text, refundAmount, refundAmount is null ? null : refundMethod, userId, now);
        HasReturns = true;
        var all = previous.Append(registered).ToList();
        if (_items.All(i => ReturnedQuantity(all, i.PartId) == i.Quantity))
        {
            Status = OrderStatus.Returned;
            ReturnedAt = now;
        }
        return registered;
    }

    public static int ReturnedQuantity(IEnumerable<OrderReturn> returns, Guid partId) =>
        returns.SelectMany(r => r.Lines).Where(l => l.PartId == partId).Sum(l => l.Quantity);

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
