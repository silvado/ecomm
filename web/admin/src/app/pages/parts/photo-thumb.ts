import { HttpClient } from '@angular/common/http';
import { Component, effect, inject, input, OnDestroy, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

/**
 * Miniatura de foto do painel. A rota exige o token (cabeçalho Authorization), que um &lt;img src&gt; não envia:
 * busca o arquivo pelo HttpClient (com o interceptor) e exibe como URL local do navegador.
 */
@Component({
  selector: 'app-photo-thumb',
  template: `
    @if (url(); as src) {
      <img [src]="src" [alt]="alt()" [width]="size()" [height]="size()" loading="lazy" />
    } @else {
      <span class="thumb-placeholder" [style.width.px]="size()" [style.height.px]="size()"></span>
    }
  `,
  styles: `
    :host { display: inline-block; line-height: 0; }
    img { object-fit: cover; border-radius: 4px; background: #eee; }
    .thumb-placeholder { display: inline-block; border-radius: 4px; background: #eee; }
  `,
})
export class PhotoThumb implements OnDestroy {
  readonly partId = input.required<string>();
  readonly photoId = input.required<string>();
  readonly size = input(64);
  readonly alt = input('');

  private readonly http = inject(HttpClient);
  protected readonly url = signal<string | null>(null);

  constructor() {
    effect(() => void this.load(this.partId(), this.photoId()));
  }

  ngOnDestroy(): void {
    this.revoke();
  }

  private async load(partId: string, photoId: string): Promise<void> {
    try {
      const blob = await firstValueFrom(this.http.get(`/api/painel/pecas/${partId}/fotos/${photoId}/300`, { responseType: 'blob' }));
      this.revoke();
      this.url.set(URL.createObjectURL(blob));
    } catch {
      this.revoke();
    }
  }

  private revoke(): void {
    const current = this.url();
    if (current) URL.revokeObjectURL(current);
    this.url.set(null);
  }
}
