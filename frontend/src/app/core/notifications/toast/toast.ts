import { Component, inject } from '@angular/core';
import { NotificationService } from '../notification.service';

@Component({
  selector: 'app-toast',
  imports: [],
  templateUrl: './toast.html',
  styleUrl: './toast.scss',
})
export class ToastComponent {
  protected readonly notificationService = inject(NotificationService);
}
