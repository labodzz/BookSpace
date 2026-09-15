import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter } from '@angular/router';
import { authInterceptor } from './core/http/auth.interceptor';
import { errorInterceptor } from './core/http/error.interceptor';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    // errorInterceptor is outermost so it only sees a request's FINAL outcome - it runs after
    // authInterceptor's own internal refresh-and-retry has already settled, not on the initial 401.
    provideHttpClient(withInterceptors([errorInterceptor, authInterceptor])),
  ],
};
