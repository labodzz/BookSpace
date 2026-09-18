import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { environment } from '../../../environments/environment';
import { AuthService } from '../../core/auth/auth.service';
import { ShellComponent } from './shell';

describe('ShellComponent', () => {
  let fixture: ComponentFixture<ShellComponent>;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [ShellComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: AuthService,
          useValue: { hasAnyRole: () => true, currentUser: () => null, displayName: () => '', logout: () => undefined },
        },
      ],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ShellComponent);
  });

  afterEach(() => httpTesting.verify());

  // getPendingApprovals().subscribe(...) used to pass only a next callback - a failure here would have
  // been an unhandled RxJS error thrown synchronously out of flush() below, instead of just leaving the
  // approvals nav badge at its initial 0.
  it('does not throw when the pending-approvals request fails, and leaves the badge count at 0', () => {
    expect(() =>
      httpTesting.expectOne(`${environment.apiUrl}/bookings/pending-approval`).flush('boom', { status: 500, statusText: 'Internal Server Error' }),
    ).not.toThrow();

    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('.shell-nav__badge')).toBeNull();
  });
});
