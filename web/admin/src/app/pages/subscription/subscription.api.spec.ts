import { BillingStanding, standingText } from './subscription.api';

const base: BillingStanding = { status: 'active', stage: 'none', overdueSince: null, readOnly: false };

describe('Faixa da régua de inadimplência (RF38)', () => {
  it('em dia, sem faixa', () => {
    expect(standingText(base)).toBeNull();
  });

  it('D+3: aviso com a data do vencimento', () => {
    expect(standingText({ ...base, stage: 'warned', overdueSince: '2026-10-10' })).toBe('Fatura vencida em 10/10/2026. Pague para evitar o bloqueio do painel.');
  });

  it('D+10: painel somente leitura, loja vendendo', () => {
    const text = standingText({ status: 'readOnly', stage: 'readOnly', overdueSince: '2026-10-01', readOnly: true })!;
    expect(text).toContain('somente leitura');
    expect(text).toContain('continua vendendo');
  });

  it('suspensa pela régua ou pela equipe', () => {
    expect(standingText({ status: 'suspended', stage: 'suspended', overdueSince: '2026-09-20', readOnly: true })).toContain('fatura vencida em 20/09/2026');
    expect(standingText({ status: 'suspended', stage: 'none', overdueSince: null, readOnly: true })).toContain('pela equipe');
  });
});
