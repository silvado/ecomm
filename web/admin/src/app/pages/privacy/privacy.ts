import { DatePipe } from '@angular/common';
import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { problemTitle } from '../../core/auth';
import { ACCESS_LABELS, AccessItem, AnonymizationPreview, DataSubject, PrivacyApi } from './privacy.api';

/**
 * Privacidade (RNF04), só para o Dono: atender o pedido de um comprador para apagar os dados dele e ver quem acessou
 * dados pessoais pelo painel.
 */
@Component({
  selector: 'app-privacy',
  imports: [DatePipe, FormsModule, RouterLink],
  template: `
    <main class="card wide">
      <h1>Privacidade</h1>

      <section data-anonymize>
        <h2>Apagar dados de um comprador</h2>
        <p class="muted">
          Quando um comprador pedir para apagar os dados dele (LGPD), procure pelo e-mail ou pelo CPF/CNPJ que ele usou nos pedidos.
          Saem e-mail, telefone e endereço de entrega. Nos pedidos <strong>com nota fiscal</strong>, o nome e o CPF/CNPJ ficam, porque a
          lei fiscal obriga a guardar por 5 anos; nos outros, saem também. Pedido, peças e valores continuam. Não dá para desfazer.
        </p>
        <div class="filters">
          <input type="email" [(ngModel)]="email" placeholder="E-mail" aria-label="E-mail do comprador" data-email />
          <input [(ngModel)]="document" placeholder="ou CPF/CNPJ" aria-label="CPF ou CNPJ do comprador" data-document />
          <button type="button" (click)="search()" [disabled]="busy()" data-search>Procurar pedidos</button>
        </div>

        @if (preview(); as p) {
          <div class="notice" data-preview>
            @if (p.orders === 0) {
              <p>Nenhum pedido com este e-mail ou documento.</p>
            } @else {
              <p>
                <strong>{{ p.orders }}</strong> pedido(s) encontrado(s){{ p.withInvoice ? ', ' + p.withInvoice + ' com nota fiscal' : '' }}.
                @if (p.alreadyAnonymized) { {{ p.alreadyAnonymized }} já anonimizado(s). }
              </p>
              @if (p.inProgress.length) {
                <p class="error" data-in-progress>
                  Pedidos em andamento:
                  @for (n of p.inProgress; track n) {
                    <a routerLink="/pedidos" [queryParams]="{ busca: n }">nº {{ n }}</a>{{ $last ? '' : ', ' }}
                  }
                  . Conclua ou cancele antes de apagar os dados.
                </p>
              } @else if (confirming()) {
                <p>Confirma apagar os dados deste comprador? Não dá para desfazer.</p>
                <button type="button" class="danger" (click)="anonymize()" [disabled]="busy()" data-confirm>Apagar dados</button>
                <button type="button" class="link" (click)="confirming.set(false)">Cancelar</button>
              } @else {
                <button type="button" class="danger" (click)="confirming.set(true)" data-anonymize-button>Apagar dados deste comprador</button>
              }
            }
          </div>
        }
        @if (done()) {
          <p class="ok" role="status" data-done>{{ done() }}</p>
        }
        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }
      </section>

      <section data-access-log>
        <h2>Quem acessou dados pessoais</h2>
        <p class="muted">Cada abertura de pedido, download de nota fiscal, exportação e anonimização fica registrada e não pode ser apagada.</p>
        <div class="filters">
          <select [(ngModel)]="action" (change)="load(1)" aria-label="Ação">
            <option value="">Todas as ações</option>
            @for (a of actions; track a) {
              <option [value]="a">{{ labels[a] }}</option>
            }
          </select>
        </div>
        <table>
          <thead><tr><th>Quando</th><th>Quem</th><th>Ação</th><th>Pedido</th><th>Detalhes</th><th>IP</th></tr></thead>
          <tbody>
            @for (i of items(); track $index) {
              <tr>
                <td>{{ i.at | date: 'dd/MM/yyyy HH:mm' }}</td>
                <td>{{ i.userEmail }}</td>
                <td>{{ labels[i.action] ?? i.action }}</td>
                <td>@if (i.orderNumber) { nº {{ i.orderNumber }} }</td>
                <td>{{ i.details }}</td>
                <td class="muted">{{ i.ip }}</td>
              </tr>
            } @empty {
              <tr><td colspan="6" class="muted">Nenhum acesso registrado.</td></tr>
            }
          </tbody>
        </table>
        <div class="pager">
          <button type="button" (click)="load(page() - 1)" [disabled]="page() === 1">Anterior</button>
          <span>Página {{ page() }}</span>
          <button type="button" (click)="load(page() + 1)" [disabled]="!hasMore()">Próxima</button>
        </div>
      </section>
    </main>
  `,
})
export class PrivacyPage implements OnInit {
  private readonly api = inject(PrivacyApi);

  protected readonly labels = ACCESS_LABELS;
  protected readonly actions = Object.keys(ACCESS_LABELS);
  protected email = '';
  protected document = '';
  protected action = '';
  protected readonly preview = signal<AnonymizationPreview | null>(null);
  protected readonly confirming = signal(false);
  protected readonly done = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly items = signal<AccessItem[]>([]);
  protected readonly page = signal(1);
  protected readonly hasMore = signal(false);

  ngOnInit(): void {
    void this.load(1);
  }

  private subject(): DataSubject {
    return { email: this.email.trim() || undefined, documento: this.document.trim() || undefined };
  }

  async search(): Promise<void> {
    await this.run(async () => {
      this.done.set(null);
      this.confirming.set(false);
      this.preview.set(await this.api.preview(this.subject()));
    });
  }

  async anonymize(): Promise<void> {
    await this.run(async () => {
      const result = await this.api.anonymize(this.subject());
      this.preview.set(null);
      this.confirming.set(false);
      this.email = '';
      this.document = '';
      this.done.set(
        `Dados apagados em ${result.orders} pedido(s)` + (result.withInvoice ? `; ${result.withInvoice} mantêm nome e CPF/CNPJ por causa da nota fiscal.` : '.'),
      );
      await this.load(1);
    });
  }

  async load(page: number): Promise<void> {
    try {
      const result = await this.api.accesses(this.action, page);
      this.items.set(result.items);
      this.page.set(result.page);
      this.hasMore.set(result.hasMore);
    } catch (error) {
      this.error.set(problemTitle(error));
    }
  }

  private async run(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await action();
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }
}
