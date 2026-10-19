import { DatePipe } from '@angular/common';
import { Component, effect, inject, input, OnInit, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { problemTitle } from '../core/auth';
import { FeedbackService } from '../core/feedback';
import { AUDIT_LABELS, AuditItem, FEATURE_LABELS, LIMIT_LABELS, PlatformApi, PlatformPlan } from './platform.api';

/**
 * Planos e o que cada um libera. Limites e recursos mudam por migração (ADR-0007); a mensalidade (RF38) a equipe muda
 * aqui e vale para as faturas geradas depois.
 */
@Component({
  selector: 'app-platform-plans',
  imports: [FormsModule],
  template: `
    <main class="card wide">
      <h1>Planos</h1>
      <p class="muted">Os limites e recursos de cada plano mudam só por atualização do sistema. A mensalidade vale para as faturas geradas depois da mudança; zero = sem cobrança.</p>
      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
      <table>
        <thead><tr><th>Plano</th><th>Mensalidade (R$)</th><th>Limites</th><th>Recursos</th><th class="num">Lojas</th></tr></thead>
        <tbody>
          @for (p of plans(); track p.code) {
            <tr>
              <td><strong>{{ p.name }}</strong></td>
              <td [attr.data-price]="p.code">
                <input type="number" min="0" step="0.01" [(ngModel)]="prices[p.code]" [attr.aria-label]="'Mensalidade do plano ' + p.name" />
                <button type="button" (click)="savePrice(p)" [disabled]="busy() || prices[p.code] === p.monthlyPrice">Salvar</button>
              </td>
              <td>
                @for (key of limitKeys; track key) {
                  <div>{{ limitLabels[key] }}: {{ p.limits[key] ?? 'sem limite' }}</div>
                }
              </td>
              <td>
                @for (f of p.features; track f) {
                  <div>{{ featureLabels[f] ?? f }}</div>
                }
              </td>
              <td class="num">{{ p.tenants }}</td>
            </tr>
          }
        </tbody>
      </table>
    </main>
  `,
})
export class PlatformPlansPage implements OnInit {
  private readonly api = inject(PlatformApi);

  protected readonly limitKeys = Object.keys(LIMIT_LABELS);
  protected readonly limitLabels = LIMIT_LABELS;
  protected readonly featureLabels = FEATURE_LABELS;
  protected readonly plans = signal<PlatformPlan[]>([]);
  protected readonly error = signal<string | null>(null);
  private readonly toast = inject(FeedbackService);
  protected readonly busy = signal(false);
  protected prices: Record<string, number> = {};

  async ngOnInit(): Promise<void> {
    try {
      this.show(await this.api.plans());
    } catch (error) {
      this.error.set(problemTitle(error));
    }
  }

  protected async savePrice(plan: PlatformPlan): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      this.show(await this.api.setPlanPrice(plan.code, Number(this.prices[plan.code] ?? 0)));
      this.toast.success(`Mensalidade do plano ${plan.name} salva.`);
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }

  private show(plans: PlatformPlan[]): void {
    this.plans.set(plans);
    this.prices = Object.fromEntries(plans.map((p) => [p.code, p.monthlyPrice]));
  }
}

/** Tudo o que os superadmins fizeram (RF07 CA3): logins, cadastros de MFA, aberturas de loja e ações. Só leitura. */
@Component({
  selector: 'app-platform-audit',
  imports: [DatePipe, FormsModule, RouterLink],
  template: `
    <main class="card wide">
      <h1>Auditoria</h1>
      <div class="filters">
        <select [(ngModel)]="action" (change)="load(1)" aria-label="Ação">
          <option value="">Todas as ações</option>
          @for (a of actions; track a) {
            <option [value]="a">{{ labels[a] }}</option>
          }
        </select>
        <input type="search" placeholder="E-mail do superadmin" [(ngModel)]="email" (keyup.enter)="load(1)" aria-label="E-mail" />
        <button type="button" (click)="load(1)">Filtrar</button>
        @if (loja()) {
          <a routerLink="/plataforma/auditoria">Ver todas as lojas</a>
        }
      </div>
      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
      <table>
        <thead><tr><th>Quando</th><th>Quem</th><th>Ação</th><th>Loja</th><th>Detalhes</th><th>IP</th></tr></thead>
        <tbody>
          @for (i of items(); track $index) {
            <tr>
              <td>{{ i.at | date: 'dd/MM/yyyy HH:mm:ss' }}</td>
              <td>{{ i.adminEmail }}</td>
              <td>{{ labels[i.action] ?? i.action }}</td>
              <td>
                @if (i.tenantId) {
                  <a [routerLink]="['/plataforma/lojas', i.tenantId]">{{ i.tenantSlug ?? 'loja' }}</a>
                }
              </td>
              <td>{{ i.details }}</td>
              <td class="muted">{{ i.ip }}</td>
            </tr>
          } @empty {
            <tr><td colspan="6" class="muted">Nada registrado com estes filtros.</td></tr>
          }
        </tbody>
      </table>
      <div class="pager">
        <button type="button" (click)="load(page() - 1)" [disabled]="page() === 1">Anterior</button>
        <span>Página {{ page() }}</span>
        <button type="button" (click)="load(page() + 1)" [disabled]="!hasMore()">Próxima</button>
      </div>
    </main>
  `,
})
export class PlatformAuditPage {
  private readonly api = inject(PlatformApi);

  /** Filtro por loja vindo da tela da loja (?loja=). */
  readonly loja = input<string>();
  protected readonly labels = AUDIT_LABELS;
  protected readonly actions = Object.keys(AUDIT_LABELS);
  protected readonly items = signal<AuditItem[]>([]);
  protected readonly page = signal(1);
  protected readonly hasMore = signal(false);
  protected readonly error = signal<string | null>(null);
  protected action = '';
  protected email = '';

  constructor() {
    // Recarrega quando o filtro por loja muda (vindo da tela da loja ou de "Ver todas as lojas").
    effect(() => {
      this.loja();
      untracked(() => void this.load(1));
    });
  }

  async load(page: number): Promise<void> {
    this.error.set(null);
    try {
      const result = await this.api.audit({ loja: this.loja(), acao: this.action, email: this.email.trim(), pagina: page });
      this.items.set(result.items);
      this.page.set(result.page);
      this.hasMore.set(result.hasMore);
    } catch (error) {
      this.error.set(problemTitle(error));
    }
  }
}
