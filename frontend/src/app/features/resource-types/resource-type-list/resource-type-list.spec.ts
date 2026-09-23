import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { environment } from '../../../../environments/environment';
import { NotificationService } from '../../../core/notifications/notification.service';
import { ResourceTypeListComponent } from './resource-type-list';

const TYPES_URL = `${environment.apiUrl}/resource-types`;

describe('ResourceTypeListComponent', () => {
  let fixture: ComponentFixture<ResourceTypeListComponent>;
  let httpTesting: HttpTestingController;

  function configure(): void {
    TestBed.configureTestingModule({
      imports: [ResourceTypeListComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ResourceTypeListComponent);
  }

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function flushLoad(types: unknown[] = []): void {
    httpTesting.expectOne((r) => r.url === TYPES_URL && r.method === 'GET').flush(types);
    fixture.detectChanges();
  }

  function buttonWithText(text: string): HTMLButtonElement {
    return [...root().querySelectorAll('button')].find((b) => b.textContent?.trim() === text) as HTMLButtonElement;
  }

  function dialogButtonWithText(text: string): HTMLButtonElement {
    const dialog = root().querySelector('.dialog') as HTMLElement;
    return [...dialog.querySelectorAll('button')].find((b) => b.textContent?.trim() === text) as HTMLButtonElement;
  }

  afterEach(() => httpTesting.verify());

  it('shows a loading skeleton before the list loads', () => {
    configure();
    fixture.detectChanges();

    expect(root().querySelector('.resource-type-list__table[aria-busy="true"]')).not.toBeNull();
    httpTesting.expectOne((r) => r.url === TYPES_URL).flush([]);
  });

  it('shows an empty-state explanation when there are no resource types yet', () => {
    configure();
    fixture.detectChanges();
    flushLoad([]);

    expect(root().textContent).toContain('No resource types yet');
  });

  it('shows an error state with a retry action when loading fails', () => {
    configure();
    fixture.detectChanges();
    httpTesting.expectOne((r) => r.url === TYPES_URL).flush('boom', { status: 500, statusText: 'Internal Server Error' });
    fixture.detectChanges();

    expect(root().textContent).toContain("Couldn't load resource types");
    buttonWithText('Try again').click();
    httpTesting.expectOne((r) => r.url === TYPES_URL).flush([{ id: 'type-1', name: 'Desk' }]);
    fixture.detectChanges();

    expect(root().textContent).toContain('Desk');
  });

  it('lists resource types by name', () => {
    configure();
    fixture.detectChanges();
    flushLoad([
      { id: 'type-1', name: 'Meeting Room' },
      { id: 'type-2', name: 'Desk' },
    ]);

    expect(root().textContent).toContain('Meeting Room');
    expect(root().textContent).toContain('Desk');
  });

  it('creates a resource type and shows a success toast', () => {
    configure();
    fixture.detectChanges();
    flushLoad([]);

    buttonWithText('Add resource type').click();
    fixture.detectChanges();
    (root().querySelector('#type-name') as HTMLInputElement).value = 'Parking Spot';
    (root().querySelector('#type-name') as HTMLInputElement).dispatchEvent(new Event('input'));
    dialogButtonWithText('Add type').click();

    const req = httpTesting.expectOne((r) => r.url === TYPES_URL && r.method === 'POST');
    expect(req.request.body).toEqual({ name: 'Parking Spot' });
    req.flush({ id: 'type-1', name: 'Parking Spot' });
    fixture.detectChanges();

    expect(root().querySelector('.dialog')).toBeNull();
    expect(root().textContent).toContain('Parking Spot');
    expect(TestBed.inject(NotificationService).toasts()).toEqual([expect.objectContaining({ message: 'Resource type added.' })]);
  });

  it('rejects an empty name client-side without calling the backend', () => {
    configure();
    fixture.detectChanges();
    flushLoad([]);

    buttonWithText('Add resource type').click();
    fixture.detectChanges();
    dialogButtonWithText('Add type').click();
    fixture.detectChanges();

    expect(root().querySelector('.dialog')!.textContent).toContain('Name is required.');
    httpTesting.expectNone((r) => r.url === TYPES_URL && r.method === 'POST');
  });

  it('shows a clear message for a duplicate-name conflict and keeps the dialog open', () => {
    configure();
    fixture.detectChanges();
    flushLoad([]);

    buttonWithText('Add resource type').click();
    fixture.detectChanges();
    (root().querySelector('#type-name') as HTMLInputElement).value = 'Desk';
    (root().querySelector('#type-name') as HTMLInputElement).dispatchEvent(new Event('input'));
    dialogButtonWithText('Add type').click();

    httpTesting
      .expectOne((r) => r.url === TYPES_URL && r.method === 'POST')
      .flush({ title: 'Conflict', errorCode: 'ResourceType.NameConflict' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(root().querySelector('.dialog')).not.toBeNull();
    expect(root().textContent).toContain('A resource type with this name already exists.');
  });

  it('edits a resource type, pre-filling the current name', () => {
    configure();
    fixture.detectChanges();
    flushLoad([{ id: 'type-1', name: 'Desk' }]);

    buttonWithText('Edit').click();
    fixture.detectChanges();

    expect((root().querySelector('#type-name') as HTMLInputElement).value).toBe('Desk');

    (root().querySelector('#type-name') as HTMLInputElement).value = 'Standing Desk';
    (root().querySelector('#type-name') as HTMLInputElement).dispatchEvent(new Event('input'));
    dialogButtonWithText('Save changes').click();

    const req = httpTesting.expectOne((r) => r.url === `${TYPES_URL}/type-1` && r.method === 'PUT');
    expect(req.request.body).toEqual({ name: 'Standing Desk' });
    req.flush({ id: 'type-1', name: 'Standing Desk' });
    fixture.detectChanges();

    expect(root().textContent).toContain('Standing Desk');
  });

  it('opens a confirmation dialog naming the type before deleting it', () => {
    configure();
    fixture.detectChanges();
    flushLoad([{ id: 'type-1', name: 'Desk' }]);

    buttonWithText('Delete').click();
    fixture.detectChanges();

    expect(root().querySelector('.dialog')!.textContent).toContain('Desk');
    httpTesting.expectNone((r) => r.url === `${TYPES_URL}/type-1` && r.method === 'DELETE');
  });

  it('deletes a resource type on confirm and shows a success toast', () => {
    configure();
    fixture.detectChanges();
    flushLoad([{ id: 'type-1', name: 'Desk' }]);

    buttonWithText('Delete').click();
    fixture.detectChanges();
    dialogButtonWithText('Delete').click();

    httpTesting.expectOne((r) => r.url === `${TYPES_URL}/type-1` && r.method === 'DELETE').flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();

    expect(root().querySelector('.dialog')).toBeNull();
    expect(root().textContent).toContain('No resource types yet');
    expect(TestBed.inject(NotificationService).toasts()).toEqual([expect.objectContaining({ message: 'Resource type deleted.' })]);
  });

  it('on a delete conflict (still referenced by a resource), keeps the type visible and shows a clear explanation', () => {
    configure();
    fixture.detectChanges();
    flushLoad([{ id: 'type-1', name: 'Desk' }]);

    buttonWithText('Delete').click();
    fixture.detectChanges();
    dialogButtonWithText('Delete').click();

    httpTesting
      .expectOne((r) => r.url === `${TYPES_URL}/type-1` && r.method === 'DELETE')
      .flush({ title: 'Conflict', errorCode: 'ResourceType.InUse' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(root().querySelector('.dialog')).not.toBeNull();
    expect(root().textContent).toContain('still used by at least one resource');
    expect(root().textContent).toContain('Desk'); // never optimistically removed
  });

  it('closes the delete dialog without deleting when "Keep it" is clicked', () => {
    configure();
    fixture.detectChanges();
    flushLoad([{ id: 'type-1', name: 'Desk' }]);

    buttonWithText('Delete').click();
    fixture.detectChanges();
    dialogButtonWithText('Keep it').click();
    fixture.detectChanges();

    expect(root().querySelector('.dialog')).toBeNull();
    httpTesting.expectNone((r) => r.url === `${TYPES_URL}/type-1` && r.method === 'DELETE');
  });
});
