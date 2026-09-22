import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';
import { AuthService } from '../../../core/auth/auth.service';
import { LoginComponent } from './login';

describe('LoginComponent', () => {
  let fixture: ComponentFixture<LoginComponent>;
  let router: Router;
  let login: ReturnType<typeof vi.fn>;

  function configure(returnUrl: string | null = null): void {
    login = vi.fn();
    TestBed.configureTestingModule({
      imports: [LoginComponent],
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: { get: () => returnUrl } } } },
        { provide: AuthService, useValue: { login } },
      ],
    });
    router = TestBed.inject(Router);
    fixture = TestBed.createComponent(LoginComponent);
    fixture.detectChanges();
  }

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function fillAndSubmit(): void {
    const email = root().querySelector('#email') as HTMLInputElement;
    const password = root().querySelector('#password') as HTMLInputElement;
    email.value = 'admin@bookspace.test';
    email.dispatchEvent(new Event('input'));
    password.value = 'Test-Passw0rd!';
    password.dispatchEvent(new Event('input'));
    (root().querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    fixture.detectChanges();
  }

  function submitButton(): HTMLButtonElement {
    return root().querySelector('.login-submit') as HTMLButtonElement;
  }

  it('shows "Logging in…" and disables the button while the request is in flight', () => {
    configure();
    login.mockReturnValue(new Subject()); // never resolves - captures the in-flight state

    fillAndSubmit();

    expect(submitButton().disabled).toBe(true);
    expect(submitButton().textContent).toContain('Logging in…');
  });

  // The bug this guards: isSubmitting used to be reset only in the error handler, relying on
  // navigateByUrl succeeding and destroying this component to "fix" the stuck button as a side effect.
  // Any navigation that does not cleanly complete - most realistically a lazy route chunk failing to
  // load, since every route in this app is loadComponent()-lazy - left the button permanently disabled
  // on "Logging in…" with no way to retry, looking exactly like a frozen page.
  it('resets isSubmitting on a successful login even when the subsequent navigation fails', async () => {
    configure('/resource-types');
    login.mockReturnValue(of(undefined));
    vi.spyOn(router, 'navigateByUrl').mockRejectedValue(new Error('Failed to fetch dynamically imported module'));

    fillAndSubmit();
    // Let the rejected navigateByUrl promise's microtask settle before asserting.
    await Promise.resolve();
    await Promise.resolve();
    fixture.detectChanges();

    expect(submitButton().disabled).toBe(false);
    expect(submitButton().textContent).toContain('Log in');
  });

  it('resets isSubmitting on a successful login and navigates to returnUrl', () => {
    configure('/resources');
    login.mockReturnValue(of(undefined));
    const navigateByUrl = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);

    fillAndSubmit();

    expect(navigateByUrl).toHaveBeenCalledWith('/resources');
    expect(submitButton().disabled).toBe(false);
  });

  it('defaults to "/" when no returnUrl is present', () => {
    configure(null);
    login.mockReturnValue(of(undefined));
    const navigateByUrl = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);

    fillAndSubmit();

    expect(navigateByUrl).toHaveBeenCalledWith('/');
  });

  it('shows an error message and re-enables the button when login fails', () => {
    configure();
    login.mockReturnValue(
      throwError(
        () =>
          new HttpErrorResponse({
            status: 401,
            error: { title: 'Invalid email or password.' },
          }),
      ),
    );

    fillAndSubmit();

    expect(submitButton().disabled).toBe(false);
    expect(root().textContent).toContain('Invalid email or password.');
  });

  it('does not submit a second time while a request is already in flight', () => {
    configure();
    login.mockReturnValue(new Subject());

    fillAndSubmit();
    fillAndSubmit();

    expect(login).toHaveBeenCalledOnce();
  });
});
