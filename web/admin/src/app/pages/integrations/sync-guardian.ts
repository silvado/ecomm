import { HttpClient } from '@angular/common/http';
import { Component, inject, Injectable, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { AuthStore, problemTitle } from '../../core/auth';

export type SyncAction = 'reconnect' | 'fixPart' | 'replay' | 'waitService' | 'contactSupport';

export interface SyncDiagnosis {
  key: string;
  kind: 'backgroundFailure' | 'repeatedCallFailure' | 'channelReauth';
  title: string;
  occurrences: number;
  lastAt: string;
  lastError: string | null;
  explanation: string;
  action: SyncAction;
  actionText: string;
}

const WHEN = new Intl.DateTimeFormat('pt-BR', { timeZone: 'America/Sao_Paulo', dateStyle: 'short', timeStyle: 'short' });

/** Guardião da sincronização (RF33): problemas de agora e o aviso por e-mail aos Donos. */
@Injectable({ providedIn: 'root' })
export class SyncGuardianApi {
  private readonly http = inject(HttpClient);

  diagnosis(): Promise<SyncDiagnosis[]> {
    return firstValueFrom(this.http.get<SyncDiagnosis[]>('/api/painel/integracoes/diagnostico'));
  }

  settings(): Promise<{ enabled: boolean }> {
    return firstValueFrom(this.http.get<{ enabled: boolean }>('/api/painel/guardiao'));
  }

  save(ativo: boolean): Promise<{ enabled: boolean }> {
    return firstValueFrom(this.http.put<{ enabled: boolean }>('/api/painel/guardiao', { ativo }));
  }
}

/**
 * Em Integrações: o que está falhando agora, em linguagem simples, com a ação sugerida. O Dono liga ou desliga o aviso
 * por e-mail (o mesmo problema só é avisado de novo depois de 24 horas).
 */
@Component({
  selector: 'app-sync-guardian',
  imports: [FormsModule],
  styles: ['[data-diagnosis] { margin-bottom: 0.75rem; } [data-diagnosis] p { margin: 0.25rem 0; }'],
  template: `
    <section data-sync-guardian>
      <h2>Guardião da sincronização</h2>
      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
      @for (d of diagnoses(); track d.key) {
        <div class="warn" [attr.data-diagnosis]="d.kind">
          <p><strong>{{ d.title }}</strong></p>
          <p>{{ d.explanation }}</p>
          <p><strong>O que fazer:</strong> {{ d.actionText }}</p>
          <p class="muted">Última vez: {{ when(d.lastAt) }}@if (d.lastError) { · {{ d.lastError }} }</p>
        </div>
      } @empty {
        @if (loaded()) {
          <p class="ok" data-no-problems>Nenhum problema na sincronização agora.</p>
        }
      }
      @if (canManage) {
        <label class="check">
          <input type="checkbox" [(ngModel)]="enabled" (ngModelChange)="save()" [disabled]="busy()" name="guardian-enabled" data-guardian-enabled />
          Avisar os Donos por e-mail quando a sincronização falhar
        </label>
        @if (saved()) {
          <p class="ok" role="status">Aviso por e-mail {{ enabled ? 'ligado' : 'desligado' }}.</p>
        }
      }
    </section>
  `,
})
export class SyncGuardianSection implements OnInit {
  private readonly api = inject(SyncGuardianApi);

  protected readonly canManage = inject(AuthStore).can('storeManage');
  protected readonly diagnoses = signal<SyncDiagnosis[]>([]);
  protected readonly loaded = signal(false);
  protected readonly busy = signal(false);
  protected readonly saved = signal(false);
  protected readonly error = signal<string | null>(null);
  protected enabled = true;
  protected readonly when = (iso: string) => WHEN.format(new Date(iso));

  async ngOnInit(): Promise<void> {
    try {
      const [diagnoses, settings] = await Promise.all([this.api.diagnosis(), this.canManage ? this.api.settings() : Promise.resolve(null)]);
      this.diagnoses.set(diagnoses);
      if (settings) this.enabled = settings.enabled;
      this.loaded.set(true);
    } catch (error) {
      this.error.set(problemTitle(error));
    }
  }

  async save(): Promise<void> {
    this.busy.set(true);
    this.saved.set(false);
    this.error.set(null);
    try {
      this.enabled = (await this.api.save(this.enabled)).enabled;
      this.saved.set(true);
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }
}
