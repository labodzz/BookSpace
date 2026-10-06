import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';
import { AuthService } from '../../../core/auth/auth.service';
import { AcceptInvitationComponent } from './accept-invitation';

describe('AcceptInvitationComponent', () => {
  let fixture: ComponentFixture<AcceptInvitationComponent>;
  let acceptInvitation: ReturnType<typeof vi.fn>;

  function configure(token: string | null): void {
    acceptInvitation = vi.fn();
    TestBed.configureTestingModule({
      imports: [AcceptInvitationComponent],
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: { get: () => token } } } },
        { provide: AuthService, useValue: { acceptInvitation } },
      ],
    });
    fixture = TestBed.createComponent(AcceptInvitationComponent);
    fixture.detectChanges();
  }

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function setPasswords(password: string, confirmPassword: string): void {
    const passwordInput = root().querySelector('#password') as HTMLInputElement;
    const confirmInput = root().querySelector('#confirm-password') as HTMLInputElement;
    passwordInput.value = password;
    passwordInput.dispatchEvent(new Event('input'));
    confirmInput.value = confirmPassword;
    confirmInput.dispatchEvent(new Event('input'));
  }

  function submit(): void {
    (root().querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    fixture.detectChanges();
  }

  function submitButton(): HTMLButtonElement {
    return root().querySelector('.accept-invitation-submit') as HTMLButtonElement;
  }

  it('shows an "invalid link" message with no form at all when the URL has no token', () => {
    configure(null);

    expect(root().textContent).toContain("This invitation link isn't valid");
    expect(root().querySelector('form')).toBeNull();
    expect(acceptInvitation).not.toHaveBeenCalled();
  });

  it('blocks submission client-side when the password is too short, without calling the API', () => {
    configure('a-raw-token');
    setPasswords('short', 'short');

    submit();

    expect(root().textContent).toContain('at least 8 characters');
    expect(acceptInvitation).not.toHaveBeenCalled();
  });

  it('blocks submission client-side when the two passwords do not match', () => {
    configure('a-raw-token');
    setPasswords('Correct-Horse-9!', 'Different-9!');

    submit();

    expect(root().textContent).toContain("Passwords don't match.");
    expect(acceptInvitation).not.toHaveBeenCalled();
  });

  it('shows "Setting password…" and disables the button while the request is in flight', () => {
    configure('a-raw-token');
    acceptInvitation.mockReturnValue(new Subject());
    setPasswords('Correct-Horse-9!', 'Correct-Horse-9!');

    submit();

    expect(submitButton().disabled).toBe(true);
    expect(submitButton().textContent).toContain('Setting password…');
  });

  it('calls AuthService.acceptInvitation with the URL token and the chosen password', () => {
    configure('a-raw-token');
    acceptInvitation.mockReturnValue(of(undefined));
    setPasswords('Correct-Horse-9!', 'Correct-Horse-9!');

    submit();

    expect(acceptInvitation).toHaveBeenCalledWith('a-raw-token', 'Correct-Horse-9!');
  });

  it('shows a success confirmation and a link to login on success - never logging the user in automatically', () => {
    configure('a-raw-token');
    acceptInvitation.mockReturnValue(of(undefined));
    setPasswords('Correct-Horse-9!', 'Correct-Horse-9!');

    submit();

    expect(root().textContent).toContain('Your password is set');
    expect(root().querySelector('a[href="/login"]')).not.toBeNull();
  });

  it('shows the backend\'s generic failure message and re-enables the button on an invalid/expired/revoked/used token', () => {
    configure('a-raw-token');
    acceptInvitation.mockReturnValue(
      throwError(
        () =>
          new HttpErrorResponse({
            status: 401,
            error: { title: 'Authentication failed.', detail: 'This invitation link is no longer valid.' },
          }),
      ),
    );
    setPasswords('Correct-Horse-9!', 'Correct-Horse-9!');

    submit();

    expect(submitButton().disabled).toBe(false);
    expect(root().textContent).toContain('This invitation link is no longer valid.');
  });

  it('does not submit a second time while a request is already in flight', () => {
    configure('a-raw-token');
    acceptInvitation.mockReturnValue(new Subject());
    setPasswords('Correct-Horse-9!', 'Correct-Horse-9!');

    submit();
    submit();

    expect(acceptInvitation).toHaveBeenCalledOnce();
  });
});
