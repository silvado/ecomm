import { DOCUMENT } from '@angular/common';
import { Component, inject, input, OnDestroy, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { problemTitle } from '../../core/auth';
import { FISCAL_STATUS, FiscalApi, FiscalDocument, FiscalRecipient, InvoiceDraft, UF } from './fiscal.api';

const WHEN = new Intl.DateTimeFormat('pt-BR', { timeZone: 'America/Sao_Paulo', dateStyle: 'short', timeStyle: 'short' });

/**
 * NF-e do pedido (RF19): emitir pelo provedor (pede o que faltar do destinatário — retirada e balcão não têm endereço),
 * acompanhar até autorizada, baixar XML/DANFE, cancelar no prazo ou registrar a nota emitida fora.
 */
@Component({
  selector: 'app-order-invoice',
  imports: [FormsModule],
  template: `
    <section data-order-invoice>
      <h3>Nota fiscal</h3>
      @if (draft(); as d) {
        @for (doc of d.documents; track doc.id) {
          <div class="invoice" [attr.data-invoice]="doc.status">
            <p>
              <strong>{{ statusLabel[doc.status] }}</strong>
              @if (doc.number) {
                · NF-e nº {{ doc.number }} série {{ doc.series }}
              }
              @if (doc.source === 'external') {
                · emitida fora da plataforma
              }
              @if (doc.environment === 'homologation') {
                · <span class="warn">homologação (sem valor fiscal)</span>
              }
              <br /><span class="muted">{{ when(doc.requestedAt) }}</span>
            </p>
            @if (doc.accessKey) {
              <p class="muted">Chave: <span class="mono" data-key>{{ doc.accessKey }}</span></p>
            }
            @if (doc.status === 'processing') {
              <p class="muted">Aguardando a SEFAZ. <button type="button" class="link" (click)="load()">Atualizar</button></p>
            }
            @if (doc.status === 'rejected') {
              <p class="error" data-rejection>{{ doc.message }}</p>
            }
            @if (doc.status === 'canceled') {
              <p class="muted">Cancelada: {{ doc.cancelJustification }}</p>
            }
            @if (doc.status === 'authorized') {
              <div class="order-actions">
                @if (doc.hasDanfe) {
                  <button type="button" (click)="download(doc, 'danfe')">Baixar DANFE</button>
                }
                @if (doc.hasXml) {
                  <button type="button" (click)="download(doc, 'xml')">Baixar XML</button>
                }
                @if (doc.canCancel && cancelingId() !== doc.id) {
                  <button type="button" class="link danger" (click)="cancelingId.set(doc.id)" data-cancel-invoice>
                    {{ doc.source === 'external' ? 'Remover registro' : 'Cancelar NF-e' }}
                  </button>
                }
              </div>
              @if (doc.source === 'provider' && !doc.canCancel) {
                <p class="muted">Passou o prazo de 24 horas para cancelar. Para desfazer a venda, emita uma NF-e de devolução.</p>
              } @else if (doc.cancelDeadline) {
                <p class="muted">Pode ser cancelada até {{ when(doc.cancelDeadline) }}.</p>
              }
              @if (cancelingId() === doc.id) {
                <label>
                  Justificativa (15 a 255 caracteres)
                  <textarea [(ngModel)]="justification" name="justification" maxlength="255" rows="2" data-justification></textarea>
                </label>
                <div class="order-actions">
                  <button type="button" class="danger" (click)="cancel(doc)" [disabled]="busy()" data-confirm-cancel>Confirmar cancelamento</button>
                  <button type="button" class="link" (click)="cancelingId.set(null)">Voltar</button>
                </div>
              }
            }
          </div>
        }

        @if (!hasActive(d)) {
          @switch (d.mode) {
            @case ('provider') {
              @if (d.problems.length) {
                <ul class="warn" data-problems>
                  @for (p of d.problems; track p) {
                    <li>{{ p }}</li>
                  }
                </ul>
              } @else if (!editing()) {
                <div class="order-actions">
                  <button type="button" (click)="startEmit()" data-start-emit>{{ d.documents.length ? 'Emitir de novo' : 'Emitir NF-e' }}</button>
                </div>
              } @else {
                <fieldset data-recipient>
                  <legend>Destinatário</legend>
                  <div class="grid-3">
                    <label>Nome <input [(ngModel)]="r.name" name="rname" maxlength="60" /></label>
                    <label>CPF ou CNPJ <input [(ngModel)]="r.document" name="rdoc" maxlength="18" data-recipient-document /></label>
                    <label>E-mail <input type="email" [(ngModel)]="r.email" name="remail" maxlength="254" /></label>
                    <label>CEP <input [(ngModel)]="a.postalCode" name="cep" maxlength="9" data-recipient-cep /></label>
                    <label>Rua <input [(ngModel)]="a.street" name="street" maxlength="120" /></label>
                    <label>Número <input [(ngModel)]="a.number" name="number" maxlength="20" /></label>
                    <label>Complemento <input [(ngModel)]="a.complement" name="complement" maxlength="60" /></label>
                    <label>Bairro <input [(ngModel)]="a.district" name="district" maxlength="60" /></label>
                    <label>Cidade <input [(ngModel)]="a.city" name="city" maxlength="60" /></label>
                    <label>
                      UF
                      <select [(ngModel)]="a.state" name="uf">
                        <option value="">—</option>
                        @for (uf of ufs; track uf) {
                          <option [value]="uf">{{ uf }}</option>
                        }
                      </select>
                    </label>
                  </div>
                </fieldset>
                <div class="order-actions">
                  <button type="button" (click)="emit()" [disabled]="busy()" data-emit>Emitir NF-e</button>
                  <button type="button" class="link" (click)="editing.set(false)">Voltar</button>
                </div>
              }
            }
            @case ('external') {
              <p class="muted">A loja emite a NF-e fora da plataforma: informe a chave de acesso ou anexe o XML.</p>
              <div class="order-actions">
                <input [(ngModel)]="accessKey" name="accessKey" maxlength="60" placeholder="Chave de acesso (44 números)" aria-label="Chave de acesso" data-access-key />
                <input type="file" accept=".xml,application/xml,text/xml" (change)="pickXml($event)" aria-label="XML da NF-e" data-xml-file />
                <button type="button" (click)="registerExternal()" [disabled]="busy() || (!accessKey.trim() && !xml)" data-register-external>Registrar NF-e</button>
              </div>
            }
            @default {
              <p class="muted">A emissão de NF-e está desligada. O Dono liga em Dados da loja → Nota fiscal.</p>
            }
          }
        }
        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }
      }
    </section>
  `,
})
export class OrderInvoice implements OnInit, OnDestroy {
  readonly orderId = input.required<string>();

  private readonly api = inject(FiscalApi);
  private readonly document = inject(DOCUMENT);
  private poll?: ReturnType<typeof setTimeout>;

  protected readonly statusLabel = FISCAL_STATUS;
  protected readonly ufs = UF;
  protected readonly draft = signal<InvoiceDraft | null>(null);
  protected readonly editing = signal(false);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly cancelingId = signal<string | null>(null);
  protected r: { name: string; document: string; email: string } = { name: '', document: '', email: '' };
  protected a = { postalCode: '', street: '', number: '', complement: '', district: '', city: '', state: '' };
  protected justification = '';
  protected accessKey = '';
  protected xml: string | null = null;
  protected readonly when = (iso: string) => WHEN.format(new Date(iso));

  ngOnInit(): void {
    void this.load();
  }

  ngOnDestroy(): void {
    clearTimeout(this.poll);
  }

  protected hasActive(d: InvoiceDraft): boolean {
    return d.documents.some((doc) => doc.status === 'processing' || doc.status === 'authorized');
  }

  async load(): Promise<void> {
    clearTimeout(this.poll);
    try {
      const draft = await this.api.draft(this.orderId());
      this.draft.set(draft);
      // Em processamento: confere de novo sozinho por alguns segundos.
      if (draft.documents.some((d) => d.status === 'processing')) this.poll = setTimeout(() => void this.load(), 3000);
    } catch (error) {
      this.error.set(problemTitle(error));
    }
  }

  protected startEmit(): void {
    const s: FiscalRecipient | null = this.draft()?.recipient ?? null;
    this.r = { name: s?.name ?? '', document: s?.document ?? '', email: s?.email ?? '' };
    const addr = s?.address;
    this.a = {
      postalCode: addr?.postalCode ?? '', street: addr?.street ?? '', number: addr?.number ?? '', complement: addr?.complement ?? '',
      district: addr?.district ?? '', city: addr?.city ?? '', state: addr?.state ?? '',
    };
    this.editing.set(true);
  }

  protected async emit(): Promise<void> {
    await this.run(async () => {
      this.draft.set(
        await this.api.emit(this.orderId(), {
          name: this.r.name, document: this.r.document, email: this.r.email.trim() || null,
          address: { ...this.a, complement: this.a.complement.trim() || null },
        }),
      );
      this.editing.set(false);
      void this.load();
    });
  }

  protected async cancel(doc: FiscalDocument): Promise<void> {
    await this.run(async () => {
      this.draft.set(await this.api.cancel(this.orderId(), doc.id, this.justification));
      this.cancelingId.set(null);
      this.justification = '';
    });
  }

  protected async pickXml(event: Event): Promise<void> {
    const file = (event.target as HTMLInputElement).files?.[0];
    this.xml = null;
    if (!file) return;
    if (file.size > 1024 * 1024) {
      this.error.set('XML acima de 1 MB.');
      return;
    }
    const bytes = new Uint8Array(await file.arrayBuffer());
    let binary = '';
    for (const b of bytes) binary += String.fromCharCode(b);
    this.xml = btoa(binary);
  }

  protected async registerExternal(): Promise<void> {
    await this.run(async () => {
      this.draft.set(await this.api.registerExternal(this.orderId(), this.accessKey.trim() || null, this.xml));
      this.accessKey = '';
      this.xml = null;
    });
  }

  protected async download(doc: FiscalDocument, file: 'xml' | 'danfe'): Promise<void> {
    await this.run(async () => {
      const blob = await this.api.download(this.orderId(), doc.id, file);
      const url = URL.createObjectURL(blob);
      const link = this.document.createElement('a');
      link.href = url;
      link.download = `NFe${doc.accessKey ?? doc.id}.${file === 'xml' ? 'xml' : 'pdf'}`;
      link.click();
      URL.revokeObjectURL(url);
    });
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
