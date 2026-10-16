export type InvoiceKind = 'setup' | 'monthly';
export type InvoiceStatus = 'open' | 'paid' | 'canceled';

/** Fatura da assinatura (RF38), igual no painel da loja e no da plataforma. */
export interface SubscriptionInvoice {
  id: string;
  kind: InvoiceKind;
  description: string;
  amount: number;
  dueDate: string;
  status: InvoiceStatus;
  overdue: boolean;
  paidAt: string | null;
  paidVia: 'gateway' | 'manual' | null;
  note: string | null;
}

export function invoiceStatusLabel(invoice: SubscriptionInvoice): string {
  if (invoice.status === 'paid') return invoice.paidVia === 'manual' ? 'Paga (registrada pela equipe)' : 'Paga';
  if (invoice.status === 'canceled') return 'Cancelada';
  return invoice.overdue ? 'Vencida' : 'Em aberto';
}
