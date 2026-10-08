import { Component, inject, OnInit, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { AuthStore, problemTitle, Role } from '../../core/auth';
import { RoleLabelPipe } from '../../shared/role-label';
import { PASSWORD_MAX, PASSWORD_MIN } from '../change-password/change-password';
import { StoreUser, UsersApi } from './users.api';

@Component({
  selector: 'app-users',
  imports: [ReactiveFormsModule, RoleLabelPipe],
  template: `
    <main class="card">
      <h1>Usuários da loja</h1>

      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }

      <table>
        <thead>
          <tr>
            <th>E-mail</th>
            <th>Perfil</th>
            <th>Situação</th>
            <th><span class="sr-only">Ações</span></th>
          </tr>
        </thead>
        <tbody>
          @for (user of users(); track user.userId) {
            <tr>
              <td>
                {{ user.email }}
                @if (user.userId === me()) {
                  <span class="muted">(você)</span>
                }
              </td>
              <td>
                <select [value]="user.role" (change)="changeRole(user, $any($event.target).value)" [attr.aria-label]="'Perfil de ' + user.email">
                  @for (role of roles; track role) {
                    <option [value]="role">{{ role | roleLabel }}</option>
                  }
                </select>
              </td>
              <td>
                @if (user.lockedOut) {
                  <span class="badge warn">Bloqueado</span>
                } @else if (user.mustChangePassword) {
                  <span class="badge">Senha provisória</span>
                } @else {
                  <span class="badge ok">Ativo</span>
                }
              </td>
              <td class="actions">
                @if (user.lockedOut) {
                  <button type="button" class="link" (click)="unlock(user)">Desbloquear</button>
                }
                <button type="button" class="link danger" (click)="remove(user)">Remover</button>
              </td>
            </tr>
          } @empty {
            <tr><td colspan="4" class="muted">Carregando…</td></tr>
          }
        </tbody>
      </table>

      <h2>Adicionar usuário</h2>
      <p class="muted">
        Informe uma senha provisória e repasse à pessoa; ela vai criar a própria senha no primeiro acesso. Se o e-mail já
        tiver conta em outra loja, ela só ganha acesso a esta e continua com a senha que já usa.
      </p>
      <form [formGroup]="form" (ngSubmit)="add()" class="inline">
        <label>
          E-mail
          <input type="email" formControlName="email" required />
        </label>
        <label>
          Perfil
          <select formControlName="role">
            @for (role of roles; track role) {
              <option [value]="role">{{ role | roleLabel }}</option>
            }
          </select>
        </label>
        <label>
          Senha provisória
          <input type="text" formControlName="temporaryPassword" autocomplete="off" />
          <small>De {{ min }} a {{ max }} caracteres.</small>
        </label>
        <button type="submit" [disabled]="form.invalid || busy()">Adicionar</button>
      </form>
    </main>
  `,
})
export class UsersPage implements OnInit {
  private readonly api = inject(UsersApi);
  private readonly auth = inject(AuthStore);

  protected readonly roles: Role[] = ['owner', 'operator'];
  protected readonly min = PASSWORD_MIN;
  protected readonly max = PASSWORD_MAX;
  protected readonly users = signal<StoreUser[]>([]);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly me = () => this.auth.current()?.userId;
  protected readonly form = inject(FormBuilder).nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    role: ['operator' as Role, Validators.required],
    temporaryPassword: ['', [Validators.minLength(PASSWORD_MIN), Validators.maxLength(PASSWORD_MAX)]],
  });

  ngOnInit(): Promise<void> {
    return this.reload();
  }

  add(): Promise<void> {
    const { email, role, temporaryPassword } = this.form.getRawValue();
    return this.run(async () => {
      await this.api.add(email, role, temporaryPassword);
      this.form.reset({ email: '', role: 'operator', temporaryPassword: '' });
    });
  }

  changeRole(user: StoreUser, role: Role): Promise<void> {
    return this.run(() => this.api.changeRole(user.userId, role));
  }

  unlock(user: StoreUser): Promise<void> {
    return this.run(() => this.api.unlock(user.userId));
  }

  remove(user: StoreUser): Promise<void> {
    if (!confirm(`Remover o acesso de ${user.email} a esta loja?`)) return Promise.resolve();
    return this.run(() => this.api.remove(user.userId));
  }

  /** Executa a ação e sempre recarrega a lista (também desfaz um select alterado quando a API recusa). */
  private async run(action: () => Promise<unknown>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await action();
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      await this.reload();
      this.busy.set(false);
    }
  }

  private async reload(): Promise<void> {
    try {
      this.users.set(await this.api.list());
    } catch (error) {
      this.error.set(problemTitle(error));
    }
  }
}
