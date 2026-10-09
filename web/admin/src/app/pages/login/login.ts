import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthStore, problemTitle } from '../../core/auth';
import { landingFor } from '../../core/guards';

@Component({
  selector: 'app-login',
  imports: [ReactiveFormsModule],
  template: `
    <main class="card narrow">
      <h1>Entrar no painel</h1>
      <form [formGroup]="form" (ngSubmit)="submit()">
        <label>
          E-mail
          <input type="email" formControlName="email" autocomplete="username" required />
        </label>
        <label>
          Senha
          <input type="password" formControlName="password" autocomplete="current-password" required />
        </label>
        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }
        <button type="submit" [disabled]="form.invalid || busy()">{{ busy() ? 'Entrando…' : 'Entrar' }}</button>
      </form>
    </main>
  `,
})
export class LoginPage {
  private readonly auth = inject(AuthStore);
  private readonly router = inject(Router);

  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', Validators.required],
  });

  async submit(): Promise<void> {
    if (this.form.invalid) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const { email, password } = this.form.getRawValue();
      const session = await this.auth.login(email, password);
      await this.router.navigateByUrl(landingFor(session));
    } catch (error) {
      this.error.set(problemTitle(error));
      this.form.controls.password.reset();
    } finally {
      this.busy.set(false);
    }
  }
}
