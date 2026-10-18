import { HttpClient } from '@angular/common/http';
import { Component, computed, inject, Injectable, OnInit, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { problemTitle } from '../../core/auth';

export type OnboardingStep = 'company' | 'domain' | 'payment' | 'fiscal' | 'shipping' | 'mercadoLivre' | 'instagram' | 'olx';

export interface OnboardingStepState {
  step: OnboardingStep;
  required: boolean;
  done: boolean;
  skipped: boolean;
  detail: string | null;
}

export interface OnboardingView {
  published: boolean;
  steps: OnboardingStepState[];
  canPublish: boolean;
}

/** Onde cada passo se resolve no painel. Instagram e OLX ainda sem tela de conexão (E2/E3). */
export const STEP_INFO: Record<OnboardingStep, { title: string; hint: string; link: string | null }> = {
  company: { title: 'Dados da loja', hint: 'Revise nome, cores, logo e textos e clique em Salvar.', link: '/loja' },
  domain: { title: 'Domínio próprio', hint: 'Em Dados da loja → Domínio próprio. Sem ele, a loja usa o endereço da plataforma.', link: '/loja' },
  payment: { title: 'Pagamento', hint: 'Conecte o Mercado Pago ou a Cielo para receber dos compradores.', link: '/pagamentos' },
  fiscal: { title: 'Nota fiscal', hint: 'Em Dados da loja → Nota fiscal: escolha como a loja emite (pode ser "não emite pela plataforma").', link: '/loja' },
  shipping: { title: 'Frete', hint: 'Em Dados da loja → Entrega e retirada: CEP de origem ou retirada no balcão.', link: '/loja' },
  mercadoLivre: { title: 'Mercado Livre', hint: 'Em Canais de venda: conecte a conta principal da loja no Mercado Livre.', link: '/canais' },
  instagram: { title: 'Instagram', hint: 'Em Canais de venda: conecte o catálogo da Meta e cadastre o endereço do feed.', link: '/canais' },
  olx: { title: 'OLX', hint: 'A conexão chega numa próxima versão.', link: null },
};

@Injectable({ providedIn: 'root' })
export class OnboardingApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/painel/primeiros-passos';

  get(): Promise<OnboardingView> {
    return firstValueFrom(this.http.get<OnboardingView>(this.base));
  }

  skip(step: OnboardingStep, skipped: boolean): Promise<OnboardingView> {
    const url = `${this.base}/${step}/pular`;
    return firstValueFrom(skipped ? this.http.put<OnboardingView>(url, null) : this.http.delete<OnboardingView>(url));
  }

  publish(): Promise<OnboardingView> {
    return firstValueFrom(this.http.post<OnboardingView>(`${this.base}/publicar`, null));
  }
}

/**
 * Primeiros passos (RF05): a situação vem das próprias telas de configuração (salvar lá é salvar o passo). A loja virtual
 * fica "em breve" até o Dono publicar, o que exige dados da loja, pagamento e frete.
 */
@Component({
  selector: 'app-onboarding',
  imports: [RouterLink],
  template: `
    <main class="card wide">
      <h1>Primeiros passos</h1>
      @if (view(); as v) {
        @if (v.published) {
          <p class="ok" role="status" data-published>A loja virtual está publicada e aberta aos compradores.</p>
        } @else {
          <p class="muted">A loja virtual mostra <strong>"Loja em breve"</strong> até você publicar. Para publicar, conclua os passos obrigatórios.</p>
        }
        <p class="muted" data-progress>{{ doneCount() }} de {{ v.steps.length }} passos concluídos.</p>

        <ol class="onboarding-steps">
          @for (s of v.steps; track s.step) {
            <li [class.done]="s.done" [attr.data-step]="s.step">
              <div class="header-row">
                <strong>{{ info[s.step].title }}</strong>
                <span class="badge">{{ s.done ? 'Concluído' : s.skipped ? 'Pulado' : s.required ? 'Obrigatório' : 'Opcional' }}</span>
              </div>
              <p class="muted">{{ s.detail ?? info[s.step].hint }}</p>
              <div class="order-actions">
                @if (!s.done && info[s.step].link) {
                  <a [routerLink]="info[s.step].link">Configurar</a>
                }
                @if (!s.required && !s.done) {
                  <button type="button" class="link" (click)="skip(s.step, !s.skipped)" [disabled]="busy()" [attr.data-skip]="s.step">
                    {{ s.skipped ? 'Mostrar de novo' : 'Pular' }}
                  </button>
                }
              </div>
            </li>
          }
        </ol>

        @if (!v.published) {
          <div class="order-actions">
            <button type="button" (click)="publish()" [disabled]="busy() || !v.canPublish" data-publish>Publicar a loja</button>
          </div>
          @if (!v.canPublish) {
            <p class="muted">Faltam: {{ missing() }}.</p>
          }
        }
      }
      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
    </main>
  `,
})
export class OnboardingPage implements OnInit {
  private readonly api = inject(OnboardingApi);

  protected readonly info = STEP_INFO;
  protected readonly view = signal<OnboardingView | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly doneCount = computed(() => this.view()?.steps.filter((s) => s.done).length ?? 0);
  protected readonly missing = computed(() =>
    (this.view()?.steps ?? []).filter((s) => s.required && !s.done).map((s) => STEP_INFO[s.step].title).join(', '),
  );

  async ngOnInit(): Promise<void> {
    await this.run(() => this.api.get());
  }

  protected skip = (step: OnboardingStep, skipped: boolean) => this.run(() => this.api.skip(step, skipped));

  protected publish = () => this.run(() => this.api.publish());

  private async run(action: () => Promise<OnboardingView>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      this.view.set(await action());
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }
}

/** Cartão no Início enquanto a loja não foi publicada (só para o Dono). */
@Component({
  selector: 'app-onboarding-card',
  imports: [RouterLink],
  template: `
    @if (view(); as v) {
      @if (!v.published) {
        <section class="card" data-onboarding-card>
          <h2>Sua loja ainda não está no ar</h2>
          <p>{{ done() }} de {{ v.steps.length }} passos concluídos. A loja virtual mostra "Loja em breve" até você publicar.</p>
          <a routerLink="/primeiros-passos">Continuar os primeiros passos</a>
        </section>
      }
    }
  `,
})
export class OnboardingCard implements OnInit {
  private readonly api = inject(OnboardingApi);

  protected readonly view = signal<OnboardingView | null>(null);
  protected readonly done = computed(() => this.view()?.steps.filter((s) => s.done).length ?? 0);

  async ngOnInit(): Promise<void> {
    try {
      this.view.set(await this.api.get());
    } catch {
      // Sem o cartão: o Início continua funcionando.
    }
  }
}
