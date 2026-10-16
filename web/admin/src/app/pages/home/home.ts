import { Component, inject } from '@angular/core';
import { AuthStore } from '../../core/auth';
import { OnboardingCard } from '../onboarding/onboarding';
import { PlanCard } from './plan-card';

@Component({
  selector: 'app-home',
  imports: [OnboardingCard, PlanCard],
  template: `
    <main class="card">
      <h1>Olá!</h1>
      <p>Você está no painel da loja <strong>{{ auth.tenant()?.name }}</strong>.</p>
      <p class="muted">Use o menu acima para peças, pedidos e configurações. As integrações com os canais de venda chegam nas próximas versões.</p>
    </main>
    @if (auth.can('storeManage')) {
      <app-onboarding-card />
      <app-plan-card />
    }
  `,
})
export class HomePage {
  protected readonly auth = inject(AuthStore);
}
