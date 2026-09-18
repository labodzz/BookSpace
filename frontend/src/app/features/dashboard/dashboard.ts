import { Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { DateTime } from 'luxon';
import { AuthService } from '../../core/auth/auth.service';
import { ResourceService } from '../resources/resource.service';
import { ResourceSummary } from '../resources/resource.models';

const AVAILABLE_RESOURCES_PREVIEW_COUNT = 4;

// The real landing page, built from the same ResourceService every other resources screen uses - no
// separate/fake summary endpoint, no hardcoded numbers. Booking/approval-derived stats and widgets
// (upcoming bookings, pending approvals) land once those features themselves ship - the booking and
// approval work isn't part of this PR, so nothing here reaches for it ahead of time.
@Component({
  selector: 'app-dashboard',
  imports: [RouterLink],
  templateUrl: './dashboard.html',
  styleUrl: './dashboard.scss',
})
export class DashboardComponent {
  protected readonly authService = inject(AuthService);
  private readonly resourceService = inject(ResourceService);

  protected readonly today = DateTime.now().toFormat('cccc, LLLL d');

  protected readonly activeResourceCount = signal(0);
  protected readonly availableResources = signal<ResourceSummary[]>([]);

  constructor() {
    this.resourceService.getResources(1, AVAILABLE_RESOURCES_PREVIEW_COUNT, undefined, 'Active').subscribe((result) => {
      this.activeResourceCount.set(result.totalCount);
      this.availableResources.set(result.items);
    });
    this.resourceService.getResourceTypes().subscribe();
  }

  protected typeName(resourceTypeId: string): string {
    return this.resourceService.resourceTypeName(resourceTypeId);
  }
}
