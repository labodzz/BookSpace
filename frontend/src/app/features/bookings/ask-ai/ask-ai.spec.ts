import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { AiAssistantService } from '../../ai-assistant/ai-assistant.service';
import { AskAiComponent } from './ask-ai';

describe('AskAiComponent', () => {
  it('opens the shared assistant widget and redirects to My Bookings, instead of rendering its own page', async () => {
    TestBed.configureTestingModule({
      imports: [AskAiComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([{ path: 'bookings', children: [] }])],
    });
    const router = TestBed.inject(Router);
    const navigateSpy = vi.spyOn(router, 'navigate');

    const fixture = TestBed.createComponent(AskAiComponent);
    fixture.detectChanges();

    expect(TestBed.inject(AiAssistantService).isOpen()).toBe(true);
    expect(navigateSpy).toHaveBeenCalledWith(['/bookings'], { replaceUrl: true });
  });
});
