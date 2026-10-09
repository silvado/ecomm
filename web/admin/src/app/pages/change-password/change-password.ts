import { Component, inject, signal } from '@angular/core';
import { AbstractControl, FormBuilder, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthStore, problemTitle } from '../../core/auth';
import { landingFor } from '../../core/guards';

/** Mesma política da API (PasswordPolicy): de 10 a 128 caracteres. */
export const PASSWORD_MIN = 10;
export const PASSWORD_MAX = 128;

const sameAsNew = (group: AbstractControl): ValidationErrors | null =>
  group.get('newPassword')?.value === group.get('confirmation')?.value ? null : { mismatch: true };

@Component({
  selector: 'app-change-password',
  imports: [ReactiveFormsModule],
  template: `
    <main class="card narrow">
      <h1>{{ mustChange() ? 'Crie sua senha' : 'Trocar senha' }}</h1>
      @if (mustChange()) {
        <p>Você entrou com uma senha provisória. Escolha uma senha só sua para continuar.</p>
      }
      <form [formGroup]="form" (ngSubmit)="submit()">
        <label>
          Senha atual
          <input type="password" formControlName="currentPassword" autocomplete="current-password" required />
        </label>
        <label>
          Nova senha
          <input type="password" formControlName="newPassword" autocomplete="new-password" required />
          <small>De {{ min }} a {{ max }} caracteres.</small>
        </label>
        <label>
          Repita a nova senha
          <input type="password" formControlName="confirmation" autocomplete="new-password" required />
        </label>
        @if (form.hasError('mismatch') && form.controls.confirmation.dirty) {
          <p class="error">As senhas não conferem.</p>
        }
        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }
        <button type="submit" [disabled]="form.invalid || busy()">Salvar senha</button>
      </form>
    </main>
  `,
})
export class ChangePasswordPage {
  private readonly auth = inject(AuthStore);
  private readonly router = inject(Router);

  protected readonly min = PASSWORD_MIN;
  protected readonly max = PASSWORD_MAX;
  protected readonly mustChange = () => this.auth.current()?.mustChangePassword ?? false;
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly form = inject(FormBuilder).nonNullable.group(
    {
      currentPassword: ['', Validators.required],
      newPassword: ['', [Validators.required, Validators.minLength(PASSWORD_MIN), Validators.maxLength(PASSWORD_MAX)]],
      confirmation: ['', Validators.required],
    },
    { validators: sameAsNew },
  );

  async submit(): Promise<void> {
    if (this.form.invalid) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const { currentPassword, newPassword } = this.form.getRawValue();
      await this.router.navigateByUrl(landingFor(await this.auth.changePassword(currentPassword, newPassword)));
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }
}
