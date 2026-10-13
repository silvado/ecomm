import { HttpClient } from '@angular/common/http';
import { Component, inject, OnInit, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export interface PlanUsage {
  key: string;
  used: number;
  limit: number | null;
}

export interface PlanView {
  code: string;
  name: string;
  usage: PlanUsage[];
  features: string[];
}

const USAGE_LABELS: Record<string, string> = {
  active_parts: 'Peças ativas',
  users: 'Usuários',
  ai_listings_month: 'Cadastros com IA neste mês',
};

const FEATURE_LABELS: Record<string, string> = {
  'channel:mercado-livre': 'Mercado Livre',
  'channel:meta': 'Instagram / Facebook',
  'channel:olx': 'OLX',
  custom_domain: 'Domínio próprio',
  'ai:part_from_photos': 'Cadastro de peça por foto (IA)',
  'ai:buyer_answers': 'Respostas a compradores (IA)',
  'ai:price_suggestion': 'Preço sugerido (IA)',
  'ai:daily_summary': 'Resumo diário (IA)',
};

/** "Seu plano" (RF37), no Início, para o Dono: uso dos limites e o que o plano inclui. */
@Component({
  selector: 'app-plan-card',
  template: `
    @if (plan(); as p) {
      <section class="card" data-plan-card>
        <h2>Seu plano: {{ p.name }}</h2>
        <ul class="plan-usage">
          @for (u of p.usage; track u.key) {
            <li [class.warn]="u.limit !== null && u.used >= u.limit" [attr.data-usage]="u.key">
              <span>{{ usageLabel(u.key) }}</span>
              <strong>{{ format(u.used) }}{{ u.limit === null ? ' (sem limite)' : ' de ' + format(u.limit) }}</strong>
              @if (u.limit !== null && u.limit > 0) {
                <progress [value]="u.used" [max]="u.limit" [attr.aria-label]="usageLabel(u.key)"></progress>
              }
            </li>
          }
        </ul>
        <p class="muted">Inclui: {{ featureList(p.features) }}. Loja virtual, balcão, nota fiscal e e-mails estão em todos os planos.</p>
        @if (atLimit(p)) {
          <p class="warn" role="status" data-plan-limit>Você chegou a um limite do plano. Para crescer, fale conosco e mude de plano.</p>
        }
      </section>
    }
  `,
})
export class PlanCard implements OnInit {
  private readonly http = inject(HttpClient);

  protected readonly plan = signal<PlanView | null>(null);

  async ngOnInit(): Promise<void> {
    try {
      this.plan.set(await firstValueFrom(this.http.get<PlanView>('/api/painel/plano')));
    } catch {
      // O cartão é só informativo: sem ele, o Início segue normal.
    }
  }

  protected usageLabel(key: string): string {
    return USAGE_LABELS[key] ?? key;
  }

  protected featureList(features: string[]): string {
    return features.length ? features.map((f) => FEATURE_LABELS[f] ?? f).join(', ') : 'nenhum recurso extra';
  }

  protected format(value: number): string {
    return value.toLocaleString('pt-BR');
  }

  protected atLimit(p: PlanView): boolean {
    return p.usage.some((u) => u.limit !== null && u.key !== 'ai_listings_month' && u.used >= u.limit);
  }
}
