import { Routes } from '@angular/router';
import { authGuard, passwordChangedGuard, permissionGuard, tenantGuard } from './core/guards';
import { Shell } from './layout/shell';
import { ChangePasswordPage } from './pages/change-password/change-password';
import { ChooseStorePage } from './pages/choose-store/choose-store';
import { CounterPage } from './pages/counter/counter';
import { DonorFormPage } from './pages/donors/donor-form';
import { DonorsListPage } from './pages/donors/donors-list';
import { HomePage } from './pages/home/home';
import { IntegrationsPage } from './pages/integrations/integrations';
import { LoginPage } from './pages/login/login';
import { OrdersPage } from './pages/orders/orders';
import { PartFormPage } from './pages/parts/part-form';
import { PartImportPage } from './pages/parts/part-import';
import { PartsListPage } from './pages/parts/parts-list';
import { PaymentsPage } from './pages/payments/payments';
import { StorePage } from './pages/store/store';
import { UsersPage } from './pages/users/users';

export const routes: Routes = [
  { path: 'entrar', component: LoginPage, title: 'Entrar' },
  { path: 'trocar-senha', component: ChangePasswordPage, canActivate: [authGuard], title: 'Trocar senha' },
  {
    path: 'escolher-loja',
    component: ChooseStorePage,
    canActivate: [authGuard, passwordChangedGuard],
    title: 'Escolher loja',
  },
  {
    path: '',
    component: Shell,
    canActivate: [authGuard, passwordChangedGuard, tenantGuard],
    children: [
      { path: '', component: HomePage, title: 'Painel' },
      { path: 'pecas', component: PartsListPage, canActivate: [permissionGuard('catalogManage')], title: 'Peças' },
      { path: 'pecas/importar', component: PartImportPage, canActivate: [permissionGuard('catalogManage')], title: 'Importar peças' },
      { path: 'pecas/nova', component: PartFormPage, canActivate: [permissionGuard('catalogManage')], title: 'Nova peça' },
      { path: 'pecas/:id', component: PartFormPage, canActivate: [permissionGuard('catalogManage')], title: 'Peça' },
      { path: 'doadores', component: DonorsListPage, canActivate: [permissionGuard('catalogManage')], title: 'Veículos doadores' },
      { path: 'doadores/novo', component: DonorFormPage, canActivate: [permissionGuard('catalogManage')], title: 'Novo veículo doador' },
      { path: 'doadores/:id', component: DonorFormPage, canActivate: [permissionGuard('catalogManage')], title: 'Veículo doador' },
      { path: 'pedidos', component: OrdersPage, canActivate: [permissionGuard('ordersManage')], title: 'Pedidos' },
      { path: 'balcao', component: CounterPage, canActivate: [permissionGuard('ordersManage')], title: 'Venda de balcão' },
      { path: 'integracoes', component: IntegrationsPage, canActivate: [permissionGuard('ordersManage')], title: 'Integrações' },
      { path: 'pagamentos', component: PaymentsPage, canActivate: [permissionGuard('vaultManage')], title: 'Pagamentos' },
      { path: 'loja', component: StorePage, canActivate: [permissionGuard('storeManage')], title: 'Dados da loja' },
      { path: 'usuarios', component: UsersPage, canActivate: [permissionGuard('usersManage')], title: 'Usuários' },
    ],
  },
  { path: '**', redirectTo: '' },
];
