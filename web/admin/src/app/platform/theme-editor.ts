import { DatePipe } from '@angular/common';
import { Component, inject, input, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { RouterLink } from '@angular/router';
import { problemTitle } from '../core/auth';
import { PageDraft, PlatformApi, PlatformTheme, ThemeDraft, ThemePageItem, ThemePreview } from './platform.api';

/** Mesmos limites da API (TenantTheme, TenantPage). */
export const THEME_CSS_MAX = 100000;
export const THEME_HTML_MAX = 50000;

const EMPTY_PAGE: PageDraft = { endereco: '', titulo: '', conteudo: '', publicada: false, ordem: 0 };

/**
 * Documento da prévia: só o que o servidor já limpou, num iframe com sandbox vazio (sem script, sem acesso ao painel).
 * O miolo imita a vitrine para a equipe ver o tema aplicado.
 */
export function previewDocument(preview: ThemePreview, sampleHtml = ''): string {
  const css = preview.css.includes('<') ? '' : preview.css;
  return (
    '<!doctype html><html lang="pt-BR"><head><meta charset="utf-8">' +
    '<style>body{font-family:system-ui,sans-serif;margin:0}main{padding:1rem}</style>' +
    `<style>.custom-theme {\n${css}\n}</style></head><body class="custom-theme">` +
    preview.headerHtml +
    `<main>${sampleHtml || '<h1>Peças em destaque</h1><p>Aqui aparecem as peças da loja.</p>'}</main>` +
    `<footer>${preview.footerHtml}</footer></body></html>`
  );
}

/**
 * Tema e páginas exclusivos da loja personalizada (RF40 CA1). Só a equipe da plataforma edita; tudo é limpo no servidor
 * ao salvar (sem script, eventos, iframes ou formulários) e fica na auditoria. A vitrine só usa com a opção ligada.
 */
@Component({
  selector: 'app-platform-theme',
  imports: [DatePipe, FormsModule, RouterLink],
  template: `
    <main class="card wide">
      <p><a [routerLink]="['/plataforma/lojas', id()]">← Loja</a></p>
      @if (theme(); as t) {
        <h1>Tema exclusivo — {{ t.tradeName }}</h1>
        @if (!t.customStorefront) {
          <p class="notice" data-custom-off>
            A loja personalizada está desligada: o tema e as páginas ficam guardados, mas a loja virtual só passa a usá-los quando a opção for ligada.
          </p>
        }
        <p class="muted">
          O HTML aceita conteúdo (títulos, textos, imagens, links, listas e tabelas); o visual vem do CSS, que vale só nas áreas do tema.
          Carrinho, checkout e Meus pedidos continuam com o layout padrão. Script, eventos, iframes, formulários e links não seguros são removidos ao salvar.
          @if (t.updatedAt) {
            Última alteração em {{ t.updatedAt | date: 'dd/MM/yyyy HH:mm' }} por {{ t.updatedBy }}.
          }
        </p>

        <h2>Tema</h2>
        <label>
          CSS
          <textarea [(ngModel)]="draft.css" name="css" rows="12" [maxlength]="cssMax" spellcheck="false" class="code" data-css></textarea>
        </label>
        <label>
          Cabeçalho (HTML, aparece acima do menu da loja)
          <textarea [(ngModel)]="draft.cabecalho" name="header" rows="6" [maxlength]="htmlMax" spellcheck="false" class="code" data-header></textarea>
        </label>
        <label>
          Rodapé (HTML, substitui o texto do rodapé)
          <textarea [(ngModel)]="draft.rodape" name="footer" rows="6" [maxlength]="htmlMax" spellcheck="false" class="code" data-footer></textarea>
        </label>
        <div class="order-actions">
          <button type="button" (click)="preview()" [disabled]="busy()" data-preview>Pré-visualizar</button>
          <button type="button" (click)="saveTheme()" [disabled]="busy()" data-save-theme>Salvar tema</button>
        </div>
      }

      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
      @if (removed().length) {
        <div class="notice" data-removed>
          <p>A limpeza removeu:</p>
          <ul>
            @for (item of removed(); track item) {
              <li>{{ item }}</li>
            }
          </ul>
        </div>
      }
      @if (previewHtml(); as html) {
        <iframe class="theme-preview" title="Pré-visualização do tema" sandbox="" [srcdoc]="html" data-preview-frame></iframe>
      }

      @if (theme(); as t) {
        <h2>Páginas</h2>
        @if (t.pages.length) {
          <table>
            <thead><tr><th>Título</th><th>Endereço</th><th>Situação</th><th></th></tr></thead>
            <tbody>
              @for (p of t.pages; track p.id) {
                <tr>
                  <td>{{ p.title }}</td>
                  <td>/p/{{ p.slug }}</td>
                  <td>{{ p.published ? 'Publicada' : 'Rascunho' }}</td>
                  <td><button type="button" class="link" (click)="edit(p)" [attr.data-edit-page]="p.slug">Editar</button></td>
                </tr>
              }
            </tbody>
          </table>
        } @else {
          <p class="muted">Nenhuma página ainda.</p>
        }

        <h3>{{ editing() ? 'Editar página' : 'Nova página' }}</h3>
        <div class="filters">
          <label>Título <input [(ngModel)]="page.titulo" name="pageTitle" maxlength="120" data-page-title /></label>
          <label>Endereço (/p/…) <input [(ngModel)]="page.endereco" name="pageSlug" maxlength="60" placeholder="quem-somos" data-page-slug /></label>
          <label>Ordem no rodapé <input type="number" [(ngModel)]="page.ordem" name="pageOrder" min="0" /></label>
          <label class="checkbox"><input type="checkbox" [(ngModel)]="page.publicada" name="pagePublished" data-page-published /> Publicada</label>
        </div>
        <label>
          Conteúdo (HTML)
          <textarea [(ngModel)]="page.conteudo" name="pageHtml" rows="10" [maxlength]="htmlMax" spellcheck="false" class="code" data-page-html></textarea>
        </label>
        <div class="order-actions">
          <button type="button" (click)="previewPage()" [disabled]="busy()">Pré-visualizar página</button>
          <button type="button" (click)="savePage()" [disabled]="busy()" data-save-page>{{ editing() ? 'Salvar página' : 'Criar página' }}</button>
          @if (editing()) {
            @if (confirmingDelete()) {
              <button type="button" class="danger" (click)="deletePage()" [disabled]="busy()" data-confirm-delete-page>Confirmar remoção</button>
              <button type="button" class="link" (click)="confirmingDelete.set(false)">Cancelar</button>
            } @else {
              <button type="button" class="danger" (click)="confirmingDelete.set(true)">Remover página</button>
            }
            <button type="button" class="link" (click)="newPage()">Nova página</button>
          }
        </div>
      }
    </main>
  `,
})
export class PlatformThemePage implements OnInit {
  private readonly api = inject(PlatformApi);
  private readonly sanitizer = inject(DomSanitizer);

  readonly id = input.required<string>();
  protected readonly cssMax = THEME_CSS_MAX;
  protected readonly htmlMax = THEME_HTML_MAX;
  protected readonly theme = signal<PlatformTheme | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly removed = signal<string[]>([]);
  protected readonly previewHtml = signal<SafeHtml | null>(null);
  protected readonly editing = signal<string | null>(null);
  protected readonly confirmingDelete = signal(false);
  protected draft: ThemeDraft = { css: '', cabecalho: '', rodape: '' };
  protected page: PageDraft = { ...EMPTY_PAGE };

  async ngOnInit(): Promise<void> {
    try {
      this.show(await this.api.theme(this.id()));
    } catch (error) {
      this.error.set(problemTitle(error, 'Loja não encontrada.'));
    }
  }

  protected preview = () =>
    this.run(async () => {
      const result = await this.api.previewTheme(this.id(), this.draft);
      this.removed.set(result.removed);
      this.showPreview(result);
    });

  protected previewPage = () =>
    this.run(async () => {
      // A página usa o mesmo tema: a prévia limpa o conteúdo junto (no lugar do cabeçalho, só para limpar).
      const theme = await this.api.previewTheme(this.id(), this.draft);
      const content = await this.api.previewTheme(this.id(), { css: '', cabecalho: this.page.conteudo, rodape: '' });
      this.removed.set(content.removed.map((r) => r.replace(/^Cabeçalho: /, 'Página: ')));
      this.showPreview(theme, `<h1>${escapeText(this.page.titulo)}</h1>${content.headerHtml}`);
    });

  protected saveTheme = () =>
    this.run(async () => {
      const saved = await this.api.saveTheme(this.id(), this.draft);
      this.show(saved.theme);
      this.removed.set(saved.removed);
    });

  protected savePage = () =>
    this.run(async () => {
      const editing = this.editing();
      const saved = editing ? await this.api.updatePage(this.id(), editing, this.page) : await this.api.createPage(this.id(), this.page);
      this.show(saved.theme);
      this.removed.set(saved.removed);
      const stored = saved.theme.pages.find((p) => p.slug === this.page.endereco.trim().toLowerCase());
      if (stored) this.edit(stored);
    });

  protected deletePage = () =>
    this.run(async () => {
      const editing = this.editing();
      if (!editing) return;
      this.show((await this.api.deletePage(this.id(), editing)).theme);
      this.newPage();
    });

  protected edit(page: ThemePageItem): void {
    this.editing.set(page.id);
    this.confirmingDelete.set(false);
    this.page = { endereco: page.slug, titulo: page.title, conteudo: page.html, publicada: page.published, ordem: page.position };
  }

  protected newPage(): void {
    this.editing.set(null);
    this.confirmingDelete.set(false);
    this.page = { ...EMPTY_PAGE };
  }

  private show(theme: PlatformTheme): void {
    this.theme.set(theme);
    this.draft = { css: theme.css, cabecalho: theme.headerHtml, rodape: theme.footerHtml };
  }

  private showPreview(preview: ThemePreview, sample = ''): void {
    // Seguro: conteúdo limpo no servidor e iframe com sandbox vazio (sem script nem mesma origem).
    this.previewHtml.set(this.sanitizer.bypassSecurityTrustHtml(previewDocument(preview, sample)));
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

function escapeText(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}
