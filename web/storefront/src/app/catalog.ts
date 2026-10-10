import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

export type PartCondition = 'new' | 'used' | 'refurbished';

export interface StorePartSummary {
  id: string;
  title: string;
  condition: PartCondition;
  price: number;
  available: number;
  coverPhotoId: string | null;
}

export interface StoreCompatibility {
  brand: string;
  model: string;
  engine: string;
  yearFrom: number;
  yearTo: number | null;
}

export interface StorePartView {
  id: string;
  internalCode: string;
  title: string;
  description: string;
  condition: PartCondition;
  price: number;
  available: number;
  photoIds: string[];
  oemCodes: string[];
  compatibilities: StoreCompatibility[];
  updatedAt: string;
}

export interface StorePartPage {
  items: StorePartSummary[];
  total: number;
  page: number;
  pageSize: number;
}

export interface VehicleOption {
  id: string;
  name: string;
}

/** Filtros da busca, no formato da URL da vitrine (/busca?q=&marca=&modelo=&ano=&pagina=). */
export interface SearchParams {
  q?: string;
  marca?: string;
  modelo?: string;
  ano?: number;
  pagina?: number;
}

export const CONDITION_LABELS: Record<PartCondition, string> = { new: 'Nova', used: 'Usada', refurbished: 'Recondicionada' };

/** schema.org/OfferItemCondition para o JSON-LD. */
export const CONDITION_SCHEMA: Record<PartCondition, string> = {
  new: 'https://schema.org/NewCondition',
  used: 'https://schema.org/UsedCondition',
  refurbished: 'https://schema.org/RefurbishedCondition',
};

export const PHOTO_SIZES = [300, 800, 1600] as const;

export function photoUrl(photoId: string, size: (typeof PHOTO_SIZES)[number]): string {
  return `/api/loja/fotos/${photoId}/${size}`;
}

/** srcset com as três versões publicadas (o navegador escolhe pela largura da tela). */
export function photoSrcset(photoId: string): string {
  return PHOTO_SIZES.map((size) => `${photoUrl(photoId, size)} ${size}w`).join(', ');
}

/** "Farol dianteiro – Gol G5 (2009/2012)" → "farol-dianteiro-gol-g5-2009-2012" (só para SEO; quem manda é o id). */
export function slugify(title: string): string {
  return title
    .normalize('NFD')
    .replace(/[̀-ͯ]/g, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 80)
    .replace(/-+$/g, '');
}

export function partPath(part: { id: string; title: string }): string {
  const slug = slugify(part.title);
  return `/peca/${part.id}${slug ? '-' + slug : ''}`;
}

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i;

/** Id da peça no início do trecho da URL; nulo se não houver um id válido. */
export function partIdFromSlug(slug: string): string | null {
  return UUID.exec(slug)?.[0].toLowerCase() ?? null;
}

/** Texto curto para meta description: sem quebras, cortado em palavra inteira. */
export function summarize(text: string, max = 155): string {
  const flat = text.replace(/\s+/g, ' ').trim();
  if (flat.length <= max) return flat;
  return flat.slice(0, flat.lastIndexOf(' ', max - 1)).trimEnd() + '…';
}

/** Catálogo da loja do domínio acessado (no SSR, a chamada vai para a API interna — ver apiInterceptor). */
@Injectable({ providedIn: 'root' })
export class CatalogApi {
  private readonly http = inject(HttpClient);

  search(params: SearchParams, pageSize = 24): Observable<StorePartPage> {
    let query = new HttpParams().set('pagina', params.pagina ?? 1).set('tamanho', pageSize);
    if (params.q?.trim()) query = query.set('busca', params.q.trim());
    if (params.modelo) query = query.set('modelo', params.modelo);
    if (params.modelo && params.ano) query = query.set('ano', params.ano);
    return this.http.get<StorePartPage>('/api/loja/pecas', { params: query });
  }

  get(id: string): Observable<StorePartView> {
    return this.http.get<StorePartView>(`/api/loja/pecas/${id}`);
  }

  brands(): Observable<VehicleOption[]> {
    return this.http.get<VehicleOption[]>('/api/loja/veiculos/marcas');
  }

  models(brandId: string): Observable<VehicleOption[]> {
    return this.http.get<VehicleOption[]>(`/api/loja/veiculos/marcas/${brandId}/modelos`);
  }
}
