import { DatePipe } from '@angular/common';
import { Component, inject, OnInit, signal } from '@angular/core';
import { FormBuilder, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { problemTitle } from '../../core/auth';
import { GatewayName, GatewaySettings, PaymentsApi } from './payments.api';

/** Mesmos limites da API: só o formato é conferido aqui; a chave errada aparece no primeiro pagamento. */
export const CREDENTIAL_MIN = 16;
export const CREDENTIAL_MAX = 500;
const GUID = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;

interface Field {
  name: string;
  label: string;
  guid?: boolean;
}

interface Gateway {
  name: GatewayName;
  title: string;
  fields: Field[];
  steps: string[];
  notificationStep: string;
}

const GATEWAYS: Gateway[] = [
  {
    name: 'mercado-pago',
    title: 'Mercado Pago',
    fields: [
      { name: 'accessToken', label: 'Access Token' },
      { name: 'webhookSecret', label: 'Assinatura secreta (Webhooks)' },
    ],
    steps: ['No Mercado Pago, abra Suas integrações e crie (ou escolha) uma aplicação.', 'Em Credenciais de produção, copie o Access Token.'],
    notificationStep: 'Em Webhooks → Configurar notificações, informe esta URL, marque o evento Pagamentos, salve e copie a assinatura secreta:',
  },
  {
    name: 'cielo',
    title: 'Cielo (Checkout Cielo / Link de Pagamento)',
    fields: [
      { name: 'merchantId', label: 'MerchantId', guid: true },
      { name: 'clientId', label: 'Client ID' },
      { name: 'clientSecret', label: 'Client Secret' },
    ],
    steps: [
      'No site da Cielo, copie o MerchantId da sua loja (Configurações → Dados cadastrais).',
      'Em E-commerce → Link de Pagamento → Configurações → Credenciais de API, gere e copie o Client ID e o Client Secret (aparecem só uma vez).',
      'Desligue o modo de teste do Checkout antes de vender: pedidos de teste também seriam confirmados.',
    ],
    notificationStep: 'Nas configurações da loja na Cielo, informe esta URL como URL de notificação e de mudança de status:',
  },
];

@Component({
  selector: 'app-payments',
  imports: [ReactiveFormsModule, DatePipe],
  styles: `
    section + section {
      margin-top: 2rem;
      padding-top: 1rem;
      border-top: 1px solid var(--border);
    }
    .copy {
      display: block;
      margin-top: 0.4rem;
      padding: 0.5rem;
      background: var(--bg);
      border: 1px solid var(--border);
      overflow-wrap: anywhere;
      user-select: all;
    }
    .notice {
      margin: 0;
    }
  `,
  template: `
    <main class="card">
      <h1>Pagamentos</h1>
      <p class="muted">
        Os pagamentos da loja vão direto para a sua conta no gateway (cartão e Pix, na página do próprio gateway). As chaves
        ficam guardadas cifradas e nunca são mostradas de novo. Com dois gateways conectados, a loja cobra pelo último
        salvo; pagamentos já iniciados no outro continuam sendo confirmados.
      </p>

      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
      @if (notice()) {
        <p class="notice" role="status">{{ notice() }}</p>
      }

      @if (loaded()) {
        @for (g of gateways; track g.name) {
          <section [attr.data-gateway]="g.name">
            <h2>{{ g.title }}</h2>
            @if (settings(g.name); as s) {
              @if (s.configured) {
                <p>
                  @if (s.active) {
                    <span class="badge ok">Em uso pela loja</span>
                  } @else {
                    <span class="badge">Conectado</span>
                  }
                  @for (c of s.credentials; track c.field) {
                    <span class="muted"> {{ label(g, c.field) }} terminado em <strong>{{ c.hint || '—' }}</strong>;</span>
                  }
                  @if (s.updatedAt) {
                    <span class="muted"> atualizado em {{ s.updatedAt | date: 'dd/MM/yyyy HH:mm' }}</span>
                  }
                </p>
                <button type="button" class="link danger" (click)="remove(g)" [disabled]="busy()">Desconectar</button>
              } @else {
                <p><span class="badge warn">Não conectado</span></p>
              }

              <h3>{{ s.configured ? 'Trocar as chaves' : 'Conectar' }}</h3>
              <ol class="muted">
                @for (step of g.steps; track step) {
                  <li>{{ step }}</li>
                }
                <li>
                  {{ g.notificationStep }}
                  <code class="copy">{{ s.notificationUrl }}</code>
                </li>
              </ol>
              <form [formGroup]="forms[g.name]" (ngSubmit)="save(g)">
                @for (f of g.fields; track f.name) {
                  <label>
                    {{ f.label }}
                    <input [type]="f.guid ? 'text' : 'password'" [formControlName]="f.name" autocomplete="off" spellcheck="false" />
                  </label>
                }
                <button type="submit" [disabled]="forms[g.name].invalid || busy()">Salvar</button>
              </form>
            }
          </section>
        }
      } @else {
        <p class="muted">Carregando…</p>
      }
    </main>
  `,
})
export class PaymentsPage implements OnInit {
  private readonly api = inject(PaymentsApi);
  private readonly fb = inject(FormBuilder).nonNullable;

  protected readonly gateways = GATEWAYS;
  protected readonly list = signal<GatewaySettings[]>([]);
  protected readonly loaded = signal(false);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly notice = signal<string | null>(null);
  protected readonly forms = Object.fromEntries(
    GATEWAYS.map((g) => [
      g.name,
      this.fb.group(
        Object.fromEntries(
          g.fields.map((f) => [
            f.name,
            ['', f.guid ? [Validators.required, Validators.pattern(GUID)] : [Validators.required, Validators.minLength(CREDENTIAL_MIN), Validators.maxLength(CREDENTIAL_MAX)]],
          ]),
        ),
      ),
    ]),
  ) as Record<GatewayName, FormGroup>;

  async ngOnInit(): Promise<void> {
    try {
      this.list.set(await this.api.list());
      this.loaded.set(true);
    } catch (error) {
      this.error.set(problemTitle(error));
    }
  }

  protected settings(name: GatewayName): GatewaySettings | undefined {
    return this.list().find((s) => s.gateway === name);
  }

  protected label(gateway: Gateway, field: string): string {
    return gateway.fields.find((f) => f.name === field)?.label ?? field;
  }

  save(gateway: Gateway): Promise<void> {
    const form = this.forms[gateway.name];
    const values = Object.fromEntries(Object.entries(form.getRawValue() as Record<string, string>).map(([k, v]) => [k, v.trim()]));
    return this.run(async () => {
      this.list.set(await this.api.save(gateway.name, values));
      // As chaves não ficam no formulário depois de salvas.
      form.reset();
      this.notice.set(`${gateway.title} conectado. Os próximos pedidos já podem ser pagos pela loja.`);
    });
  }

  remove(gateway: Gateway): Promise<void> {
    if (!confirm(`Desconectar ${gateway.title}? Pagamentos iniciados nele deixam de ser confirmados automaticamente.`)) {
      return Promise.resolve();
    }
    return this.run(async () => {
      this.list.set(await this.api.remove(gateway.name));
      this.notice.set(`${gateway.title} desconectado.`);
    });
  }

  private async run(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    this.notice.set(null);
    try {
      await action();
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }
}
