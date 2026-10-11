import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export type PartImportStatus = 'validated' | 'processing' | 'completed';

export interface PartImportView {
  id: string;
  fileName: string;
  status: PartImportStatus;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  totalRows: number;
  validRows: number;
  errorCount: number;
  existingCodesAtPreview: number;
  processedRows: number;
  importedCount: number;
  skippedCount: number;
  errors: { line: number; message: string }[];
  skipped: { line: number; internalCode: string }[];
}

/** Importação de peças por planilha (RF11): modelo, prévia, confirmação e progresso. */
@Injectable({ providedIn: 'root' })
export class PartImportApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/painel/pecas/importacoes';

  template(format: 'xlsx' | 'csv'): Promise<Blob> {
    return firstValueFrom(this.http.get(`${this.base}/modelo`, { params: { formato: format }, responseType: 'blob' }));
  }

  /** Corpo = bytes do arquivo; a API reconhece XLSX ou CSV pelo conteúdo. */
  preview(file: File): Promise<PartImportView> {
    return firstValueFrom(this.http.post<PartImportView>(this.base, file, { params: new HttpParams().set('arquivo', file.name) }));
  }

  confirm(id: string): Promise<PartImportView> {
    return firstValueFrom(this.http.post<PartImportView>(`${this.base}/${id}/confirmar`, {}));
  }

  get(id: string): Promise<PartImportView> {
    return firstValueFrom(this.http.get<PartImportView>(`${this.base}/${id}`));
  }

  recent(): Promise<PartImportView[]> {
    return firstValueFrom(this.http.get<PartImportView[]>(this.base));
  }
}
