import { DatePipe } from '@angular/common';
import { Component, inject, OnInit, signal } from '@angular/core';
import { FormBuilder, FormsModule, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { problemTitle } from '../core/auth';
import { CreatedTenant, PlatformApi, PlatformTenantItem, STATUS_LABELS, TenantStatus } from './platform.api';

/** Lojas da plataforma: busca, filtro por situação e criação de loja (com o Dono e a senha provisória dele). */
@Component({
  selector: 'app-platform-tenants',
  imports: [DatePipe, FormsModule, ReactiveFormsModule, RouterLink],
  template: `
    <main class="card wide">
      <div class="header-row">
        <h1>Lojas</h1>
        <button type="button" (click)="creating.set(!creating())">{{ creating() ? 'Fechar' : 'Nova loja' }}</button>
      </div>

      @if (created(); as c) {
        <div class="notice" role="status" data-created>
          <p><strong>Loja criada:</strong> <a [href]="'https://' + c.host" target="_blank" rel="noopener">{{ c.host }}</a></p>
          @if (c.temporaryPassword) {
            <p>
              Senha provisória do Dono ({{ c.ownerEmail }}): <code>{{ c.temporaryPassword }}</code><br />
              <small>Aparece só agora. Envie ao Dono por um canal seguro; no primeiro acesso ele troca a senha.</small>
            </p>
          } @else {
            <p>{{ c.ownerEmail }} já tinha conta na plataforma e entra com a senha que já usa.</p>
          }
          <button type="button" class="link" (click)="created.set(null)">Fechar aviso</button>
        </div>
      }

      @if (creating()) {
        <form [formGroup]="form" (ngSubmit)="create()" data-create-form>
          <div class="grid-3">
            <label>Nome fantasia <input formControlName="nome" required maxlength="120" /></label>
            <label>Razão social <input formControlName="razaoSocial" required maxlength="200" /></label>
            <label>CNPJ <input formControlName="cnpj" required /></label>
            <label>
              Endereço
              <input formControlName="slug" required maxlength="40" />
              <small>{{ form.controls.slug.value || 'endereco' }}.&lt;domínio da plataforma&gt; — letras minúsculas, números e hífen.</small>
            </label>
            <label>E-mail do Dono <input type="email" formControlName="dono" required /></label>
          </div>
          @if (createError()) {
            <p class="error" role="alert">{{ createError() }}</p>
          }
          <button type="submit" [disabled]="form.invalid || busy()">Criar loja</button>
        </form>
      }

      <div class="filters">
        <input type="search" placeholder="Nome, endereço, domínio ou CNPJ completo" [(ngModel)]="search" (keyup.enter)="load()" aria-label="Buscar lojas" />
        <select [(ngModel)]="status" (change)="load()" aria-label="Situação">
          <option value="">Todas as situações</option>
          @for (s of statuses; track s) {
            <option [value]="s">{{ labels[s] }}</option>
          }
        </select>
        <button type="button" (click)="load()">Buscar</button>
      </div>

      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
      <table>
        <thead>
          <tr><th>Loja</th><th>Endereço</th><th>CNPJ</th><th>Plano</th><th>Situação</th><th>Desde</th></tr>
        </thead>
        <tbody>
          @for (t of tenants(); track t.id) {
            <tr>
              <td><a [routerLink]="['/plataforma/lojas', t.id]">{{ t.tradeName }}</a>@if (t.customStorefront) { <span class="badge">personalizada</span> }</td>
              <td>{{ t.primaryHost }}</td>
              <td>{{ t.cnpj }}</td>
              <td>{{ t.planCode }}</td>
              <td>{{ labels[t.status] }}</td>
              <td>{{ t.createdAt | date: 'dd/MM/yyyy' }}</td>
            </tr>
          } @empty {
            <tr><td colspan="6" class="muted">Nenhuma loja encontrada.</td></tr>
          }
        </tbody>
      </table>
    </main>
  `,
})
export class PlatformTenantsPage implements OnInit {
  private readonly api = inject(PlatformApi);

  protected readonly labels = STATUS_LABELS;
  protected readonly statuses = Object.keys(STATUS_LABELS) as TenantStatus[];
  protected readonly tenants = signal<PlatformTenantItem[]>([]);
  protected readonly creating = signal(false);
  protected readonly created = signal<CreatedTenant | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly createError = signal<string | null>(null);
  protected search = '';
  protected status = '';

  protected readonly form = inject(FormBuilder).nonNullable.group({
    nome: ['', Validators.required],
    razaoSocial: ['', Validators.required],
    cnpj: ['', Validators.required],
    slug: ['', Validators.required],
    dono: ['', [Validators.required, Validators.email]],
  });

  ngOnInit(): void {
    void this.load();
  }

  async load(): Promise<void> {
    this.error.set(null);
    try {
      this.tenants.set(await this.api.tenants(this.search, this.status));
    } catch (error) {
      this.error.set(problemTitle(error));
    }
  }

  async create(): Promise<void> {
    this.busy.set(true);
    this.createError.set(null);
    try {
      this.created.set(await this.api.create(this.form.getRawValue()));
      this.form.reset();
      this.creating.set(false);
      await this.load();
    } catch (error) {
      this.createError.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }
}
