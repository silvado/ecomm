import { HttpContext, HttpContextToken, HttpErrorResponse, HttpInterceptorFn, HttpResponse } from '@angular/common/http';
import { Component, computed, inject, Injectable, signal } from '@angular/core';
import { finalize, tap } from 'rxjs';
import { problemTitle } from './auth';

/**
 * Retorno de uma ação que grava (POST/PUT/PATCH/DELETE). `success`: texto do aviso de sucesso (nulo = sem aviso, quando a
 * própria tela mostra o resultado ou a ação leva a outra página). `overlay`: falso para ações com progresso próprio.
 */
export interface FeedbackOptions {
  success?: string | null;
  overlay?: boolean;
}

/** Falso = ação silenciosa (sem overlay nem aviso, nem de erro). */
export const FEEDBACK = new HttpContextToken<FeedbackOptions | false>(() => ({}));

/** Contexto da chamada com o retorno escolhido: `{ context: feedback('Peça salva.') }`. */
export function feedback(success: string | null, options: Omit<FeedbackOptions, 'success'> = {}): HttpContext {
  return new HttpContext().set(FEEDBACK, { ...options, success });
}

/** Contexto de ação silenciosa (prévias, rotinas de fundo). */
export const silent = (): HttpContext => new HttpContext().set(FEEDBACK, false);

export interface Toast {
  id: number;
  kind: 'success' | 'error';
  text: string;
}

/** Só aparece se a ação passar disso: as rápidas não piscam a tela. */
export const OVERLAY_DELAY_MS = 300;
const SUCCESS_MS = 4000;
const ERROR_MS = 8000;

/** Overlay enquanto há ação gravando e os avisos (toasts) do painel. */
@Injectable({ providedIn: 'root' })
export class FeedbackService {
  private readonly pending = signal(0);
  private readonly delayed = signal(false);
  private timer: ReturnType<typeof setTimeout> | null = null;
  private nextId = 1;

  readonly toasts = signal<Toast[]>([]);
  readonly busy = computed(() => this.pending() > 0 && this.delayed());

  begin(): void {
    this.pending.update((n) => n + 1);
    this.timer ??= setTimeout(() => this.delayed.set(true), OVERLAY_DELAY_MS);
  }

  end(): void {
    this.pending.update((n) => Math.max(0, n - 1));
    if (this.pending() > 0) return;
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
    this.delayed.set(false);
  }

  success(text: string): void {
    this.push('success', text, SUCCESS_MS);
  }

  error(text: string): void {
    this.push('error', text, ERROR_MS);
  }

  dismiss(id: number): void {
    this.toasts.update((list) => list.filter((t) => t.id !== id));
  }

  private push(kind: Toast['kind'], text: string, ms: number): void {
    const id = this.nextId++;
    // Até 4 na tela: o mais antigo sai.
    this.toasts.update((list) => [...list.slice(-3), { id, kind, text }]);
    setTimeout(() => this.dismiss(id), ms);
  }
}

const WRITES = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);

/** Sessão (login, renovação, troca de senha, MFA): a própria tela de entrada mostra o resultado. */
const isSession = (url: string) => url.startsWith('/api/auth/') || url.startsWith('/api/plataforma/auth');

const defaultSuccess = (method: string) => (method === 'DELETE' ? 'Removido.' : method === 'POST' ? 'Feito.' : 'Alterações salvas.');

export function errorText(error: unknown): string {
  if (error instanceof HttpErrorResponse && error.status === 0) return 'Sem conexão com o servidor. Confira a internet e tente de novo.';
  return problemTitle(error);
}

/**
 * Toda ação que grava ganha overlay (depois de {@link OVERLAY_DELAY_MS}) e um aviso de sucesso ou de erro (com a
 * mensagem da API). Fica por fora dos interceptores de sessão: vê o resultado depois de uma renovação de token.
 */
export const feedbackInterceptor: HttpInterceptorFn = (req, next) => {
  const options = req.context.get(FEEDBACK);
  if (!WRITES.has(req.method) || !req.url.startsWith('/api/') || isSession(req.url) || options === false) return next(req);

  const service = inject(FeedbackService);
  const overlay = options.overlay !== false;
  if (overlay) service.begin();
  return next(req).pipe(
    tap({
      next: (event) => {
        if (!(event instanceof HttpResponse)) return;
        const text = options.success === undefined ? defaultSuccess(req.method) : options.success;
        if (text) service.success(text);
      },
      error: (error: unknown) => service.error(errorText(error)),
    }),
    finalize(() => {
      if (overlay) service.end();
    }),
  );
};

/** Overlay e avisos, uma vez na raiz do app. */
@Component({
  selector: 'app-feedback',
  template: `
    @if (feedback.busy()) {
      <div class="overlay" aria-busy="true" data-overlay>
        <div class="spinner" role="status"><span class="sr-only">Processando…</span></div>
      </div>
    }
    <div class="toasts" aria-live="polite">
      @for (t of feedback.toasts(); track t.id) {
        <div class="toast" [class.error]="t.kind === 'error'" [attr.role]="t.kind === 'error' ? 'alert' : 'status'" [attr.data-toast]="t.kind">
          <span class="toast-icon" aria-hidden="true">{{ t.kind === 'error' ? '!' : '✓' }}</span>
          <span class="toast-text">{{ t.text }}</span>
          <button type="button" class="toast-close" (click)="feedback.dismiss(t.id)" aria-label="Fechar aviso">×</button>
        </div>
      }
    </div>
  `,
})
export class FeedbackHost {
  protected readonly feedback = inject(FeedbackService);
}
