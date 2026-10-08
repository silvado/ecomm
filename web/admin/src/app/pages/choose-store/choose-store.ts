import { Component, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { AuthStore, problemTitle } from '../../core/auth';
import { landingFor } from '../../core/guards';
import { RoleLabelPipe } from '../../shared/role-label';

@Component({
  selector: 'app-choose-store',
  imports: [RoleLabelPipe],
  template: `
    <main class="card narrow">
      <h1>Escolha a loja</h1>
      <ul class="stores">
        @for (tenant of auth.current()?.tenants ?? []; track tenant.id) {
          <li>
            <button type="button" (click)="choose(tenant.id)" [disabled]="busy()">
              <strong>{{ tenant.name }}</strong>
              <span>{{ tenant.role | roleLabel }}</span>
            </button>
          </li>
        }
      </ul>
      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
    </main>
  `,
})
export class ChooseStorePage {
  protected readonly auth = inject(AuthStore);
  private readonly router = inject(Router);

  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  async choose(tenantId: string): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await this.router.navigateByUrl(landingFor(await this.auth.selectTenant(tenantId)));
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }
}
