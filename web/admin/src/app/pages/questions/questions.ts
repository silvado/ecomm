import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AuthStore, problemTitle } from '../../core/auth';
import { ANSWER_ORIGIN_LABELS, BuyerQuestion, QuestionsApi } from './questions.api';

const WHEN = new Intl.DateTimeFormat('pt-BR', { timeZone: 'America/Sao_Paulo', dateStyle: 'short', timeStyle: 'short' });

/**
 * Perguntas de compradores (RF31): a IA deixa um rascunho só com o cadastro da peça; a loja revisa, edita e envia. O Dono
 * pode ligar a resposta automática das perguntas simples (desligada por padrão); compatibilidade sempre passa pela loja.
 */
@Component({
  selector: 'app-questions',
  imports: [FormsModule, RouterLink],
  template: `
    <main class="card">
      <h1>Perguntas</h1>
      <p class="muted">
        Perguntas dos compradores nos seus anúncios do Mercado Livre. A IA sugere uma resposta usando só o cadastro da peça; nada
        é enviado sem você, a não ser que a resposta automática esteja ligada.
      </p>

      @if (auth.can('storeManage')) {
        <label class="check">
          <input type="checkbox" [checked]="autoAnswer()" (change)="toggleAuto($event)" [disabled]="busy()" data-auto />
          Responder sozinho as perguntas simples (disponibilidade, estado, códigos, retirada). Perguntas sobre compatibilidade,
          preço ou que faltam dados sempre esperam a sua revisão.
        </label>
      } @else if (autoAnswer()) {
        <p class="muted">A resposta automática das perguntas simples está ligada.</p>
      }

      <div class="order-actions">
        <button type="button" class="link" [class.active]="!all()" (click)="show(false)">Sem resposta</button>
        <button type="button" class="link" [class.active]="all()" (click)="show(true)" data-all>Todas</button>
      </div>

      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }

      @for (q of questions(); track q.id) {
        <section class="channel" [attr.data-question]="q.id">
          <p>
            <strong>“{{ q.text }}”</strong>
            <br />
            <span class="muted">
              {{ when(q.askedAt) }} ·
              @if (q.partId) {
                <a [routerLink]="['/pecas', q.partId]">{{ q.partTitle ?? 'Peça' }}</a>
              } @else {
                anúncio {{ q.externalItemId }}
              }
            </span>
          </p>

          @if (q.status === 'pending') {
            @if (q.kind) {
              <span class="badge" [class.ok]="q.kind === 'simple'" [class.warn]="q.kind === 'complex'">{{ q.kind === 'simple' ? 'Simples' : 'Revisar' }}</span>
            }
            @if (q.draftNote) {
              <p class="warn" data-note>{{ q.draftNote }}</p>
            }
            @if (q.lastError) {
              <p class="error" role="alert">{{ q.lastError }}</p>
            }
            <label>
              Resposta
              <textarea rows="4" maxlength="2000" [(ngModel)]="drafts[q.id]" [name]="'answer-' + q.id" data-answer></textarea>
            </label>
            <small>Revise antes de enviar: não informe telefone, e-mail ou site (o Mercado Livre não permite).</small>
            <div class="order-actions">
              <button type="button" (click)="send(q)" [disabled]="busy() || !(drafts[q.id] ?? '').trim()" data-send>Enviar resposta</button>
              @if (q.partId) {
                <button type="button" class="link" (click)="redraft(q)" [disabled]="busy()" data-redraft>Novo rascunho da IA</button>
              }
            </div>
          } @else if (q.status === 'answered') {
            <p data-answered>
              <span class="badge ok">Respondida {{ originLabels[q.answeredVia ?? 'panel'] }}</span>
              {{ q.answerText }}
              @if (q.answeredAt) {
                <span class="muted">· {{ when(q.answeredAt) }}</span>
              }
            </p>
          } @else {
            <p class="muted"><span class="badge">Encerrada</span> Excluída ou encerrada no Mercado Livre.</p>
          }
        </section>
      } @empty {
        @if (loaded()) {
          <p>{{ all() ? 'Nenhuma pergunta recebida ainda.' : 'Nenhuma pergunta esperando resposta.' }}</p>
        } @else {
          <p class="muted">Carregando…</p>
        }
      }
    </main>
  `,
})
export class QuestionsPage implements OnInit {
  private readonly api = inject(QuestionsApi);
  protected readonly auth = inject(AuthStore);

  protected readonly questions = signal<BuyerQuestion[]>([]);
  protected readonly all = signal(false);
  protected readonly loaded = signal(false);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly autoAnswer = signal(false);
  protected readonly originLabels = ANSWER_ORIGIN_LABELS;
  protected readonly when = (iso: string) => WHEN.format(new Date(iso));
  /** Texto em edição por pergunta (começa com o rascunho da IA). */
  protected drafts: Record<string, string> = {};

  async ngOnInit(): Promise<void> {
    try {
      this.autoAnswer.set(await this.api.settings());
    } catch {
      // Só a opção: a lista segue.
    }
    await this.load();
  }

  async show(all: boolean): Promise<void> {
    this.all.set(all);
    await this.load();
  }

  async toggleAuto(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const enabled = input.checked;
    if (enabled && !confirm('Ligar a resposta automática? As perguntas simples passam a ser respondidas pela IA sem revisão.')) {
      input.checked = false;
      return;
    }
    await this.run(async () => this.autoAnswer.set(await this.api.setAutoAnswer(enabled)));
    input.checked = this.autoAnswer();
  }

  async send(q: BuyerQuestion): Promise<void> {
    await this.run(async () => {
      const updated = await this.api.answer(q.id, (this.drafts[q.id] ?? '').trim());
      this.replace(updated);
    });
  }

  async redraft(q: BuyerQuestion): Promise<void> {
    await this.run(async () => {
      const updated = await this.api.redraft(q.id);
      this.drafts[q.id] = updated.draftText ?? '';
      this.replace(updated);
    });
  }

  private replace(updated: BuyerQuestion): void {
    // Respondida sai da lista de pendentes; em "Todas", fica com a resposta.
    this.questions.update((list) =>
      list.flatMap((q) => (q.id !== updated.id ? [q] : !this.all() && updated.status !== 'pending' ? [] : [updated])),
    );
  }

  private async run(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await action();
    } catch (error) {
      this.error.set(problemTitle(error));
      // A pergunta pode ter mudado (respondida no ML, erro gravado): recarrega.
      await this.load();
    } finally {
      this.busy.set(false);
    }
  }

  private async load(): Promise<void> {
    try {
      const list = await this.api.list(this.all());
      for (const q of list) if (this.drafts[q.id] === undefined) this.drafts[q.id] = q.draftText ?? '';
      this.questions.set(list);
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.loaded.set(true);
    }
  }
}
