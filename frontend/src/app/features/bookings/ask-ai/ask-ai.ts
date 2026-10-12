import { Component, inject } from '@angular/core';
import { Router } from '@angular/router';
import { AiAssistantService } from '../../ai-assistant/ai-assistant.service';

// The old /bookings/ask-ai route used to BE the AI booking form - now the AI assistant is the floating,
// always-available widget (ShellComponent/AiAssistantWidgetComponent), not a page. This route is kept,
// rather than removed, purely so existing links/bookmarks to it keep working (the task's own "do not
// leave broken navigation links"): visiting it simply opens the shared widget and redirects to My
// Bookings, instead of maintaining a second, separate chat implementation on a page of its own.
@Component({
  selector: 'app-ask-ai',
  template: '',
})
export class AskAiComponent {
  constructor() {
    const assistant = inject(AiAssistantService);
    const router = inject(Router);
    assistant.open();
    router.navigate(['/bookings'], { replaceUrl: true });
  }
}
