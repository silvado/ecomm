import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { problemTitle } from '../../core/auth';
import { FiscalApi, FiscalSettings, UF } from './fiscal.api';

/**
 * Nota fiscal (RF19), em Dados da loja (só o Dono): como a loja emite a NF-e e os padrões usados nas peças sem dados
 * fiscais próprios. Os códigos são definidos com o contador da loja.
 */
@Component({
  selector: 'app-fiscal-settings',
  imports: [FormsModule],
  template: `
    <section data-fiscal-settings>
      <h2>Nota fiscal (NF-e)</h2>
      @if (cnpj()) {
        <p class="muted">Emitente: CNPJ {{ cnpj() }}. Confira os códigos com o contador da loja.</p>
      }
      <label>
        Como a loja emite
        <select [(ngModel)]="s.mode" name="mode" data-mode>
          <option value="disabled">Não emite pela plataforma</option>
          <option value="provider">Pela plataforma (provedor fiscal da loja)</option>
          <option value="external">Fora da plataforma (informar chave ou XML no pedido)</option>
        </select>
      </label>
      @if (s.mode === 'provider' && !connected()) {
        <p class="warn" role="status">O provedor fiscal (Focus NFe) ainda não está conectado. Até lá, use "Fora da plataforma".</p>
      }
      @if (s.mode === 'provider') {
        <label>
          Ambiente
          <select [(ngModel)]="s.environment" name="environment">
            <option value="homologation">Homologação (testes, sem valor fiscal)</option>
            <option value="production">Produção</option>
          </select>
        </label>
      }
      @if (s.mode !== 'disabled') {
        <div class="grid-3">
          <label>
            Regime tributário
            <select [(ngModel)]="s.regime" name="regime" data-regime>
              <option value="simplesNacional">Simples Nacional (CSOSN)</option>
              <option value="normal">Regime Normal (CST)</option>
            </select>
          </label>
          <label>
            UF da loja
            <select [(ngModel)]="s.state" name="state" data-state>
              <option [ngValue]="null">—</option>
              @for (uf of ufs; track uf) {
                <option [ngValue]="uf">{{ uf }}</option>
              }
            </select>
          </label>
          <label>NCM padrão <input [(ngModel)]="s.defaultNcm" name="defaultNcm" maxlength="10" placeholder="8708.29.99" data-ncm /></label>
          <label>CFOP dentro do estado <input [(ngModel)]="s.cfopInState" name="cfopInState" maxlength="4" placeholder="5102" /></label>
          <label>CFOP fora do estado <input [(ngModel)]="s.cfopOutOfState" name="cfopOutOfState" maxlength="4" placeholder="6102" /></label>
          <label>
            {{ s.regime === 'simplesNacional' ? 'CSOSN padrão' : 'CST do ICMS padrão' }}
            <input [(ngModel)]="s.defaultTaxCode" name="defaultTaxCode" maxlength="3" [placeholder]="s.regime === 'simplesNacional' ? '102' : '00'" data-tax-code />
          </label>
          <label>
            Origem padrão
            <select [(ngModel)]="s.defaultOrigin" name="defaultOrigin">
              @for (o of origins; track o.value) {
                <option [ngValue]="o.value">{{ o.label }}</option>
              }
            </select>
          </label>
        </div>
        <p class="muted">Peças sem NCM, CSOSN/CST ou origem próprios usam estes padrões.</p>
      }
      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
      @if (saved()) {
        <p class="ok" role="status">Nota fiscal salva.</p>
      }
      <div class="order-actions">
        <button type="button" (click)="save()" [disabled]="busy()" data-save-fiscal>Salvar nota fiscal</button>
      </div>
    </section>
  `,
})
export class FiscalSettingsSection implements OnInit {
  private readonly api = inject(FiscalApi);

  protected readonly ufs = UF;
  protected readonly origins = ORIGINS;
  protected s: FiscalSettings = {
    mode: 'disabled', environment: 'homologation', regime: 'simplesNacional', state: null, defaultNcm: null,
    cfopInState: '5102', cfopOutOfState: '6102', defaultTaxCode: '102', defaultOrigin: 0,
  };
  protected readonly cnpj = signal<string | null>(null);
  protected readonly connected = signal(false);
  protected readonly busy = signal(false);
  protected readonly saved = signal(false);
  protected readonly error = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    try {
      this.apply(await this.api.settings());
    } catch (error) {
      this.error.set(problemTitle(error));
    }
  }

  protected async save(): Promise<void> {
    this.busy.set(true);
    this.saved.set(false);
    this.error.set(null);
    try {
      const blank = (v: string | null) => (v?.trim() ? v.trim() : null);
      this.apply(
        await this.api.saveSettings({
          ...this.s,
          defaultNcm: blank(this.s.defaultNcm),
          cfopInState: blank(this.s.cfopInState),
          cfopOutOfState: blank(this.s.cfopOutOfState),
          defaultTaxCode: blank(this.s.defaultTaxCode),
        }),
      );
      this.saved.set(true);
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }

  private apply(view: { settings: FiscalSettings; issuerCnpj: string; providerConnected: boolean }): void {
    this.s = { ...view.settings };
    this.cnpj.set(view.issuerCnpj);
    this.connected.set(view.providerConnected);
  }
}

/** Origem da mercadoria (tabela da NF-e). */
export const ORIGINS = [
  { value: 0, label: '0 — Nacional' },
  { value: 1, label: '1 — Estrangeira, importação direta' },
  { value: 2, label: '2 — Estrangeira, adquirida no mercado interno' },
  { value: 3, label: '3 — Nacional, conteúdo importado > 40% e ≤ 70%' },
  { value: 4, label: '4 — Nacional, processos produtivos básicos' },
  { value: 5, label: '5 — Nacional, conteúdo importado ≤ 40%' },
  { value: 6, label: '6 — Estrangeira, importação direta, sem similar nacional' },
  { value: 7, label: '7 — Estrangeira, mercado interno, sem similar nacional' },
  { value: 8, label: '8 — Nacional, conteúdo importado > 70%' },
];
