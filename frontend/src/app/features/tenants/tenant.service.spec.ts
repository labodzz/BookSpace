import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { environment } from '../../../environments/environment';
import { TenantService } from './tenant.service';

const TENANTS_URL = `${environment.apiUrl}/tenants`;

describe('TenantService', () => {
  let service: TenantService;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(TenantService);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpTesting.verify());

  it('createTenant POSTs the request as-is and returns the created tenant/admin', () => {
    let result: unknown;
    service
      .createTenant({ name: 'Acme Coworking', defaultTimeZoneId: 'Europe/Zagreb', adminFirstName: 'Ana', adminLastName: 'Anic', adminEmail: 'ana@acme.test' })
      .subscribe((created) => (result = created));

    const req = httpTesting.expectOne((r) => r.url === TENANTS_URL && r.method === 'POST');
    expect(req.request.body).toEqual({
      name: 'Acme Coworking', defaultTimeZoneId: 'Europe/Zagreb', adminFirstName: 'Ana', adminLastName: 'Anic', adminEmail: 'ana@acme.test',
    });
    req.flush({ tenantId: 'tenant-1', tenantName: 'Acme Coworking', adminUserId: 'user-1', adminEmail: 'ana@acme.test' });

    expect(result).toEqual({ tenantId: 'tenant-1', tenantName: 'Acme Coworking', adminUserId: 'user-1', adminEmail: 'ana@acme.test' });
  });

  it('resendAdminInvitation POSTs to the exact tenant/user resend-invitation route with no body', () => {
    let result: unknown;
    service.resendAdminInvitation('tenant-1', 'user-1').subscribe((response) => (result = response));

    const req = httpTesting.expectOne((r) => r.url === `${TENANTS_URL}/tenant-1/users/user-1/resend-invitation` && r.method === 'POST');
    expect(req.request.body).toBeNull();
    req.flush({ tenantId: 'tenant-1', adminUserId: 'user-1', adminEmail: 'ana@acme.test', invitationExpiresAtUtc: '2026-03-04T09:00:00Z' });

    expect(result).toEqual({ tenantId: 'tenant-1', adminUserId: 'user-1', adminEmail: 'ana@acme.test', invitationExpiresAtUtc: '2026-03-04T09:00:00Z' });
  });
});
