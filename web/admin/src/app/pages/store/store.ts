import { Component, computed, inject, OnDestroy, OnInit, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { problemTitle } from '../../core/auth';
import { contrastRatio, isHexColor, MIN_COMPONENT_CONTRAST, MIN_TEXT_CONTRAST, readableOn } from '../../shared/contrast';
import { StoreApi, StoreProfile } from './store.api';

const hex = Validators.pattern(/^#[0-9a-fA-F]{6}$/);

/** Mesmo limite da API (LogoImage.MaxBytes): evita enviar à toa um arquivo que será recusado. */
const LOGO_MAX_BYTES = 2 * 1024 * 1024;

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

      <section class="logo">
        <h2>Logo</h2>
        <div class="logo-row">
          @if (logoUrl(); as url) {
            <img [src]="url" alt="Logo atual" class="logo-preview" />
          } @else {
            <span class="muted">Sem logo: a loja mostra o nome no cabeçalho.</span>
          }
          <label class="button-like">
            {{ logoUrl() ? 'Trocar logo' : 'Enviar logo' }}
            <input type="file" accept="image/png,image/jpeg,image/webp" (change)="uploadLogo($event)" [disabled]="busy()" hidden />
          </label>
          @if (logoUrl()) {
            <button type="button" class="link danger" (click)="removeLogo()" [disabled]="busy()">Remover</button>
          }
        </div>
        <small>PNG, JPG ou WebP, até 2 MB.</small>
        @if (logoError()) {
          <p class="error" role="alert">{{ logoError() }}</p>
        }
      </section>

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

        <fieldset>
          <legend>Entrega e retirada</legend>
          <label>
            CEP de onde saem as entregas
            <input type="text" formControlName="originPostalCode" inputmode="numeric" maxlength="9" placeholder="00000-000" />
            <small>Sem CEP, a loja não oferece entrega (só retirada, se habilitada).</small>
          </label>
          <label class="checkbox">
            <input type="checkbox" formControlName="pickupEnabled" />
            Oferecer retirada no balcão
          </label>
          @if (form.controls.pickupEnabled.value) {
            <label>
              Endereço e horário da retirada
              <textarea formControlName="pickupAddress" rows="2" maxlength="300" placeholder="Rua, número, bairro — seg a sex, 8h às 18h"></textarea>
            </label>
          }
        </fieldset>

        <label class="checkbox">
          <input type="checkbox" formControlName="hideOutOfStock" />
          Ocultar da loja as peças sem estoque (sem marcar, elas aparecem como indisponíveis)
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
export class StorePage implements OnInit, OnDestroy {
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
    hideOutOfStock: [false],
    originPostalCode: ['', Validators.pattern(/^\s*\d{5}-?\d{3}\s*$/)],
    pickupEnabled: [false],
    pickupAddress: [''],
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

  protected readonly logoUrl = signal<string | null>(null);
  protected readonly logoError = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    try {
      this.fill(await this.api.get());
    } catch (error) {
      this.error.set(problemTitle(error));
    }
  }

  ngOnDestroy(): void {
    this.setLogoUrl(null);
  }

  async uploadLogo(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    this.logoError.set(null);
    if (file.size > LOGO_MAX_BYTES) {
      this.logoError.set('O logo pode ter no máximo 2 MB.');
      return;
    }
    await this.runLogo(() => this.api.uploadLogo(file));
  }

  async removeLogo(): Promise<void> {
    if (!confirm('Remover o logo? A loja volta a mostrar o nome no cabeçalho.')) return;
    await this.runLogo(() => this.api.removeLogo());
  }

  private async runLogo(action: () => Promise<StoreProfile>): Promise<void> {
    this.busy.set(true);
    this.logoError.set(null);
    try {
      const profile = await action();
      this.profile.set(profile);
      await this.loadLogo(profile);
    } catch (error) {
      this.logoError.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }

  private async loadLogo(profile: StoreProfile): Promise<void> {
    if (!profile.logoId) {
      this.setLogoUrl(null);
      return;
    }
    try {
      this.setLogoUrl(URL.createObjectURL(await this.api.logo()));
    } catch {
      this.setLogoUrl(null);
    }
  }

  private setLogoUrl(url: string | null): void {
    const previous = this.logoUrl();
    if (previous) URL.revokeObjectURL(previous);
    this.logoUrl.set(url);
  }

  async save(): Promise<void> {
    if (this.form.invalid) return;
    this.busy.set(true);
    this.saved.set(false);
    this.error.set(null);
    try {
      const { originPostalCode, pickupEnabled, pickupAddress, ...v } = this.form.getRawValue();
      this.fill(
        await this.api.update({
          ...v,
          primaryColor: v.primaryColor.toUpperCase(),
          backgroundColor: v.backgroundColor.toUpperCase(),
          textColor: v.textColor.toUpperCase(),
          shipping: { originPostalCode: originPostalCode.trim() || null, pickupEnabled, pickupAddress: pickupEnabled ? pickupAddress.trim() || null : null },
        }),
      );
      this.saved.set(true);
    } catch (error) {
      this.error.set(problemTitle(error));
    } finally {
      this.busy.set(false);
    }
  }

  private fill(profile: StoreProfile): void {
    this.profile.set(profile);
    void this.loadLogo(profile);
    const cep = profile.shipping.originPostalCode;
    this.form.reset({
      tradeName: profile.tradeName,
      ...profile.theme,
      ...profile.texts,
      hideOutOfStock: profile.hideOutOfStock,
      originPostalCode: cep ? `${cep.slice(0, 5)}-${cep.slice(5)}` : '',
      pickupEnabled: profile.shipping.pickupEnabled,
      pickupAddress: profile.shipping.pickupAddress ?? '',
    });
  }
}
