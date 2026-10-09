import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import type { CompatibilityView } from './vehicles.api';

export type PartCondition = 'new' | 'used' | 'refurbished';
export type PartStatus = 'draft' | 'active' | 'inactive';

export interface StockView {
  onHand: number;
  reserved: number;
  available: number;
}

export interface PartSummary {
  id: string;
  internalCode: string;
  title: string;
  condition: PartCondition;
  price: number;
  status: PartStatus;
  stock: StockView;
  hasShippingDimensions: boolean;
  updatedAt: string;
  coverPhotoId: string | null;
}

export interface PhotoView {
  id: string;
  position: number;
}

export interface PartView extends PartSummary {
  description: string;
  lengthCm: number | null;
  widthCm: number | null;
  heightCm: number | null;
  weightG: number | null;
  oemCodes: string[];
  createdAt: string;
  photos: PhotoView[];
  compatibilities: CompatibilityView[];
}

export interface PartPage {
  items: PartSummary[];
  total: number;
  page: number;
  pageSize: number;
}

export interface PartRequest {
  internalCode?: string;
  title: string;
  description: string;
  condition: PartCondition;
  price: number;
  lengthCm: number | null;
  widthCm: number | null;
  heightCm: number | null;
  weightG: number | null;
  oemCodes: string[];
  quantity: number;
}

export const CONDITION_LABELS: Record<PartCondition, string> = { new: 'Nova', used: 'Usada', refurbished: 'Recondicionada' };
export const STATUS_LABELS: Record<PartStatus, string> = { draft: 'Rascunho', active: 'Ativa', inactive: 'Inativa' };

/** Um código OEM por linha ou separados por vírgula/ponto e vírgula; a API normaliza (maiúsculas, sem espaços e hífens). */
export function parseOemCodes(text: string): string[] {
  return text
    .split(/[\n,;]+/)
    .map((code) => code.trim())
    .filter((code) => code.length > 0);
}

@Injectable({ providedIn: 'root' })
export class PartsApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/painel/pecas';

  /** Veículo opcional: modelo (e ano) filtram pelas compatibilidades. */
  search(search: string, status: PartStatus | '', page: number, pageSize = 25, vehicle?: { modelId: string; year: number | null }): Promise<PartPage> {
    let params = new HttpParams().set('pagina', page).set('tamanho', pageSize);
    if (vehicle?.modelId) params = params.set('modelo', vehicle.modelId);
    if (vehicle?.modelId && vehicle.year) params = params.set('ano', vehicle.year);
    if (search.trim()) params = params.set('busca', search.trim());
    if (status) params = params.set('situacao', status);
    return firstValueFrom(this.http.get<PartPage>(this.base, { params }));
  }

  get(id: string): Promise<PartView> {
    return firstValueFrom(this.http.get<PartView>(`${this.base}/${id}`));
  }

  create(request: PartRequest): Promise<PartView> {
    return firstValueFrom(this.http.post<PartView>(this.base, request));
  }

  update(id: string, request: PartRequest): Promise<PartView> {
    return firstValueFrom(this.http.put<PartView>(`${this.base}/${id}`, request));
  }

  /** Bytes da imagem no corpo; a API reconhece o formato pelo conteúdo e gera os WebP. */
  addPhoto(id: string, file: Blob): Promise<PartView> {
    return firstValueFrom(this.http.post<PartView>(`//fotos`, file));
  }

  removePhoto(id: string, photoId: string): Promise<PartView> {
    return firstValueFrom(this.http.delete<PartView>(`//fotos/`));
  }

  reorderPhotos(id: string, photoIds: string[]): Promise<PartView> {
    return firstValueFrom(this.http.put<PartView>(`//fotos/ordem`, { photoIds }));
  }

  setStatus(id: string, status: PartStatus): Promise<PartView> {
    return firstValueFrom(this.http.put<PartView>(`${this.base}/${id}/situacao`, { status }));
  }
}
