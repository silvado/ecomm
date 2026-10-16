import { Routes } from '@angular/router';
import { CartPage } from './cart-page';
import { CustomPage, customPageResolver } from './custom-theme';
import { LegalPage, legalTextsResolver } from './legal';
import { MyOrdersPage } from './my-orders';
import { OrderPage } from './order-page';
import { HomePage, PartPage, ReturnPolicyPage, SearchPage } from './pages';
import { partResolver, recentPartsResolver, searchResolver } from './resolvers';

export const routes: Routes = [
  { path: '', component: HomePage, resolve: { recent: recentPartsResolver } },
  // Filtros na URL (?q=&marca=&modelo=&ano=&pagina=): mudar a busca refaz a consulta.
  { path: 'busca', component: SearchPage, resolve: { result: searchResolver }, runGuardsAndResolvers: 'paramsOrQueryParamsChange' },
  { path: 'peca/:slug', component: PartPage, resolve: { part: partResolver } },
  { path: 'carrinho', component: CartPage },
  // Token no fragmento (#): nunca chega ao servidor nem aos logs.
  { path: 'pedido', component: OrderPage },
  // Área do cliente (RF16): o link do e-mail também traz o token no fragmento.
  { path: 'meus-pedidos', component: MyOrdersPage },
  { path: 'trocas-e-devolucoes', component: ReturnPolicyPage },
  // RNF04: textos da loja ou o modelo padrão.
  { path: 'privacidade', component: LegalPage, data: { kind: 'privacy' }, resolve: { texts: legalTextsResolver } },
  { path: 'termos', component: LegalPage, data: { kind: 'terms' }, resolve: { texts: legalTextsResolver } },
  // RF40 CA1: páginas exclusivas da loja personalizada.
  { path: 'p/:slug', component: CustomPage, resolve: { page: customPageResolver } },
  { path: '**', redirectTo: '' },
];
