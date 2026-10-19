import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/** Traços (viewBox 24x24, só contorno) dos ícones do painel. Desenho próprio, sem biblioteca. */
const PATHS = {
  home: 'M3 11.5 12 4l9 7.5M5.5 9.5V20h5v-6h3v6h5V9.5',
  catalog: 'M3.5 7.5 12 3l8.5 4.5v9L12 21l-8.5-4.5v-9ZM3.5 7.5 12 12l8.5-4.5M12 12v9',
  sales: 'M3 4h2.5l2.2 10.5h10.6L20.5 7H7M9.5 19.5a1 1 0 1 0 0 .01M17 19.5a1 1 0 1 0 0 .01',
  channels: 'M8 12h8M6.5 16H6a4 4 0 0 1 0-8h.5M17.5 8h.5a4 4 0 0 1 0 8h-.5',
  settings:
    'M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6ZM19.4 13.5a7.6 7.6 0 0 0 0-3l2-1.5-2-3.4-2.3 1a7.4 7.4 0 0 0-2.6-1.5L14 2.5h-4l-.5 2.6a7.4 7.4 0 0 0-2.6 1.5l-2.3-1-2 3.4 2 1.5a7.6 7.6 0 0 0 0 3l-2 1.5 2 3.4 2.3-1a7.4 7.4 0 0 0 2.6 1.5l.5 2.6h4l.5-2.6a7.4 7.4 0 0 0 2.6-1.5l2.3 1 2-3.4-2-1.5Z',
  stores: 'M4 9.5 5.5 4h13L20 9.5M4 9.5h16M4 9.5a2.7 2.7 0 0 0 5.3 0 2.7 2.7 0 0 0 5.4 0 2.7 2.7 0 0 0 5.3 0M5.5 12v8h13v-8M10 20v-4.5h4V20',
  manage: 'M4 6h16M4 12h16M4 18h16M8 4v4M16 10v4M11 16v4',
  chevron: 'm9 6 6 6-6 6',
  menu: 'M4 6h16M4 12h16M4 18h16',
  close: 'm6 6 12 12M18 6 6 18',
} as const;

export type IconName = keyof typeof PATHS;

@Component({
  selector: 'app-icon',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">
      <path [attr.d]="path()" />
    </svg>
  `,
  styles: ':host { display: inline-flex; flex: none; }',
})
export class Icon {
  readonly name = input.required<IconName>();
  protected readonly path = computed(() => PATHS[this.name()]);
}
