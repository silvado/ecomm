import { Component, inject } from '@angular/core';
import { StoreContext } from './store';

@Component({
  selector: 'app-home',
  template: `
    <section class="hero">
      <h1>{{ ctx.store?.name }}</h1>
      @if (ctx.store?.texts?.about) {
        <p class="pre">{{ ctx.store?.texts?.about }}</p>
      }
      <p class="muted">Catálogo de peças em breve.</p>
    </section>
  `,
})
export class HomePage {
  protected readonly ctx = inject(StoreContext);
}

@Component({
  selector: 'app-return-policy',
  template: `
    <section>
      <h1>Trocas e devoluções</h1>
      <p class="pre">{{ ctx.store?.texts?.returnPolicy || 'Consulte a loja.' }}</p>
    </section>
  `,
})
export class ReturnPolicyPage {
  protected readonly ctx = inject(StoreContext);
}
