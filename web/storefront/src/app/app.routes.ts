import { Routes } from '@angular/router';
import { HomePage, ReturnPolicyPage } from './pages';

export const routes: Routes = [
  { path: '', component: HomePage },
  { path: 'trocas-e-devolucoes', component: ReturnPolicyPage },
  { path: '**', redirectTo: '' },
];
