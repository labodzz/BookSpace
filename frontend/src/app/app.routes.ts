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
    // Public token route, including for authenticated browsers. The component requires an explicit
    // logout before showing the acceptance form; guestGuard would discard the invitation URL.
    path: 'accept-invitation',
    loadComponent: () => import('./features/auth/accept-invitation/accept-invitation').then((m) => m.AcceptInvitationComponent),
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
        // Must come before 'resources/:id' - otherwise ':id' would greedily match the literal "new".
        path: 'resources/new',
        canActivate: [roleGuard('TenantAdmin')],
        loadComponent: () => import('./features/resources/resource-form/resource-form').then((m) => m.ResourceFormComponent),
      },
      {
        path: 'resources/:id',
        loadComponent: () => import('./features/resources/resource-detail/resource-detail').then((m) => m.ResourceDetailComponent),
      },
      {
        path: 'resources/:id/edit',
        canActivate: [roleGuard('TenantAdmin')],
        loadComponent: () => import('./features/resources/resource-form/resource-form').then((m) => m.ResourceFormComponent),
      },
      {
        path: 'resources/:id/availability',
        loadComponent: () =>
          import('./features/resources/resource-availability/resource-availability').then((m) => m.ResourceAvailabilityComponent),
      },
      {
        path: 'resources/:id/manage-availability',
        canActivate: [roleGuard('TenantAdmin')],
        loadComponent: () =>
          import('./features/resources/resource-availability-manage/resource-availability-manage').then(
            (m) => m.ResourceAvailabilityManageComponent,
          ),
      },
      {
        path: 'resources/:id/manage-approvers',
        canActivate: [roleGuard('TenantAdmin')],
        loadComponent: () =>
          import('./features/resources/resource-approvers-manage/resource-approvers-manage').then(
            (m) => m.ResourceApproversManageComponent,
          ),
      },
      {
        path: 'users',
        canActivate: [roleGuard('TenantAdmin')],
        loadComponent: () => import('./features/users/user-list/user-list').then((m) => m.UserListComponent),
      },
      {
        path: 'users/:id',
        canActivate: [roleGuard('TenantAdmin')],
        loadComponent: () => import('./features/users/user-detail/user-detail').then((m) => m.UserDetailComponent),
      },
      {
        path: 'resource-types',
        canActivate: [roleGuard('TenantAdmin')],
        loadComponent: () =>
          import('./features/resource-types/resource-type-list/resource-type-list').then((m) => m.ResourceTypeListComponent),
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
        // SysAdmin only - every tenant-lifecycle route is, since this is platform administration, not a
        // tenant's own business function. TenantAdmin never gets access here, not even to its own tenant
        // (see TenantsController's own doc comment and docs/tenant-onboarding.md, "Permissions matrix").
        path: 'tenants',
        canActivate: [roleGuard('SysAdmin')],
        loadComponent: () => import('./features/tenants/tenant-list/tenant-list').then((m) => m.TenantListComponent),
      },
      {
        // Must come before 'tenants/:id' - otherwise ':id' would greedily match the literal "new".
        path: 'tenants/new',
        canActivate: [roleGuard('SysAdmin')],
        loadComponent: () => import('./features/tenants/tenant-form/tenant-form').then((m) => m.TenantFormComponent),
      },
      {
        path: 'tenants/:id',
        canActivate: [roleGuard('SysAdmin')],
        loadComponent: () => import('./features/tenants/tenant-detail/tenant-detail').then((m) => m.TenantDetailComponent),
      },
      {
        // SysAdmin deliberately excluded - a global SysAdmin is not a tenant member and must not approve/
        // reject bookings (see ApprovalAuthorization and BookingsController's own [Authorize]).
        path: 'approvals',
        canActivate: [roleGuard('Approver', 'TenantAdmin')],
        loadComponent: () => import('./features/approvals/approval-queue/approval-queue').then((m) => m.ApprovalQueueComponent),
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
