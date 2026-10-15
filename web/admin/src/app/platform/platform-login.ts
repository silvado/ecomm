import { Component, inject, signal } from '@angular/core';
import { AbstractControl, FormBuilder, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { problemTitle } from '../core/auth';
import { PASSWORD_MAX, PASSWORD_MIN } from '../pages/change-password/change-password';
import { MfaEnrollment, PlatformAuthStore, PlatformStep } from './platform-auth';

type View = 'login' | 'code' | 'password' | 'enroll' | 'recovery';

const sameAsNew = (group: AbstractControl): ValidationErrors | null =>
  group.get('newPassword')?.value === group.get('confirmation')?.value ? null : { mismatch: true };

/** Agrupa a chave em blocos de 4 para digitar no app sem errar. */
export const groupKey = (secret: string) => secret.match(/.{1,4}/g)?.join(' ') ?? secret;

/**
 * Login do superadmin em etapas (ADR-0007): senha → código do app. No primeiro acesso: senha provisória →
 * senha nova → cadastro do app autenticador → códigos de recuperação (mostrados uma vez).
 */
@Component({
  selector: 'app-platform-login',
  imports: [ReactiveFormsModule],
  template: `
    <main class="card narrow">
      <h1>Painel da plataforma</h1>
      @switch (view()) {
        @case ('login') {
          <form [formGroup]="login" (ngSubmit)="submitLogin()">
            <label>E-mail <input type="email" formControlName="email" autocomplete="username" required /></label>
            <label>Senha <input type="password" formControlName="password" autocomplete="current-password" required /></label>
            <button type="submit" [disabled]="login.invalid || busy()">{{ busy() ? 'Entrando…' : 'Entrar' }}</button>
          </form>
        }
        @case ('code') {
          <p>Digite o código de 6 dígitos do seu app autenticador. Sem o celular, use um dos códigos de recuperação.</p>
          <form [formGroup]="code" (ngSubmit)="submitCode()">
            <label>Código <input formControlName="code" autocomplete="one-time-code" inputmode="text" required data-code /></label>
            <button type="submit" [disabled]="code.invalid || busy()">Confirmar</button>
            <button type="button" class="link" (click)="restart()">Voltar</button>
          </form>
        }
        @case ('password') {
          <p>Primeiro acesso: troque a senha provisória por uma senha só sua.</p>
          <form [formGroup]="password" (ngSubmit)="submitPassword()">
            <label>Senha provisória <input type="password" formControlName="currentPassword" autocomplete="current-password" required /></label>
            <label>
              Nova senha
              <input type="password" formControlName="newPassword" autocomplete="new-password" required />
              <small>De {{ min }} a {{ max }} caracteres.</small>
            </label>
            <label>Repita a nova senha <input type="password" formControlName="confirmation" autocomplete="new-password" required /></label>
            @if (password.hasError('mismatch') && password.controls.confirmation.dirty) {
              <p class="error">As senhas não conferem.</p>
            }
            <button type="submit" [disabled]="password.invalid || busy()">Salvar senha</button>
          </form>
        }
        @case ('enroll') {
          <p>O acesso à plataforma exige um app autenticador (Google Authenticator, Microsoft Authenticator, Authy ou outro).</p>
          @if (enrollment(); as e) {
            <ol>
              <li>No app, escolha <strong>adicionar conta</strong> e depois <strong>inserir chave de configuração</strong>.</li>
              <li>
                Digite esta chave (tipo: baseada em tempo):
                <p><code class="pre" data-secret>{{ grouped(e.secret) }}</code></p>
                <small class="muted">No celular, você também pode <a [href]="e.provisioningUri">abrir direto no app</a>.</small>
              </li>
              <li>Digite abaixo o código de 6 dígitos que o app mostrar.</li>
            </ol>
            <form [formGroup]="code" (ngSubmit)="submitEnrollment()">
              <label>Código <input formControlName="code" autocomplete="one-time-code" inputmode="numeric" required data-code /></label>
              <button type="submit" [disabled]="code.invalid || busy()">Ativar</button>
            </form>
          } @else {
            <button type="button" (click)="startEnrollment()" [disabled]="busy()">Gerar chave</button>
          }
        }
        @case ('recovery') {
          <p><strong>Guarde estes códigos de recuperação</strong> em lugar seguro (gerenciador de senhas ou papel).</p>
          <p>Cada um entra uma única vez, no lugar do código do app, se você perder o celular. Eles não aparecem de novo.</p>
          <ul class="pre" data-recovery>
            @for (c of recoveryCodes(); track c) {
              <li><code>{{ c }}</code></li>
            }
          </ul>
          <button type="button" (click)="finish()">Já guardei, continuar</button>
        }
      }
      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
    </main>
  `,
})
export class PlatformLoginPage {
  private readonly auth = inject(PlatformAuthStore);
  private readonly router = inject(Router);
  private readonly fb = inject(FormBuilder).nonNullable;

  protected readonly min = PASSWORD_MIN;
  protected readonly max = PASSWORD_MAX;
  protected readonly grouped = groupKey;
  protected readonly view = signal<View>('login');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly enrollment = signal<MfaEnrollment | null>(null);
  protected readonly recoveryCodes = signal<string[]>([]);

  protected readonly login = this.fb.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', Validators.required],
  });
  protected readonly code = this.fb.group({ code: ['', [Validators.required, Validators.minLength(6)]] });
  protected readonly password = this.fb.group(
    {
      currentPassword: ['', Validators.required],
      newPassword: ['', [Validators.required, Validators.minLength(PASSWORD_MIN), Validators.maxLength(PASSWORD_MAX)]],
      confirmation: ['', Validators.required],
    },
    { validators: sameAsNew },
  );

  submitLogin = () =>
    this.run(async () => {
      const { email, password } = this.login.getRawValue();
      const step = await this.auth.login(email, password);
      this.login.controls.password.reset();
      this.after(step);
    }, () => this.login.controls.password.reset());

  submitCode = () =>
    this.run(async () => {
      await this.auth.verifyCode(this.code.getRawValue().code);
      await this.router.navigateByUrl('/plataforma');
    }, () => this.code.reset());

  submitPassword = () =>
    this.run(async () => {
      const { currentPassword, newPassword } = this.password.getRawValue();
      this.after(await this.auth.changePassword(currentPassword, newPassword));
      this.password.reset();
    });

  startEnrollment = () => this.run(async () => this.enrollment.set(await this.auth.startEnrollment()));

  submitEnrollment = () =>
    this.run(async () => {
      const step = await this.auth.confirmEnrollment(this.code.getRawValue().code);
      this.enrollment.set(null);
      this.recoveryCodes.set(step.recoveryCodes ?? []);
      this.view.set('recovery');
    }, () => this.code.reset());

  async finish(): Promise<void> {
    this.recoveryCodes.set([]);
    await this.router.navigateByUrl('/plataforma');
  }

  restart(): void {
    this.auth.reset();
    this.code.reset();
    this.error.set(null);
    this.view.set('login');
  }

  private after(step: PlatformStep): void {
    this.code.reset();
    if (step.stage === 'codigo') this.view.set('code');
    else if (step.admin.mustChangePassword) this.view.set('password');
    else this.view.set('enroll');
  }

  private async run(action: () => Promise<void>, onError?: () => void): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await action();
    } catch (error) {
      this.error.set(problemTitle(error));
      onError?.();
    } finally {
      this.busy.set(false);
    }
  }
}
