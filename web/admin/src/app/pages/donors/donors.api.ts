import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { PartStatus, PartView } from '../parts/parts.api';

/** Dados do veículo doador como o formulário envia (a API normaliza e confere placa, chassi, Renavam e chave da NF). */
export interface DonorVehicleRequest {
  plate: string | null;
  chassis: string | null;
  renavam: string | null;
  brand: string;
  model: string;
  modelYear: number | null;
  color: string | null;
  deregistrationCertificate: string | null;
  entryInvoiceKey: string | null;
  entryDate: string | null;
  notes: string | null;
}

export interface DonorVehicleView extends Omit<DonorVehicleRequest, 'brand' | 'model'> {
  id: string;
  label: string;
  brand: string;
  model: string;
  missingForDismantler: string[];
  partCount: number;
  parts: { id: string; internalCode: string; title: string; status: PartStatus; traceabilityTag: string | null }[];
  createdAt: string;
}

export interface DonorVehiclePage {
  items: DonorVehicleView[];
  total: number;
  page: number;
  pageSize: number;
}

export interface TraceabilityField {
  key: string;
  label: string;
  required: boolean;
}

export interface TraceabilitySettings {
  dismantlerMode: boolean;
  fields: TraceabilityField[];
}

/** Rastreabilidade de peça usada (RF10): veículos doadores, configuração da loja e origem de cada peça. Só no painel. */
@Injectable({ providedIn: 'root' })
export class DonorsApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/painel/doadores';

  search(text: string, page = 1, pageSize = 25): Promise<DonorVehiclePage> {
    let params = new HttpParams().set('pagina', page).set('tamanho', pageSize);
    if (text.trim()) params = params.set('busca', text.trim());
    return firstValueFrom(this.http.get<DonorVehiclePage>(this.base, { params }));
  }

  get(id: string): Promise<DonorVehicleView> {
    return firstValueFrom(this.http.get<DonorVehicleView>(`${this.base}/${id}`));
  }

  create(request: DonorVehicleRequest): Promise<DonorVehicleView> {
    return firstValueFrom(this.http.post<DonorVehicleView>(this.base, request));
  }

  update(id: string, request: DonorVehicleRequest): Promise<DonorVehicleView> {
    return firstValueFrom(this.http.put<DonorVehicleView>(`${this.base}/${id}`, request));
  }

  remove(id: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`${this.base}/${id}`));
  }

  settings(): Promise<TraceabilitySettings> {
    return firstValueFrom(this.http.get<TraceabilitySettings>('/api/painel/rastreabilidade'));
  }

  saveSettings(settings: TraceabilitySettings): Promise<TraceabilitySettings> {
    return firstValueFrom(this.http.put<TraceabilitySettings>('/api/painel/rastreabilidade', settings));
  }

  setPartTraceability(partId: string, body: { donorVehicleId: string | null; tag: string | null; customFields: Record<string, string> }): Promise<PartView> {
    return firstValueFrom(this.http.put<PartView>(`/api/painel/pecas/${partId}/rastreabilidade`, body));
  }
}
