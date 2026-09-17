import { Routes } from '@angular/router';
import { authGuard } from './core/auth/auth.guard';
import { guestGuard } from './core/auth/guest.guard';

export const routes: Routes = [
  {
    path: 'welcome',
    canActivate: [guestGuard],
    loadComponent: () => import('./features/auth/welcome/welcome').then((m) => m.WelcomeComponent),
  },
  {
    path: 'login',
    canActivate: [guestGuard],
    loadComponent: () => import('./features/auth/login/login').then((m) => m.LoginComponent),
  },
  {
    path: '',
    canActivate: [authGuard],
    loadComponent: () => import('./features/shell/shell').then((m) => m.ShellComponent),
    children: [
      {
        path: '',
        loadComponent: () => import('./features/dashboard/dashboard').then((m) => m.DashboardComponent),
      },
      {
        path: 'resources',
        loadComponent: () => import('./features/resources/resource-list/resource-list').then((m) => m.ResourceListComponent),
      },
      {
        path: 'resources/:id',
        loadComponent: () => import('./features/resources/resource-detail/resource-detail').then((m) => m.ResourceDetailComponent),
      },
      {
        path: 'resources/:id/availability',
        loadComponent: () =>
          import('./features/resources/resource-availability/resource-availability').then((m) => m.ResourceAvailabilityComponent),
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
