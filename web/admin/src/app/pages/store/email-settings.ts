import { HttpClient } from '@angular/common/http';
import { Component, inject, Injectable, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { problemTitle } from '../../core/auth';
import { feedback } from '../../core/feedback';

export type SmtpSecurity = 'startTls' | 'sslOnConnect' | 'none';

export interface SmtpServer {
  host: string;
  port: number;
  security: SmtpSecurity;
  user: string;
  senderName: string;
  senderAddress: string;
}

export interface StoreEmailSettings {
  contactEmail: string | null;
  usesOwnSmtp: boolean;
  host: string | null;
  port: number | null;
  security: SmtpSecurity | null;
  user: string | null;
  senderName: string | null;
  senderAddress: string | null;
  hasPassword: boolean;
  platformSender: string;
}

/** E-mail de envio da loja (RF16, Q38) — só o Dono (permissão vaultManage): a senha vai para o cofre e nunca volta. */
@Injectable({ providedIn: 'root' })
export class EmailSettingsApi {
  private readonly http = inject(HttpClient);

  get(): Promise<StoreEmailSettings> {
    return firstValueFrom(this.http.get<StoreEmailSettings>('/api/painel/loja/email'));
  }

  save(request: { contactEmail: string | null; useOwnSmtp: boolean; smtp: SmtpServer | null; password: string | null }): Promise<StoreEmailSettings> {
    return firstValueFrom(this.http.put<StoreEmailSettings>('/api/painel/loja/email', request, { context: feedback('E-mail de envio salvo.') }));
  }

  test(destinatario: string): Promise<{ outcome: 'sent' | 'failed'; message: string | null }> {
    return firstValueFrom(this.http.post<{ outcome: 'sent' | 'failed'; message: string | null }>('/api/painel/loja/email/teste', { destinatario }, { context: feedback(null) }));
  }
}

/**
 * Em Dados da loja: e-mail de contato (para onde vão as respostas dos compradores) e, se a loja quiser, o servidor de
 * e-mail próprio (Google Workspace, Microsoft 365 ou outro). Sem isso, os avisos saem pelo remetente da plataforma.
 */
@Component({
  selector: 'app-email-settings',
  imports: [FormsModule],
  template: `
    <section data-email-settings>
      <h2>E-mail de envio</h2>
      <p class="muted">
        A loja manda ao comprador o link de acesso aos pedidos e os avisos de pedido recebido, pago, enviado e cancelado.
      </p>
      <label>
        E-mail de contato da loja
        <input type="email" [(ngModel)]="contactEmail" name="contactEmail" maxlength="254" placeholder="contato@sualoja.com.br" data-contact />
      </label>
      <p class="muted">As respostas dos compradores vão para este e-mail.</p>

      <label class="checkbox">
        <input type="checkbox" [(ngModel)]="useOwnSmtp" name="useOwnSmtp" data-own-smtp />
        Enviar pelo e-mail da própria loja (Google Workspace, Microsoft 365 ou outro)
      </label>
      @if (!useOwnSmtp) {
        <p class="muted">Os e-mails saem de <strong>{{ platformSender() }}</strong> com o nome da loja.</p>
      } @else {
        <div class="grid-3">
          <label>Servidor <input [(ngModel)]="smtp.host" name="host" placeholder="smtp.gmail.com" maxlength="255" data-host /></label>
          <label>Porta <input type="number" [(ngModel)]="smtp.port" name="port" min="1" max="65535" /></label>
          <label>
            Segurança
            <select [(ngModel)]="smtp.security" name="security">
              <option value="startTls">STARTTLS (porta 587)</option>
              <option value="sslOnConnect">SSL/TLS (porta 465)</option>
              <option value="none">Nenhuma (só rede interna)</option>
            </select>
          </label>
          <label>Usuário <input [(ngModel)]="smtp.user" name="user" maxlength="254" autocomplete="off" data-user /></label>
          <label>
            Senha
            <input type="password" [(ngModel)]="password" name="password" maxlength="200" autocomplete="new-password"
              [placeholder]="hasPassword() ? 'Salva — deixe em branco para manter' : ''" data-password />
          </label>
          <label>Nome do remetente <input [(ngModel)]="smtp.senderName" name="senderName" maxlength="80" data-sender-name /></label>
          <label>E-mail do remetente <input type="email" [(ngModel)]="smtp.senderAddress" name="senderAddress" maxlength="254" data-sender-address /></label>
        </div>
        <p class="muted">No Google Workspace, use smtp.gmail.com, porta 587 e uma "senha de app" (não a senha normal da conta).</p>
      }

      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
      <div class="order-actions">
        <button type="button" (click)="save()" [disabled]="busy()" data-save-email>Salvar e-mail de envio</button>
      </div>

      <h3>Testar</h3>
      <div class="order-actions">
        <input type="email" [(ngModel)]="testTo" name="testTo" placeholder="seu@email.com.br" aria-label="Enviar teste para" data-test-to />
        <button type="button" (click)="test()" [disabled]="busy() || !testTo.trim()" data-send-test>Enviar e-mail de teste</button>
      </div>
      @if (testResult(); as r) {
        <p [class]="r.ok ? 'ok' : 'error'" role="status">{{ r.text }}</p>
      }
    </section>
  `,
})
export class EmailSettingsSection implements OnInit {
  private readonly api = inject(EmailSettingsApi);

  protected contactEmail = '';
  protected useOwnSmtp = false;
  protected smtp: SmtpServer = { host: '', port: 587, security: 'startTls', user: '', senderName: '', senderAddress: '' };
  protected password = '';
  protected testTo = '';
  protected readonly hasPassword = signal(false);
  protected readonly platformSender = signal('');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly testResult = signal<{ ok: boolean; text: string } | null>(null);

  async ngOnInit(): Promise<void> {
    try {
      this.apply(await this.api.get());
    } catch (error) {
      this.error.set(problemTitle(error));
    }
  }

  protected async save(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      this.apply(
        await this.api.save({
          contactEmail: this.contactEmail.trim() || null,
          useOwnSmtp: this.useOwnSmtp,
          smtp: this.useOwnSmtp ? { ...this.smtp, port: Number(this.smtp.port) } : null,
          password: this.password || null,
        }),
      );
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }

  protected async test(): Promise<void> {
    this.busy.set(true);
    this.testResult.set(null);
    try {
      const result = await this.api.test(this.testTo.trim());
      this.testResult.set(
        result.outcome === 'sent'
          ? { ok: true, text: `E-mail de teste enviado para ${this.testTo.trim()}. Confira a caixa de entrada (e o spam).` }
          : { ok: false, text: result.message ?? 'Não foi possível enviar.' },
      );
    } catch (error) {
      this.testResult.set({ ok: false, text: problemTitle(error) });
    } finally {
      this.busy.set(false);
    }
  }

  private apply(s: StoreEmailSettings): void {
    this.contactEmail = s.contactEmail ?? '';
    this.useOwnSmtp = s.usesOwnSmtp;
    if (s.usesOwnSmtp)
      this.smtp = {
        host: s.host ?? '',
        port: s.port ?? 587,
        security: s.security ?? 'startTls',
        user: s.user ?? '',
        senderName: s.senderName ?? '',
        senderAddress: s.senderAddress ?? '',
      };
    this.password = '';
    this.hasPassword.set(s.hasPassword);
    this.platformSender.set(s.platformSender);
  }
}
