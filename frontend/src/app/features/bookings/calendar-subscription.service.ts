import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { CalendarSubscriptionStatus, CalendarSubscriptionToken } from './calendar-subscription.models';

@Injectable({ providedIn: 'root' })
export class CalendarSubscriptionService {
  private readonly http = inject(HttpClient);

  getStatus(): Observable<CalendarSubscriptionStatus> {
    return this.http.get<CalendarSubscriptionStatus>(`${environment.apiUrl}/calendar-subscriptions`);
  }

  create(): Observable<CalendarSubscriptionToken> {
    return this.http.post<CalendarSubscriptionToken>(`${environment.apiUrl}/calendar-subscriptions`, null);
  }

  regenerate(): Observable<CalendarSubscriptionToken> {
    return this.http.post<CalendarSubscriptionToken>(`${environment.apiUrl}/calendar-subscriptions/regenerate`, null);
  }

  revoke(): Observable<void> {
    return this.http.delete<void>(`${environment.apiUrl}/calendar-subscriptions`);
  }
}
