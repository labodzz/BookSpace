import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { environment } from '../../../../environments/environment';
import { TenantFormComponent } from './tenant-form';

const TENANTS_URL = `${environment.apiUrl}/tenants`;
const TIMEZONES_URL = `${environment.apiUrl}/resources/supported-timezones`;
const SUPPORTED_TIMEZONES = ['Europe/Sarajevo', 'Europe/Zagreb', 'UTC'];

describe('TenantFormComponent', () => {
  let fixture: ComponentFixture<TenantFormComponent>;
  let httpTesting: HttpTestingController;

  function configure(): void {
    vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockReturnValue({
      timeZone: 'Europe/Sarajevo',
    } as Intl.ResolvedDateTimeFormatOptions);

    TestBed.configureTestingModule({
      imports: [TenantFormComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(TenantFormComponent);
    fixture.detectChanges(); // constructs app-timezone-select, which issues the TIMEZONES_URL request
    httpTesting.expectOne((r) => r.url === TIMEZONES_URL).flush(SUPPORTED_TIMEZONES);
    fixture.detectChanges();
  }

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function setInput(id: string, value: string): void {
    const input = root().querySelector(`#${id}`) as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  function selectTimeZone(zoneId: string): void {
    const input = root().querySelector('#time-zone') as HTMLInputElement;
    input.dispatchEvent(new Event('focus'));
    fixture.detectChanges();
    input.value = zoneId;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    const option = [...root().querySelectorAll('[role="option"]')].find((element) => element.textContent?.trim() === zoneId);
    option?.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
    fixture.detectChanges();
  }

  // Dispatches 'submit' directly on the form rather than clicking the submit button - jsdom's
  // HTMLFormElement.requestSubmit() (what a real browser's implicit button-click submission routes
  // through) is an unimplemented stub here, so button.click() never actually fires the submit event. Same
  // approach login.spec.ts already uses for its own (ngSubmit) form.
  function submitForm(): void {
    (root().querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    fixture.detectChanges();
  }

  function fillValidFormAndSubmit(): void {
    setInput('tenant-name', 'Acme Coworking');
    selectTimeZone('Europe/Zagreb');
    setInput('admin-first-name', 'Ana');
    setInput('admin-last-name', 'Anic');
    setInput('admin-email', 'ana@acme.test');
    submitForm();
  }

  beforeEach(configure);

  afterEach(() => {
    httpTesting.verify();
    vi.restoreAllMocks();
  });

  it('renders an empty create form', () => {
    expect(root().textContent).toContain('New tenant');
    httpTesting.expectNone((r) => r.url === TENANTS_URL);
  });

  it('blocks submission client-side when required fields are missing, without an HTTP call', () => {
    submitForm();

    expect(root().textContent).toContain('Tenant name is required.');
    httpTesting.expectNone((r) => r.url === TENANTS_URL);
  });

  it('submits a valid request and shows the confirmation panel - never claiming the email was sent', () => {
    fillValidFormAndSubmit();

    const req = httpTesting.expectOne((r) => r.url === TENANTS_URL && r.method === 'POST');
    expect(req.request.body).toEqual({
      name: 'Acme Coworking', defaultTimeZoneId: 'Europe/Zagreb', adminFirstName: 'Ana', adminLastName: 'Anic', adminEmail: 'ana@acme.test',
    });
    req.flush({ tenantId: 'tenant-1', tenantName: 'Acme Coworking', adminUserId: 'user-1', adminEmail: 'ana@acme.test' });
    fixture.detectChanges();

    expect(root().textContent).toContain('Tenant created');
    expect(root().textContent).toContain('Acme Coworking');
    expect(root().textContent).toContain('queued for sending');
    // Never claims delivery - only that the outbox write succeeded (see tenant-form.ts's own doc comment).
    expect(root().textContent).not.toMatch(/email (has been |was )?sent\b/i);
  });

  it('renders a conflict message when the tenant name already exists', () => {
    fillValidFormAndSubmit();

    httpTesting
      .expectOne((r) => r.url === TENANTS_URL && r.method === 'POST')
      .flush(
        { title: 'Conflict', detail: "A tenant named 'Acme Coworking' already exists.", errorCode: 'Tenant.NameConflict' },
        { status: 409, statusText: 'Conflict' },
      );
    fixture.detectChanges();

    expect(root().textContent).toContain('A tenant with this name already exists.');
  });

  it('renders field-level validation errors from the backend', () => {
    fillValidFormAndSubmit();

    httpTesting
      .expectOne((r) => r.url === TENANTS_URL && r.method === 'POST')
      .flush({ title: 'Validation failed', errors: { AdminEmail: ['AdminEmail is not a valid email address.'] } }, { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();

    expect(root().textContent).toContain('AdminEmail is not a valid email address.');
  });

  describe('resending the invitation from the confirmation panel', () => {
    beforeEach(() => {
      fillValidFormAndSubmit();
      httpTesting
        .expectOne((r) => r.url === TENANTS_URL && r.method === 'POST')
        .flush({ tenantId: 'tenant-1', tenantName: 'Acme Coworking', adminUserId: 'user-1', adminEmail: 'ana@acme.test' });
      fixture.detectChanges();
    });

    it('resends to the exact created tenant/admin and confirms it was queued again', () => {
      const resendButton = [...root().querySelectorAll('button')].find((b) => b.textContent?.includes('Resend invitation'))!;
      resendButton.click();
      fixture.detectChanges();

      const req = httpTesting.expectOne((r) => r.url === `${TENANTS_URL}/tenant-1/users/user-1/resend-invitation` && r.method === 'POST');
      req.flush({ tenantId: 'tenant-1', adminUserId: 'user-1', adminEmail: 'ana@acme.test', invitationExpiresAtUtc: '2026-03-04T09:00:00Z' });
      fixture.detectChanges();

      expect(root().textContent).toContain('A new invitation was queued for ana@acme.test.');
    });

    it('shows an error if the admin already accepted (resend no longer eligible)', () => {
      const resendButton = [...root().querySelectorAll('button')].find((b) => b.textContent?.includes('Resend invitation'))!;
      resendButton.click();
      fixture.detectChanges();

      httpTesting
        .expectOne((r) => r.url === `${TENANTS_URL}/tenant-1/users/user-1/resend-invitation`)
        .flush(
          { title: 'Conflict', detail: 'User no longer has a pending invitation (current status: Active).' },
          { status: 409, statusText: 'Conflict' },
        );
      fixture.detectChanges();

      expect(root().textContent).toContain('User no longer has a pending invitation');
    });
  });
});
