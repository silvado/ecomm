import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { problemTitle } from '../../core/auth';
import { alertText, CertificateAlert, CertificateView, KIND_LABELS, NAME_LABELS, SecretMetadata, VaultApi } from './vault.api';

const DAY = new Intl.DateTimeFormat('pt-BR', { timeZone: 'America/Sao_Paulo', dateStyle: 'short' });
const WHEN = new Intl.DateTimeFormat('pt-BR', { timeZone: 'America/Sao_Paulo', dateStyle: 'short', timeStyle: 'short' });

/** Mesmo limite da API (VaultPanel.MaxPfxBytes). */
const MAX_PFX_BYTES = 100 * 1024;

/**
 * Cofre (RF06, só o Dono): o que está guardado cifrado (sem nunca mostrar o valor) e o certificado digital A1 da NF-e,
 * com aviso de vencimento.
 */
@Component({
  selector: 'app-vault',
  imports: [FormsModule],
  template: `
    <main class="card">
      <h1>Cofre</h1>
      <p class="muted">Chaves e certificados ficam guardados cifrados. Depois de salvos, nunca aparecem de novo — só os últimos caracteres e a validade.</p>

      <section data-certificate>
        <h2>Certificado digital A1 (NF-e)</h2>
        @if (alert(); as a) {
          <p [class]="a.level === 'notice' ? 'warn' : 'error'" role="alert" data-certificate-alert>{{ alertMessage(a) }}</p>
        }
        @if (certificate(); as c) {
          <dl class="facts">
            <dt>Titular</dt><dd data-holder>{{ c.holder }}</dd>
            @if (c.cnpj) {
              <dt>CNPJ</dt><dd>{{ c.cnpj }}</dd>
            }
            <dt>Validade</dt><dd data-expires>{{ day(c.notBefore) }} a {{ day(c.expiresAt) }}</dd>
            <dt>Enviado em</dt><dd>{{ when(c.updatedAt) }}</dd>
          </dl>
        } @else {
          <p class="muted">Nenhum certificado enviado.</p>
        }

        <div class="grid-3">
          <label>
            Arquivo do certificado (.pfx ou .p12)
            <input type="file" accept=".pfx,.p12,application/x-pkcs12" (change)="pick($event)" data-pfx />
          </label>
          <label>
            Senha do certificado
            <input type="password" [(ngModel)]="password" name="password" autocomplete="new-password" data-pfx-password />
          </label>
        </div>
        <div class="order-actions">
          <button type="button" (click)="save()" [disabled]="busy() || !pfx || !password" data-save-certificate>
            {{ certificate() ? 'Trocar certificado' : 'Enviar certificado' }}
          </button>
          @if (certificate()) {
            <button type="button" class="link danger" (click)="remove()" [disabled]="busy()" data-remove-certificate>Remover certificado</button>
          }
        </div>
        <small class="muted">Conferimos a senha, a validade e se o certificado é do CNPJ da loja antes de guardar.</small>
        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }
      </section>

      <section>
        <h2>O que está guardado</h2>
        @if (secrets().length) {
          <table>
            <thead><tr><th>Tipo</th><th>Item</th><th>Final</th><th>Validade</th><th>Atualizado</th></tr></thead>
            <tbody>
              @for (s of secrets(); track s.kind + s.name) {
                <tr data-secret>
                  <td>{{ kindLabel[s.kind] ?? s.kind }}</td>
                  <td>{{ nameLabel[s.name] ?? s.name }}</td>
                  <td>{{ s.hint ? '…' + s.hint : '—' }}</td>
                  <td>{{ s.expiresAt ? day(s.expiresAt) : '—' }}</td>
                  <td>{{ when(s.updatedAt) }}</td>
                </tr>
              }
            </tbody>
          </table>
        } @else {
          <p class="muted">Nada guardado ainda.</p>
        }
      </section>
    </main>
  `,
})
export class VaultPage implements OnInit {
  private readonly api = inject(VaultApi);

  protected readonly kindLabel = KIND_LABELS;
  protected readonly nameLabel = NAME_LABELS;
  protected readonly secrets = signal<SecretMetadata[]>([]);
  protected readonly certificate = signal<CertificateView | null>(null);
  protected readonly alert = signal<CertificateAlert | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected pfx: string | null = null;
  protected password = '';
  protected readonly day = (iso: string) => DAY.format(new Date(iso));
  protected readonly when = (iso: string) => WHEN.format(new Date(iso));
  protected readonly alertMessage = alertText;

  async ngOnInit(): Promise<void> {
    await this.load();
  }

  protected async pick(event: Event): Promise<void> {
    const file = (event.target as HTMLInputElement).files?.[0];
    this.pfx = null;
    this.error.set(null);
    if (!file) return;
    if (file.size > MAX_PFX_BYTES) {
      this.error.set('Arquivo grande demais para um certificado A1.');
      return;
    }
    const bytes = new Uint8Array(await file.arrayBuffer());
    let binary = '';
    for (const b of bytes) binary += String.fromCharCode(b);
    this.pfx = btoa(binary);
  }

  protected async save(): Promise<void> {
    if (!this.pfx || !this.password) return;
    await this.run(async () => {
      this.certificate.set(await this.api.saveCertificate(this.pfx!, this.password));
      this.password = '';
      await this.load();
    });
  }

  protected async remove(): Promise<void> {
    if (!confirm('Remover o certificado? A NF-e pela plataforma deixa de ser emitida até enviar outro.')) return;
    await this.run(async () => {
      await this.api.removeCertificate();
      await this.load();
    });
  }

  private async load(): Promise<void> {
    try {
      const [secrets, certificate, alert] = await Promise.all([this.api.secrets(), this.api.certificate(), this.api.alert()]);
      this.secrets.set(secrets);
      this.certificate.set(certificate);
      this.alert.set(alert);
    } catch (error) {
      this.error.set(problemTitle(error));
    }
  }

  private async run(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await action();
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }
}
