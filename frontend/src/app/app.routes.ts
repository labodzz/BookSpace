import { Routes } from '@angular/router';
import { authGuard } from './core/auth/auth.guard';
import { guestGuard } from './core/auth/guest.guard';
import { roleGuard } from './core/auth/role.guard';

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
      {
        path: 'users',
        canActivate: [roleGuard('TenantAdmin', 'SysAdmin')],
        loadComponent: () => import('./features/users/user-list/user-list').then((m) => m.UserListComponent),
      },
      {
        path: 'bookings',
        loadComponent: () => import('./features/bookings/my-bookings/my-bookings').then((m) => m.MyBookingsComponent),
      },
      {
        path: 'bookings/new',
        loadComponent: () => import('./features/bookings/booking-form/booking-form').then((m) => m.BookingFormComponent),
      },
      {
        path: 'bookings/new-recurring',
        loadComponent: () =>
          import('./features/bookings/recurring-booking-form/recurring-booking-form').then((m) => m.RecurringBookingFormComponent),
      },
      {
        path: 'calendar',
        loadComponent: () => import('./features/calendar/calendar').then((m) => m.CalendarComponent),
      },
      {
        path: 'approvals',
        canActivate: [roleGuard('Approver', 'TenantAdmin', 'SysAdmin')],
        loadComponent: () => import('./features/approvals/approval-queue/approval-queue').then((m) => m.ApprovalQueueComponent),
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
