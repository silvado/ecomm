import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Role } from '../../core/auth';
import { feedback } from '../../core/feedback';

export interface StoreUser {
  userId: string;
  email: string;
  role: Role;
  lockedOut: boolean;
  mustChangePassword: boolean;
}

/** Usuários da loja atual — a loja vem do token; a API só aceita o Dono (permissão usersManage). */
@Injectable({ providedIn: 'root' })
export class UsersApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/painel/usuarios';

  list(): Promise<StoreUser[]> {
    return firstValueFrom(this.http.get<StoreUser[]>(this.base));
  }

  add(email: string, role: Role, temporaryPassword: string): Promise<StoreUser> {
    return firstValueFrom(this.http.post<StoreUser>(this.base, { email, role, temporaryPassword }, { context: feedback('Usuário criado.') }));
  }

  changeRole(userId: string, role: Role): Promise<void> {
    return firstValueFrom(this.http.put<void>(`${this.base}/${userId}`, { role }, { context: feedback('Perfil alterado.') }));
  }

  remove(userId: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`${this.base}/${userId}`, { context: feedback('Usuário removido.') }));
  }

  unlock(userId: string): Promise<void> {
    return firstValueFrom(this.http.post<void>(`${this.base}/${userId}/desbloquear`, null, { context: feedback('Usuário desbloqueado.') }));
  }
}
