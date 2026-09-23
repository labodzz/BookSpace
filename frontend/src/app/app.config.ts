import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter } from '@angular/router';
import { environment } from '../environments/environment';
import { authInterceptor } from './core/http/auth.interceptor';
import { diagnosticsInterceptor } from './core/diagnostics/diagnostics.interceptor';
import { errorInterceptor } from './core/http/error.interceptor';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    // errorInterceptor is outermost so it only sees a request's FINAL outcome - it runs after
    // authInterceptor's own internal refresh-and-retry has already settled, not on the initial 401.
    // diagnosticsInterceptor is innermost (closest to the actual HTTP call) so its recorded duration
    // reflects the real network/backend time, not time spent inside the auth retry - and it is entirely
    // absent from the array in a production build, not merely disabled at runtime.
    provideHttpClient(
      withInterceptors(environment.production ? [errorInterceptor, authInterceptor] : [errorInterceptor, authInterceptor, diagnosticsInterceptor]),
    ),
  ],
};
