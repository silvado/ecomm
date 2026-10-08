import { Component, inject } from '@angular/core';
import { AuthStore } from '../../core/auth';

@Component({
  selector: 'app-home',
  template: `
    <main class="card">
      <h1>Olá!</h1>
      <p>Você está no painel da loja <strong>{{ auth.tenant()?.name }}</strong>.</p>
      <p class="muted">Catálogo, pedidos e integrações chegam nas próximas versões.</p>
    </main>
  `,
})
export class HomePage {
  protected readonly auth = inject(AuthStore);
}
