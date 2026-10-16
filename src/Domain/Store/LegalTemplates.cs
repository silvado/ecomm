namespace Ecommerce.Domain.Store;

/// <summary>
/// Modelos de política de privacidade e termos de uso (RNF04), mostrados enquanto a loja não escreve os seus.
/// HIPÓTESE (requisitos → Em aberto, Q42): texto genérico da plataforma, a ser revisado por advogado antes do lançamento;
/// a loja é a controladora dos dados dos compradores e a plataforma, operadora.
/// </summary>
public static class LegalTemplates
{
    public static string PrivacyPolicy(string tradeName, string legalName, string cnpj) => $"""
        Política de privacidade — {tradeName}

        Quem cuida dos seus dados
        {legalName} (CNPJ {cnpj}), responsável por esta loja, é a controladora dos dados pessoais coletados aqui, conforme a Lei Geral de Proteção de Dados (Lei 13.709/2018). A plataforma que hospeda a loja trata os dados só em nome dela, como operadora.

        Que dados coletamos e para quê
        - Nome, e-mail, telefone e CPF ou CNPJ: para registrar o pedido, falar com você sobre ele e emitir a nota fiscal.
        - Endereço de entrega: para calcular o frete e entregar as peças.
        - Dados de pagamento: digitados direto no ambiente do meio de pagamento; a loja não recebe nem guarda o número do cartão.
        Não pedimos dados além dos necessários para a compra e não usamos seus dados para publicidade.

        Com quem compartilhamos
        Só com quem participa da compra: meio de pagamento, transportadora ou serviço de frete, e o emissor de notas fiscais. Também quando a lei ou uma autoridade exigir.

        Por quanto tempo guardamos
        Os dados do pedido e da nota fiscal ficam guardados pelo prazo da lei fiscal (5 anos). Os demais dados de contato podem ser apagados antes, a seu pedido, se não houver pedido em andamento.

        Cookies
        Usamos apenas o armazenamento necessário para o carrinho e para manter o acesso à área "Meus pedidos". Não usamos cookies de propaganda.

        Seus direitos
        Você pode pedir para confirmar se tratamos seus dados, acessar, corrigir ou anonimizar seus dados de contato e saber com quem foram compartilhados. Faça o pedido pelos canais de atendimento desta loja; respondemos em até 15 dias.

        Mudanças
        Esta política pode ser atualizada. A versão em vigor é sempre a publicada nesta página.
        """;

    public static string Terms(string tradeName, string legalName, string cnpj) => $"""
        Termos de uso e de venda — {tradeName}

        Quem vende
        {legalName} (CNPJ {cnpj}).

        Peças
        As peças anunciadas, novas ou usadas, têm a condição informada no anúncio. Peças usadas podem ter marcas de uso; confira a compatibilidade com o seu veículo antes de comprar e, em caso de dúvida, fale com a loja.

        Preço e estoque
        O preço vale no momento em que o pedido é confirmado. A peça fica reservada para você enquanto o pagamento está dentro do prazo mostrado no pedido; se o pagamento não for feito no prazo, o pedido é cancelado e a peça volta a ficar disponível.

        Pagamento e nota fiscal
        O pagamento é processado pelo meio de pagamento escolhido. A nota fiscal é emitida em nome de quem comprou, com os dados informados no pedido.

        Entrega e retirada
        O prazo de entrega começa a contar depois da confirmação do pagamento. Quando a loja oferece retirada, o endereço aparece no pedido.

        Trocas, devoluções e arrependimento
        Compras feitas pela internet podem ser desistidas em até 7 dias do recebimento (Código de Defesa do Consumidor, art. 49). As demais regras de troca estão na página "Trocas e devoluções".

        Privacidade
        O uso dos seus dados segue a Política de privacidade desta loja.
        """;
}
