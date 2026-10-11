import { DatePipe } from '@angular/common';
import { Component, inject, OnInit, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { problemTitle } from '../../core/auth';
import { MercadoPagoSettings, PaymentsApi } from './payments.api';

/** Mesmo limite da API: só o formato é conferido aqui; a chave errada aparece no primeiro pagamento. */
export const CREDENTIAL_MIN = 16;
export const CREDENTIAL_MAX = 500;

@Component({
  selector: 'app-payments',
  imports: [ReactiveFormsModule, DatePipe],
  styles: `
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
        Os pagamentos da loja vão direto para a sua conta do Mercado Pago (cartão e Pix, na página do próprio Mercado
        Pago). As chaves ficam guardadas cifradas e nunca são mostradas de novo.
      </p>

      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
      @if (notice()) {
        <p class="notice" role="status">{{ notice() }}</p>
      }

      @if (settings(); as s) {
        @if (s.configured) {
          <p>
            <span class="badge ok">Mercado Pago conectado</span>
            Access Token terminado em <strong>{{ s.accessTokenHint || '—' }}</strong>, chave secreta terminada em
            <strong>{{ s.webhookSecretHint || '—' }}</strong>
            @if (s.updatedAt) {
              <span class="muted">(atualizado em {{ s.updatedAt | date: 'dd/MM/yyyy HH:mm' }})</span>
            }
          </p>
          <button type="button" class="link danger" (click)="remove()" [disabled]="busy()">Desconectar o Mercado Pago</button>
        } @else {
          <p><span class="badge warn">Sem pagamento online</span> Conecte o Mercado Pago para receber pela loja.</p>
        }

        <h2>{{ s.configured ? 'Trocar as chaves' : 'Conectar o Mercado Pago' }}</h2>
        <ol class="muted">
          <li>No Mercado Pago, abra <em>Suas integrações</em> e crie (ou escolha) uma aplicação.</li>
          <li>Em <em>Credenciais de produção</em>, copie o <strong>Access Token</strong>.</li>
          <li>
            Em <em>Webhooks → Configurar notificações</em>, informe esta URL, marque o evento <em>Pagamentos</em>, salve e
            copie a <strong>assinatura secreta</strong>:
            <code class="copy">{{ s.notificationUrl }}</code>
          </li>
        </ol>
        <form [formGroup]="form" (ngSubmit)="save()">
          <label>
            Access Token
            <input type="password" formControlName="accessToken" autocomplete="off" spellcheck="false" />
          </label>
          <label>
            Assinatura secreta (Webhooks)
            <input type="password" formControlName="webhookSecret" autocomplete="off" spellcheck="false" />
          </label>
          <button type="submit" [disabled]="form.invalid || busy()">Salvar</button>
        </form>
      } @else {
        <p class="muted">Carregando…</p>
      }
    </main>
  `,
})
export class PaymentsPage implements OnInit {
  private readonly api = inject(PaymentsApi);

  protected readonly settings = signal<MercadoPagoSettings | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly notice = signal<string | null>(null);
  private readonly credential = [Validators.required, Validators.minLength(CREDENTIAL_MIN), Validators.maxLength(CREDENTIAL_MAX)];
  protected readonly form = inject(FormBuilder).nonNullable.group({
    accessToken: ['', this.credential],
    webhookSecret: ['', this.credential],
  });

  async ngOnInit(): Promise<void> {
    try {
      this.settings.set(await this.api.get());
    } catch (error) {
      this.error.set(problemTitle(error));
    }
  }

  save(): Promise<void> {
    const { accessToken, webhookSecret } = this.form.getRawValue();
    return this.run(async () => {
      this.settings.set(await this.api.save(accessToken.trim(), webhookSecret.trim()));
      // As chaves não ficam no formulário depois de salvas.
      this.form.reset({ accessToken: '', webhookSecret: '' });
      this.notice.set('Mercado Pago conectado. Os próximos pedidos já podem ser pagos pela loja.');
    });
  }

  remove(): Promise<void> {
    if (!confirm('Desconectar o Mercado Pago? A loja deixa de receber pagamentos online até você conectar de novo.')) {
      return Promise.resolve();
    }
    return this.run(async () => {
      this.settings.set(await this.api.remove());
      this.notice.set('Mercado Pago desconectado.');
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
