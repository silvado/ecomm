import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { problemTitle } from '../../core/auth';
import { contrastRatio, isHexColor, MIN_COMPONENT_CONTRAST, MIN_TEXT_CONTRAST, readableOn } from '../../shared/contrast';
import { StoreApi, StoreProfile } from './store.api';

const hex = Validators.pattern(/^#[0-9a-fA-F]{6}$/);

@Component({
  selector: 'app-store',
  imports: [ReactiveFormsModule],
  template: `
    <main class="card">
      <h1>Dados da loja</h1>
      @if (profile(); as p) {
        <p class="muted">
          {{ p.legalName }} · CNPJ {{ p.cnpj }} · endereço da loja: <strong>{{ p.slug }}</strong>.
          Razão social e CNPJ não mudam por aqui.
        </p>
      }

      <form [formGroup]="form" (ngSubmit)="save()">
        <label>
          Nome da loja
          <input type="text" formControlName="tradeName" maxlength="120" required />
        </label>

        <fieldset class="colors">
          <legend>Cores</legend>
          @for (field of colorFields; track field.name) {
            <label>
              {{ field.label }}
              <span class="color-input">
                <input type="color" [formControlName]="field.name" [attr.aria-label]="field.label + ' (seletor)'" />
                <input type="text" [formControlName]="field.name" maxlength="7" />
              </span>
            </label>
          }
        </fieldset>

        @for (warning of liveWarnings(); track warning) {
          <p class="warn" role="status">{{ warning }}</p>
        }

        <div class="preview" [style.background]="colors().background" [style.color]="colors().text">
          <div class="preview-bar" [style.background]="colors().primary" [style.color]="onPrimary()">
            {{ form.controls.tradeName.value || 'Sua loja' }}
          </div>
          <p>Assim fica o texto da loja sobre o fundo.</p>
          <a [style.color]="colors().primary">Um link na cor principal</a>
        </div>

        <label>
          Sobre a loja
          <textarea formControlName="about" rows="4" maxlength="4000"></textarea>
        </label>
        <label>
          Trocas e devoluções
          <textarea formControlName="returnPolicy" rows="4" maxlength="4000"></textarea>
        </label>
        <label>
          Rodapé (endereço, telefone, horário)
          <textarea formControlName="footer" rows="2" maxlength="500"></textarea>
        </label>

        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }
        @if (saved()) {
          <p class="ok" role="status">Salvo. A loja mostra as mudanças em até 1 minuto.</p>
        }
        <button type="submit" [disabled]="form.invalid || busy()">Salvar</button>
      </form>
    </main>
  `,
})
export class StorePage implements OnInit {
  private readonly api = inject(StoreApi);

  protected readonly colorFields = [
    { name: 'primaryColor', label: 'Principal (cabeçalho, botões, links)' },
    { name: 'backgroundColor', label: 'Fundo' },
    { name: 'textColor', label: 'Texto' },
  ] as const;

  protected readonly profile = signal<StoreProfile | null>(null);
  protected readonly busy = signal(false);
  protected readonly saved = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    tradeName: ['', [Validators.required, Validators.maxLength(120)]],
    primaryColor: ['#1F5FBF', [Validators.required, hex]],
    backgroundColor: ['#FFFFFF', [Validators.required, hex]],
    textColor: ['#1D2330', [Validators.required, hex]],
    about: [''],
    returnPolicy: [''],
    footer: [''],
  });

  private readonly values = toSignal(this.form.valueChanges, { initialValue: this.form.getRawValue() });

  protected readonly colors = computed(() => {
    const v = { ...this.form.getRawValue(), ...this.values() };
    const pick = (c: string | undefined, fallback: string) => (c && isHexColor(c) ? c : fallback);
    return {
      primary: pick(v.primaryColor, '#1F5FBF'),
      background: pick(v.backgroundColor, '#FFFFFF'),
      text: pick(v.textColor, '#1D2330'),
    };
  });

  protected readonly onPrimary = computed(() => readableOn(this.colors().primary));

  /** Aviso enquanto o Dono escolhe; não impede salvar (RF01 CA4). */
  protected readonly liveWarnings = computed(() => {
    const { primary, background, text } = this.colors();
    const warnings: string[] = [];
    const textRatio = contrastRatio(text, background);
    if (textRatio < MIN_TEXT_CONTRAST)
      warnings.push(`Texto pouco legível sobre o fundo (contraste ${textRatio.toFixed(1)}:1; o recomendado é ${MIN_TEXT_CONTRAST}:1).`);
    const primaryRatio = contrastRatio(primary, background);
    if (primaryRatio < MIN_COMPONENT_CONTRAST)
      warnings.push(`A cor principal some sobre o fundo (contraste ${primaryRatio.toFixed(1)}:1; o recomendado é ${MIN_COMPONENT_CONTRAST}:1).`);
    return warnings;
  });

  async ngOnInit(): Promise<void> {
    try {
      this.fill(await this.api.get());
    } catch (error) {
      this.error.set(problemTitle(error));
    }
  }

  async save(): Promise<void> {
    if (this.form.invalid) return;
    this.busy.set(true);
    this.saved.set(false);
    this.error.set(null);
    try {
      const v = this.form.getRawValue();
      this.fill(await this.api.update({ ...v, primaryColor: v.primaryColor.toUpperCase(), backgroundColor: v.backgroundColor.toUpperCase(), textColor: v.textColor.toUpperCase() }));
      this.saved.set(true);
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }

  private fill(profile: StoreProfile): void {
    this.profile.set(profile);
    this.form.reset({ tradeName: profile.tradeName, ...profile.theme, ...profile.texts });
  }
}
