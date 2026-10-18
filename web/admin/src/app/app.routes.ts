import { Routes } from '@angular/router';
import { authGuard, passwordChangedGuard, permissionGuard, tenantGuard } from './core/guards';
import { Shell } from './layout/shell';
import { ChangePasswordPage } from './pages/change-password/change-password';
import { ChooseStorePage } from './pages/choose-store/choose-store';
import { HomePage } from './pages/home/home';
import { LoginPage } from './pages/login/login';
import { platformGuard } from './platform/platform-auth';

// Páginas do painel carregadas sob demanda: o bundle inicial fica só com login, moldura e início.
export const routes: Routes = [
  { path: 'entrar', component: LoginPage, title: 'Entrar' },
  // Painel da plataforma (superadmin, ADR-0007): login, sessão e moldura próprios; nada da sessão de loja.
  { path: 'plataforma/entrar', loadComponent: () => import('./platform/platform-login').then((m) => m.PlatformLoginPage), title: 'Entrar na plataforma' },
  {
    path: 'plataforma',
    loadComponent: () => import('./platform/platform-shell').then((m) => m.PlatformShell),
    canActivate: [platformGuard],
    children: [
      { path: '', loadComponent: () => import('./platform/tenants').then((m) => m.PlatformTenantsPage), title: 'Lojas — plataforma' },
      { path: 'lojas/:id', loadComponent: () => import('./platform/tenant-detail').then((m) => m.PlatformTenantPage), title: 'Loja — plataforma' },
      { path: 'lojas/:id/tema', loadComponent: () => import('./platform/theme-editor').then((m) => m.PlatformThemePage), title: 'Tema exclusivo — plataforma' },
      { path: 'planos', loadComponent: () => import('./platform/plans-audit').then((m) => m.PlatformPlansPage), title: 'Planos — plataforma' },
      { path: 'auditoria', loadComponent: () => import('./platform/plans-audit').then((m) => m.PlatformAuditPage), title: 'Auditoria — plataforma' },
    ],
  },
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
      { path: 'pecas', loadComponent: () => import('./pages/parts/parts-list').then((m) => m.PartsListPage), canActivate: [permissionGuard('catalogManage')], title: 'Peças' },
      { path: 'pecas/importar', loadComponent: () => import('./pages/parts/part-import').then((m) => m.PartImportPage), canActivate: [permissionGuard('catalogManage')], title: 'Importar peças' },
      { path: 'pecas/nova', loadComponent: () => import('./pages/parts/part-form').then((m) => m.PartFormPage), canActivate: [permissionGuard('catalogManage')], title: 'Nova peça' },
      { path: 'pecas/:id', loadComponent: () => import('./pages/parts/part-form').then((m) => m.PartFormPage), canActivate: [permissionGuard('catalogManage')], title: 'Peça' },
      { path: 'doadores', loadComponent: () => import('./pages/donors/donors-list').then((m) => m.DonorsListPage), canActivate: [permissionGuard('catalogManage')], title: 'Veículos doadores' },
      { path: 'doadores/novo', loadComponent: () => import('./pages/donors/donor-form').then((m) => m.DonorFormPage), canActivate: [permissionGuard('catalogManage')], title: 'Novo veículo doador' },
      { path: 'doadores/:id', loadComponent: () => import('./pages/donors/donor-form').then((m) => m.DonorFormPage), canActivate: [permissionGuard('catalogManage')], title: 'Veículo doador' },
      { path: 'pedidos', loadComponent: () => import('./pages/orders/orders').then((m) => m.OrdersPage), canActivate: [permissionGuard('ordersManage')], title: 'Pedidos' },
      { path: 'balcao', loadComponent: () => import('./pages/counter/counter').then((m) => m.CounterPage), canActivate: [permissionGuard('ordersManage')], title: 'Venda de balcão' },
      { path: 'perguntas', loadComponent: () => import('./pages/questions/questions').then((m) => m.QuestionsPage), canActivate: [permissionGuard('supportManage')], title: 'Perguntas' },
      { path: 'integracoes', loadComponent: () => import('./pages/integrations/integrations').then((m) => m.IntegrationsPage), canActivate: [permissionGuard('ordersManage')], title: 'Integrações' },
      { path: 'pagamentos', loadComponent: () => import('./pages/payments/payments').then((m) => m.PaymentsPage), canActivate: [permissionGuard('vaultManage')], title: 'Pagamentos' },
      { path: 'cofre', loadComponent: () => import('./pages/vault/vault').then((m) => m.VaultPage), canActivate: [permissionGuard('vaultManage')], title: 'Cofre' },
      { path: 'primeiros-passos', loadComponent: () => import('./pages/onboarding/onboarding').then((m) => m.OnboardingPage), canActivate: [permissionGuard('storeManage')], title: 'Primeiros passos' },
      { path: 'canais', loadComponent: () => import('./pages/channels/channels').then((m) => m.ChannelsPage), canActivate: [permissionGuard('storeManage')], title: 'Canais de venda' },
      { path: 'canais/mercado-livre/retorno', loadComponent: () => import('./pages/channels/channel-return').then((m) => m.ChannelReturnPage), canActivate: [permissionGuard('storeManage')], title: 'Mercado Livre' },
      { path: 'loja', loadComponent: () => import('./pages/store/store').then((m) => m.StorePage), canActivate: [permissionGuard('storeManage')], title: 'Dados da loja' },
      { path: 'assinatura', loadComponent: () => import('./pages/subscription/subscription').then((m) => m.SubscriptionPage), canActivate: [permissionGuard('planManage')], title: 'Assinatura' },
      { path: 'privacidade', loadComponent: () => import('./pages/privacy/privacy').then((m) => m.PrivacyPage), canActivate: [permissionGuard('dataExport')], title: 'Privacidade' },
      { path: 'usuarios', loadComponent: () => import('./pages/users/users').then((m) => m.UsersPage), canActivate: [permissionGuard('usersManage')], title: 'Usuários' },
    ],
  },
  { path: '**', redirectTo: '' },
];
