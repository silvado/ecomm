import { HttpClient } from '@angular/common/http';
import { Component, inject, Injectable, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { problemTitle } from '../../core/auth';
import { feedback } from '../../core/feedback';

export interface LegalTexts {
  privacyPolicy: string;
  terms: string;
  defaultPrivacyPolicy: string;
  defaultTerms: string;
}

/** Mesmo limite da API (StoreBranding.LegalTextMaxLength). */
export const LEGAL_TEXT_MAX = 20000;

@Injectable({ providedIn: 'root' })
export class LegalTextsApi {
  private readonly http = inject(HttpClient);

  get(): Promise<LegalTexts> {
    return firstValueFrom(this.http.get<LegalTexts>('/api/painel/loja/privacidade'));
  }

  save(politica: string, termos: string): Promise<LegalTexts> {
    return firstValueFrom(this.http.put<LegalTexts>('/api/painel/loja/privacidade', { politica, termos }, { context: feedback('Textos salvos. A loja mostra a nova versão em até 1 minuto.') }));
  }
}

/**
 * Política de privacidade e termos de uso (RNF04), em Dados da loja. Vazio = a loja mostra o modelo padrão, já com a
 * razão social e o CNPJ. Vale também com a loja personalizada: é texto jurídico, não aparência.
 */
@Component({
  selector: 'app-legal-texts',
  imports: [FormsModule],
  template: `
    <section data-legal-texts>
      <h2>Privacidade e termos</h2>
      <p class="muted">
        Aparecem na loja virtual em <strong>Política de privacidade</strong> e <strong>Termos de uso</strong>. Enquanto ficarem em branco,
        a loja mostra um modelo padrão com a razão social e o CNPJ. Recomendamos revisar com um advogado.
      </p>

      <label>
        Política de privacidade
        <textarea [(ngModel)]="privacyPolicy" name="privacyPolicy" rows="10" [maxlength]="max" [placeholder]="defaults.privacyPolicy" data-privacy></textarea>
      </label>
      <div class="order-actions">
        <button type="button" class="link" (click)="privacyPolicy = defaults.privacyPolicy" data-use-privacy-template>Começar pelo modelo</button>
        @if (privacyPolicy) {
          <button type="button" class="link" (click)="privacyPolicy = ''">Voltar ao modelo padrão</button>
        }
      </div>

      <label>
        Termos de uso
        <textarea [(ngModel)]="terms" name="terms" rows="10" [maxlength]="max" [placeholder]="defaults.terms" data-terms></textarea>
      </label>
      <div class="order-actions">
        <button type="button" class="link" (click)="terms = defaults.terms">Começar pelo modelo</button>
        @if (terms) {
          <button type="button" class="link" (click)="terms = ''">Voltar ao modelo padrão</button>
        }
      </div>

      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
      <div class="order-actions">
        <button type="button" (click)="save()" [disabled]="busy()" data-save-legal>Salvar privacidade e termos</button>
      </div>
    </section>
  `,
})
export class LegalTextsSection implements OnInit {
  private readonly api = inject(LegalTextsApi);

  protected readonly max = LEGAL_TEXT_MAX;
  protected privacyPolicy = '';
  protected terms = '';
  protected defaults = { privacyPolicy: '', terms: '' };
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    try {
      this.apply(await this.api.get());
    } catch (error) {
      this.error.set(problemTitle(error));
    }
  }

  protected async save(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      this.apply(await this.api.save(this.privacyPolicy, this.terms));
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }

  private apply(texts: LegalTexts): void {
    this.privacyPolicy = texts.privacyPolicy;
    this.terms = texts.terms;
    this.defaults = { privacyPolicy: texts.defaultPrivacyPolicy, terms: texts.defaultTerms };
  }
}
