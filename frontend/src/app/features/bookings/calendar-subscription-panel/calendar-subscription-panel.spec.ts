import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { environment } from '../../../../environments/environment';
import { CalendarSubscriptionPanelComponent } from './calendar-subscription-panel';

const SUBSCRIPTIONS_URL = `${environment.apiUrl}/calendar-subscriptions`;
const REGENERATE_URL = `${SUBSCRIPTIONS_URL}/regenerate`;

describe('CalendarSubscriptionPanelComponent', () => {
  let fixture: ComponentFixture<CalendarSubscriptionPanelComponent>;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [CalendarSubscriptionPanelComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(CalendarSubscriptionPanelComponent);
  });

  afterEach(() => httpTesting.verify());

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function flushStatus(hasActiveSubscription: boolean, createdAtUtc: string | null = null): void {
    httpTesting.expectOne((r) => r.url === SUBSCRIPTIONS_URL && r.method === 'GET').flush({ hasActiveSubscription, createdAtUtc });
  }

  function findButton(text: string): HTMLButtonElement | undefined {
    return [...root().querySelectorAll('button')].find((b) => b.textContent?.trim() === text) as HTMLButtonElement | undefined;
  }

  it('shows a loading state before the initial status request resolves', () => {
    fixture.detectChanges();

    expect(root().textContent).toContain('Loading');
    flushStatus(false);
  });

  it('shows a retry option when the initial status request fails', () => {
    fixture.detectChanges();
    httpTesting.expectOne((r) => r.url === SUBSCRIPTIONS_URL).flush('error', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    expect(findButton('Try again')).toBeTruthy();

    findButton('Try again')!.click();
    flushStatus(false);
  });

  it('with no active subscription, offers to create one and never shows a link or Regenerate/Revoke', () => {
    fixture.detectChanges();
    flushStatus(false);
    fixture.detectChanges();

    expect(findButton('Create subscription link')).toBeTruthy();
    expect(findButton('Regenerate link')).toBeFalsy();
    expect(findButton('Revoke')).toBeFalsy();
    expect(root().querySelector('input')).toBeFalsy();
  });

  it('creating a subscription shows the returned feed URL exactly once, with a copy affordance', () => {
    fixture.detectChanges();
    flushStatus(false);
    fixture.detectChanges();

    findButton('Create subscription link')!.click();
    httpTesting
      .expectOne((r) => r.url === SUBSCRIPTIONS_URL && r.method === 'POST')
      .flush({ feedUrl: 'https://bookspace.example.com/api/calendar-feed?token=abc123', createdAtUtc: '2026-06-01T00:00:00Z' });
    fixture.detectChanges();

    const urlInput = root().querySelector('input') as HTMLInputElement;
    expect(urlInput.value).toBe('https://bookspace.example.com/api/calendar-feed?token=abc123');
    expect(findButton('Copy link')).toBeTruthy();
    expect(findButton('Create subscription link')).toBeFalsy();
    expect(findButton('Regenerate link')).toBeTruthy();
  });

  it('with an existing active subscription (e.g. after a reload), shows status but never a token or URL', () => {
    fixture.detectChanges();
    flushStatus(true, '2026-05-01T00:00:00Z');
    fixture.detectChanges();

    expect(root().querySelector('input')).toBeFalsy();
    expect(root().textContent).not.toContain('token=');
    expect(findButton('Regenerate link')).toBeTruthy();
    expect(findButton('Revoke')).toBeTruthy();
  });

  it('regenerating replaces the shown link with a new one', () => {
    fixture.detectChanges();
    flushStatus(true, '2026-05-01T00:00:00Z');
    fixture.detectChanges();

    findButton('Regenerate link')!.click();
    httpTesting
      .expectOne((r) => r.url === REGENERATE_URL && r.method === 'POST')
      .flush({ feedUrl: 'https://bookspace.example.com/api/calendar-feed?token=new-token', createdAtUtc: '2026-06-02T00:00:00Z' });
    fixture.detectChanges();

    const urlInput = root().querySelector('input') as HTMLInputElement;
    expect(urlInput.value).toContain('new-token');
  });

  it('revoking requires an explicit confirmation before calling the API', () => {
    fixture.detectChanges();
    flushStatus(true, '2026-05-01T00:00:00Z');
    fixture.detectChanges();

    findButton('Revoke')!.click();
    fixture.detectChanges();

    expect(findButton('Yes, revoke')).toBeTruthy();
    httpTesting.expectNone((r) => r.url === SUBSCRIPTIONS_URL && r.method === 'DELETE');

    findButton('Yes, revoke')!.click();
    httpTesting.expectOne((r) => r.url === SUBSCRIPTIONS_URL && r.method === 'DELETE').flush(null);
    fixture.detectChanges();

    expect(findButton('Create subscription link')).toBeTruthy();
  });

  it('cancelling the revoke confirmation never calls the API', () => {
    fixture.detectChanges();
    flushStatus(true, '2026-05-01T00:00:00Z');
    fixture.detectChanges();

    findButton('Revoke')!.click();
    fixture.detectChanges();
    findButton('Cancel')!.click();
    fixture.detectChanges();

    expect(findButton('Revoke')).toBeTruthy();
    httpTesting.expectNone((r) => r.url === SUBSCRIPTIONS_URL && r.method === 'DELETE');
  });

  it('copies the feed URL to the clipboard and shows transient confirmation', async () => {
    const writeText = vi.fn().mockReturnValue(Promise.resolve());
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    fixture.detectChanges();
    flushStatus(false);
    fixture.detectChanges();
    findButton('Create subscription link')!.click();
    httpTesting
      .expectOne((r) => r.url === SUBSCRIPTIONS_URL && r.method === 'POST')
      .flush({ feedUrl: 'https://bookspace.example.com/api/calendar-feed?token=abc123', createdAtUtc: '2026-06-01T00:00:00Z' });
    fixture.detectChanges();

    findButton('Copy link')!.click();
    await Promise.resolve();
    fixture.detectChanges();

    expect(writeText).toHaveBeenCalledWith('https://bookspace.example.com/api/calendar-feed?token=abc123');
    expect(findButton('Copied!')).toBeTruthy();
  });
});
