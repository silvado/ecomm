import { Component, computed, DestroyRef, ElementRef, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationStart, Router } from '@angular/router';
import { filter } from 'rxjs';

/** Iniciais do e-mail para o botão ("maria.souza@x" → "MS"). */
export function initials(email: string | null | undefined): string {
  const name = (email ?? '').split('@', 1)[0];
  const parts = name.split(/[._\-+]+/).filter((p) => p.length > 0);
  const letters = parts.length > 1 ? parts[0][0] + parts[1][0] : name.slice(0, 2);
  return letters.toUpperCase() || '?';
}

/**
 * Área do usuário no cabeçalho: botão com as iniciais e o e-mail que abre as ações da conta (projetadas pelo pai,
 * cada uma com a classe `menu-item`). Fecha com Esc, clique fora ou ao navegar.
 */
@Component({
  selector: 'app-user-menu',
  host: {
    '(document:click)': 'onDocumentClick($event)',
    '(document:keydown.escape)': 'close(true)',
  },
  template: `
    <div class="user-menu">
      <button #trigger type="button" class="user-trigger" aria-haspopup="menu" [attr.aria-expanded]="open()" aria-controls="user-menu-panel"
        (click)="toggle()" data-user-menu>
        <span class="avatar" aria-hidden="true">{{ avatar() }}</span>
        <span class="who">
          <span class="email">{{ email() }}</span>
          <small>{{ subtitle() }}</small>
        </span>
      </button>
      @if (open()) {
        <div id="user-menu-panel" class="user-panel" role="menu" (click)="onPanelClick($event)">
          <div class="user-head">
            <strong>{{ email() }}</strong>
            <small>{{ subtitle() }}</small>
          </div>
          <ng-content />
        </div>
      }
    </div>
  `,
})
export class UserMenu {
  readonly email = input.required<string | null | undefined>();
  readonly subtitle = input('');

  private readonly host: ElementRef<HTMLElement> = inject(ElementRef);
  protected readonly open = signal(false);
  protected readonly avatar = computed(() => initials(this.email()));

  constructor() {
    inject(Router)
      .events.pipe(filter((e) => e instanceof NavigationStart), takeUntilDestroyed(inject(DestroyRef)))
      .subscribe(() => this.open.set(false));
  }

  protected toggle(): void {
    this.open.update((o) => !o);
    // Abriu pelo teclado ou mouse: o foco vai para a primeira ação.
    if (this.open()) setTimeout(() => {
      if (this.open()) this.host.nativeElement.querySelector<HTMLElement>('.user-panel .menu-item')?.focus();
    });
  }

  close(returnFocus = false): void {
    if (!this.open()) return;
    this.open.set(false);
    if (returnFocus) this.host.nativeElement.querySelector<HTMLElement>('.user-trigger')?.focus();
  }

  protected onDocumentClick(event: MouseEvent): void {
    if (!this.host.nativeElement.contains(event.target as Node)) this.close();
  }

  /** Escolher uma ação (link ou botão) fecha o menu. */
  protected onPanelClick(event: MouseEvent): void {
    if ((event.target as HTMLElement).closest('.menu-item')) this.close();
  }
}
