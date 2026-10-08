import { Pipe, PipeTransform } from '@angular/core';
import { Role } from '../core/auth';

const labels: Record<Role, string> = { owner: 'Dono', operator: 'Operador' };

@Pipe({ name: 'roleLabel' })
export class RoleLabelPipe implements PipeTransform {
  transform(role: Role | null | undefined): string {
    return role ? labels[role] : '';
  }
}
