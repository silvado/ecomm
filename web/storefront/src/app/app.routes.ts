import { Routes } from '@angular/router';
import { CartPage } from './cart-page';
import { HomePage, PartPage, ReturnPolicyPage, SearchPage } from './pages';
import { partResolver, recentPartsResolver, searchResolver } from './resolvers';

export const routes: Routes = [
  { path: '', component: HomePage, resolve: { recent: recentPartsResolver } },
  // Filtros na URL (?q=&marca=&modelo=&ano=&pagina=): mudar a busca refaz a consulta.
  { path: 'busca', component: SearchPage, resolve: { result: searchResolver }, runGuardsAndResolvers: 'paramsOrQueryParamsChange' },
  { path: 'peca/:slug', component: PartPage, resolve: { part: partResolver } },
  { path: 'carrinho', component: CartPage },
  { path: 'trocas-e-devolucoes', component: ReturnPolicyPage },
  { path: '**', redirectTo: '' },
];
