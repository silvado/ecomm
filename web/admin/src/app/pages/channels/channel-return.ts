import { Component, inject, OnInit, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { problemTitle } from '../../core/auth';
import { ChannelsApi } from './channels.api';

/**
 * Volta da página de autorização do Mercado Livre (endereço de retorno cadastrado no app). Repassa código e state à API
 * e mostra o resultado. O código só vale uma vez: recarregar esta página mostra "já foi usada".
 */
@Component({
  selector: 'app-channel-return',
  imports: [RouterLink],
  template: `
    <main class="card narrow">
      <h1>Mercado Livre</h1>
      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
        <p><a routerLink="/canais">Voltar para Canais de venda</a></p>
      } @else {
        <p role="status">Concluindo a conexão…</p>
      }
    </main>
  `,
})
export class ChannelReturnPage implements OnInit {
  private readonly api = inject(ChannelsApi);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  protected readonly error = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    const query = this.route.snapshot.queryParamMap;
    try {
      await this.api.completeMercadoLivre(query.get('code'), query.get('state'), query.get('error'));
      // Sem o código na barra de endereço (nem no histórico) depois de usado.
      await this.router.navigate(['/canais'], { replaceUrl: true });
    } catch (error) {
      this.error.set(problemTitle(error));
    }
  }
}
