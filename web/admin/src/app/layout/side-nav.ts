import { Component, computed, DestroyRef, effect, inject, input, output, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import { filter } from 'rxjs';
import { Icon, IconName } from './icon';

export interface NavItem {
  label: string;
  link: string;
  /** Quando o item fica marcado; sem ele, a própria rota e as de baixo (ex.: /pecas/123 marca "Peças"). */
  active?: (url: string) => boolean;
}

/** Grupo do menu. Com um item só, aparece como link direto, sem submenu. */
export interface NavGroup {
  id: string;
  label: string;
  icon: IconName;
  items: NavItem[];
}

const path = (url: string) => url.split(/[?#]/, 1)[0];

export const isActive = (item: NavItem, url: string): boolean => {
  const current = path(url);
  if (item.active) return item.active(current);
  return item.link === '/' ? current === '/' : current === item.link || current.startsWith(item.link + '/');
};

/**
 * Menu lateral em grupos que abrem e fecham. O grupo da página atual abre sozinho; os grupos sem itens (o perfil não
 * pode usar nada ali) nem aparecem. No celular é uma gaveta: `open` controla e `closed` avisa ao escolher um item.
 */
@Component({
  selector: 'app-side-nav',
  imports: [RouterLink, Icon],
  template: `
    <nav class="side-nav" [class.open]="open()" aria-label="Menu principal">
      <ul>
        @for (group of visible(); track group.id) {
          <li [attr.data-group]="group.id">
            @if (group.items.length === 1) {
              <a class="entry" [routerLink]="group.items[0].link" [class.active]="activeGroup() === group.id"
                [attr.aria-current]="activeGroup() === group.id ? 'page' : null" (click)="closed.emit()">
                <app-icon [name]="group.icon" /> <span>{{ group.items[0].label }}</span>
              </a>
            } @else {
              <button type="button" class="entry" [class.current]="activeGroup() === group.id" [attr.aria-expanded]="expanded().has(group.id)"
                [attr.aria-controls]="'nav-' + group.id" (click)="toggle(group.id)">
                <app-icon [name]="group.icon" /> <span>{{ group.label }}</span>
                <app-icon name="chevron" class="chevron" />
              </button>
              @if (expanded().has(group.id)) {
                <ul [id]="'nav-' + group.id" class="sub">
                  @for (item of group.items; track item.link) {
                    <li>
                      <a [routerLink]="item.link" [class.active]="isActive(item, url())" [attr.aria-current]="isActive(item, url()) ? 'page' : null"
                        (click)="closed.emit()">{{ item.label }}</a>
                    </li>
                  }
                </ul>
              }
            }
          </li>
        }
      </ul>
    </nav>
  `,
})
export class SideNav {
  readonly groups = input.required<NavGroup[]>();
  /** Gaveta aberta (celular). */
  readonly open = input(false);
  readonly closed = output<void>();

  private readonly router = inject(Router);
  protected readonly url = signal(this.router.url);
  protected readonly expanded = signal(new Set<string>());
  protected readonly isActive = isActive;

  protected readonly visible = computed(() => this.groups().filter((g) => g.items.length > 0));
  protected readonly activeGroup = computed(() => this.visible().find((g) => g.items.some((i) => isActive(i, this.url())))?.id ?? null);

  constructor() {
    this.router.events
      .pipe(filter((e) => e instanceof NavigationEnd), takeUntilDestroyed(inject(DestroyRef)))
      .subscribe((e) => this.url.set(e.urlAfterRedirects));
    // Ao chegar a uma página, o grupo dela abre (os outros ficam como a pessoa deixou).
    effect(() => {
      const id = this.activeGroup();
      // untracked: fechar o grupo atual à mão não o reabre.
      if (id && !untracked(this.expanded).has(id)) this.expanded.update((s) => new Set(s).add(id));
    });
  }

  protected toggle(id: string): void {
    this.expanded.update((s) => {
      const next = new Set(s);
      if (!next.delete(id)) next.add(id);
      return next;
    });
  }
}
