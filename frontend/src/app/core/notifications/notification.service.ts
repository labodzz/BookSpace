import { Injectable, signal } from '@angular/core';

export interface Toast {
  id: number;
  message: string;
  tone: 'error' | 'success';
}

const AUTO_DISMISS_MS = 6000;

@Injectable({ providedIn: 'root' })
export class NotificationService {
  private nextId = 0;
  readonly toasts = signal<Toast[]>([]);

  showError(message: string): void {
    this.push(message, 'error');
  }

  showSuccess(message: string): void {
    this.push(message, 'success');
  }

  dismiss(id: number): void {
    this.toasts.update((toasts) => toasts.filter((toast) => toast.id !== id));
  }

  private push(message: string, tone: Toast['tone']): void {
    const id = this.nextId++;
    this.toasts.update((toasts) => [...toasts, { id, message, tone }]);
    setTimeout(() => this.dismiss(id), AUTO_DISMISS_MS);
  }
}
