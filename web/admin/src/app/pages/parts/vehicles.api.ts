import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { PartView } from './parts.api';

export interface VehicleOption {
  id: string;
  name: string;
}

export interface VehicleVersionOption {
  id: string;
  engine: string;
  yearFrom: number;
  yearTo: number | null;
  discontinued: boolean;
}

export interface CompatibilityView {
  id: string;
  vehicleVersionId: string;
  brand: string;
  model: string;
  engine: string;
  yearFrom: number;
  yearTo: number | null;
  narrowed: boolean;
  discontinued: boolean;
  /** Veio do cadastro por foto (RF30). */
  suggestedByAi?: boolean;
}

/** "2009–2012" ou "2020–atual". */
export function yearRange(from: number, to: number | null): string {
  return `${from}–${to ?? 'atual'}`;
}

/** Tabela de veículos da plataforma (seleção em cascata) e compatibilidades da peça (RF09). */
@Injectable({ providedIn: 'root' })
export class VehiclesApi {
  private readonly http = inject(HttpClient);

  brands(): Promise<VehicleOption[]> {
    return firstValueFrom(this.http.get<VehicleOption[]>('/api/painel/veiculos/marcas'));
  }

  models(brandId: string): Promise<VehicleOption[]> {
    return firstValueFrom(this.http.get<VehicleOption[]>(`/api/painel/veiculos/marcas/${brandId}/modelos`));
  }

  versions(modelId: string): Promise<VehicleVersionOption[]> {
    return firstValueFrom(this.http.get<VehicleVersionOption[]>(`/api/painel/veiculos/modelos/${modelId}/versoes`));
  }

  /** `suggestedByAi`: veículo sugerido no cadastro por foto e mantido pela pessoa (RF30). */
  addCompatibility(partId: string, vehicleVersionId: string, yearFrom: number | null, yearTo: number | null, suggestedByAi = false): Promise<PartView> {
    const body = suggestedByAi ? { vehicleVersionId, yearFrom, yearTo, suggestedByAi } : { vehicleVersionId, yearFrom, yearTo };
    return firstValueFrom(this.http.post<PartView>(`/api/painel/pecas/${partId}/compatibilidades`, body));
  }

  removeCompatibility(partId: string, compatibilityId: string): Promise<PartView> {
    return firstValueFrom(this.http.delete<PartView>(`/api/painel/pecas/${partId}/compatibilidades/${compatibilityId}`));
  }
}
