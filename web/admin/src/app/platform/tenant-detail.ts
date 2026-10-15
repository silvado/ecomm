import { DatePipe } from '@angular/common';
import { Component, inject, input, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { problemTitle } from '../core/auth';
import { RoleLabelPipe } from '../shared/role-label';
import { PlatformApi, PlatformPlan, PlatformTenantDetail, STATUS_LABELS } from './platform.api';

/**
 * Uma loja vista pela plataforma: dados cadastrais, domínios e usuários (nada de peças, pedidos ou clientes — ADR-0007).
 * Abrir esta tela fica registrado na auditoria.
 */
@Component({
  selector: 'app-platform-tenant',
  imports: [DatePipe, FormsModule, RouterLink, RoleLabelPipe],
  template: `
    <main class="card wide">
      <p><a routerLink="/plataforma">← Lojas</a></p>
      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
      @if (tenant(); as t) {
        <div class="header-row">
          <h1>{{ t.tradeName }}</h1>
          <span class="badge" data-status>{{ labels[t.status] }}</span>
        </div>
        <p class="muted">{{ t.legalName }} · CNPJ {{ t.cnpj }} · criada em {{ t.createdAt | date: 'dd/MM/yyyy' }}</p>

        <h2>Plano</h2>
        <div class="filters">
          <select [(ngModel)]="plan" aria-label="Plano">
            @for (p of plans(); track p.code) {
              <option [value]="p.code">{{ p.name }}</option>
            }
          </select>
          <button type="button" (click)="changePlan()" [disabled]="busy() || plan === t.planCode">Trocar plano</button>
        </div>
        <p class="muted">Vale na hora. Ao baixar de plano, o que já passou do novo limite continua; só não dá para acrescentar mais.</p>

        <h2>Situação</h2>
        @if (t.status === 'Suspended') {
          <p>A loja está suspensa: a vitrine fica fora do ar e a sincronização parada.</p>
          <button type="button" (click)="act('reactivate')" [disabled]="busy()" data-reactivate>Reativar loja</button>
        } @else if (t.status !== 'Closed') {
          @if (confirming()) {
            <form class="confirm" (ngSubmit)="act('suspend')">
              <p>Suspender tira a vitrine do ar e para a sincronização com os canais. Confirma?</p>
              <button type="submit" class="danger" [disabled]="busy()" data-confirm-suspend>Suspender</button>
              <button type="button" class="link" (click)="confirming.set(false)">Cancelar</button>
            </form>
          } @else {
            <button type="button" class="danger" (click)="confirming.set(true)" data-suspend>Suspender loja</button>
          }
        }

        <h2>Loja personalizada</h2>
        @if (t.customStorefrontSince) {
          <p>Ligada desde {{ t.customStorefrontSince | date: 'dd/MM/yyyy HH:mm' }}: o Dono não muda logo, nome, cores nem textos.</p>
          <button type="button" (click)="setCustom(false)" [disabled]="busy()">Desligar</button>
        } @else {
          <p>Desligada: o Dono edita a aparência pelo painel.</p>
          <button type="button" (click)="setCustom(true)" [disabled]="busy()" data-custom-on>Ligar</button>
        }

        <h2>Endereços</h2>
        <table>
          <thead><tr><th>Domínio</th><th>Situação</th><th></th></tr></thead>
          <tbody>
            @for (d of t.domains; track d.host) {
              <tr>
                <td>{{ d.host }}</td>
                <td>{{ d.kind === 'PlatformSubdomain' ? 'Subdomínio da plataforma' : d.status === 'Verified' ? 'Verificado' : d.status === 'Failed' ? 'Falhou' : 'Aguardando DNS' }}</td>
                <td>@if (d.isPrimary) { <span class="badge">principal</span> }</td>
              </tr>
            }
          </tbody>
        </table>

        <h2>Usuários</h2>
        <table>
          <thead><tr><th>E-mail</th><th>Perfil</th><th></th></tr></thead>
          <tbody>
            @for (u of t.users; track u.email) {
              <tr>
                <td>{{ u.email }}</td>
                <td>{{ roleOf(u.role) | roleLabel }}</td>
                <td class="muted">
                  @if (u.mustChangePassword) { senha provisória }
                  @if (u.lockedOut) { bloqueado }
                </td>
              </tr>
            }
          </tbody>
        </table>

        <p><a [routerLink]="['/plataforma/auditoria']" [queryParams]="{ loja: t.id }">Ver o que a plataforma fez nesta loja</a></p>
      }
    </main>
  `,
})
export class PlatformTenantPage implements OnInit {
  private readonly api = inject(PlatformApi);

  readonly id = input.required<string>();
  protected readonly labels = STATUS_LABELS;
  protected readonly tenant = signal<PlatformTenantDetail | null>(null);
  protected readonly plans = signal<PlatformPlan[]>([]);
  protected readonly busy = signal(false);
  protected readonly confirming = signal(false);
  protected readonly error = signal<string | null>(null);
  protected plan = '';

  async ngOnInit(): Promise<void> {
    try {
      const [tenant, plans] = await Promise.all([this.api.tenant(this.id()), this.api.plans()]);
      this.plans.set(plans);
      this.show(tenant);
    } catch (error) {
      this.error.set(problemTitle(error, 'Loja não encontrada.'));
    }
  }

  protected roleOf(role: string): 'owner' | 'operator' {
    return role.toLowerCase() === 'owner' ? 'owner' : 'operator';
  }

  changePlan = () => this.run(() => this.api.changePlan(this.id(), this.plan));

  act = (action: 'suspend' | 'reactivate') =>
    this.run(() => (action === 'suspend' ? this.api.suspend(this.id()) : this.api.reactivate(this.id())));

  setCustom = (on: boolean) => this.run(() => this.api.setCustomStorefront(this.id(), on));

  private show(tenant: PlatformTenantDetail): void {
    this.tenant.set(tenant);
    this.plan = tenant.planCode;
    this.confirming.set(false);
  }

  private async run(action: () => Promise<PlatformTenantDetail>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      this.show(await action());
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }
}
