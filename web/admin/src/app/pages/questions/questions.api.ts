import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { feedback } from '../../core/feedback';

export type QuestionStatus = 'pending' | 'answered' | 'closed';
export type QuestionKind = 'simple' | 'complex';
export type AnswerOrigin = 'panel' | 'automatic' | 'channel';

/** Pergunta de comprador num anúncio (RF31). O rascunho da IA é só sugestão: a loja revisa e envia. */
export interface BuyerQuestion {
  id: string;
  channel: string;
  externalItemId: string;
  partId: string | null;
  partTitle: string | null;
  text: string;
  askedAt: string;
  status: QuestionStatus;
  draftText: string | null;
  kind: QuestionKind | null;
  draftNote: string | null;
  answerText: string | null;
  answeredVia: AnswerOrigin | null;
  answeredAt: string | null;
  lastError: string | null;
}

export const ANSWER_ORIGIN_LABELS: Record<AnswerOrigin, string> = {
  panel: 'pelo painel',
  automatic: 'automaticamente',
  channel: 'no Mercado Livre',
};

@Injectable({ providedIn: 'root' })
export class QuestionsApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/painel/perguntas';

  list(all = false): Promise<BuyerQuestion[]> {
    return firstValueFrom(this.http.get<BuyerQuestion[]>(this.base, { params: all ? { situacao: 'todas' } : {} }));
  }

  answer(id: string, texto: string): Promise<BuyerQuestion> {
    return firstValueFrom(this.http.post<BuyerQuestion>(`${this.base}/${id}/responder`, { texto }, { context: feedback('Resposta enviada.') }));
  }

  redraft(id: string): Promise<BuyerQuestion> {
    return firstValueFrom(this.http.post<BuyerQuestion>(`${this.base}/${id}/rascunho`, {}, { context: feedback(null) }));
  }

  async settings(): Promise<boolean> {
    return (await firstValueFrom(this.http.get<{ autoAnswer: boolean }>(`${this.base}/configuracao`))).autoAnswer;
  }

  async setAutoAnswer(automatica: boolean): Promise<boolean> {
    return (await firstValueFrom(this.http.put<{ autoAnswer: boolean }>(`${this.base}/configuracao`, { automatica }, { context: feedback('Configuração salva.') }))).autoAnswer;
  }
}
