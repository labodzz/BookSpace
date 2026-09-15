import { Injectable } from '@angular/core';
import { AuthTokens } from './auth.models';

const ACCESS_TOKEN_KEY = 'bookspace.accessToken';
const ACCESS_TOKEN_EXPIRES_KEY = 'bookspace.accessTokenExpiresAtUtc';
const REFRESH_TOKEN_KEY = 'bookspace.refreshToken';
const REFRESH_TOKEN_EXPIRES_KEY = 'bookspace.refreshTokenExpiresAtUtc';

// The backend hands tokens back as plain JSON (not Set-Cookie), so Web Storage is the only place for
// the client to keep them. Which storage backs a session is the real implementation of "Remember me":
// localStorage survives closing the browser, sessionStorage is cleared with the tab - not a cosmetic
// checkbox, an actual choice of how long the session outlives this tab.
@Injectable({ providedIn: 'root' })
export class TokenStorageService {
  // Tracks which backend the current session lives in, so a token refresh (which doesn't go through
  // the login form and has no "remember me" value of its own) keeps writing to the same place instead
  // of silently upgrading a session-only login into a persistent one.
  private activeStorage: Storage = sessionStorage;

  save(tokens: AuthTokens, persist: boolean): void {
    this.activeStorage = persist ? localStorage : sessionStorage;
    this.clearStorage(persist ? sessionStorage : localStorage);
    this.writeTo(this.activeStorage, tokens);
  }

  // Used by AuthService when persisting tokens from a refresh - there's no "remember me" input at that
  // point, so it must reuse whichever storage the session was already found in.
  saveToActiveStorage(tokens: AuthTokens): void {
    this.writeTo(this.activeStorage, tokens);
  }

  load(): AuthTokens | null {
    const fromSession = this.readFrom(sessionStorage);
    if (fromSession) {
      this.activeStorage = sessionStorage;
      return fromSession;
    }

    const fromLocal = this.readFrom(localStorage);
    if (fromLocal) {
      this.activeStorage = localStorage;
      return fromLocal;
    }

    return null;
  }

  clear(): void {
    this.clearStorage(localStorage);
    this.clearStorage(sessionStorage);
  }

  private writeTo(storage: Storage, tokens: AuthTokens): void {
    storage.setItem(ACCESS_TOKEN_KEY, tokens.accessToken);
    storage.setItem(ACCESS_TOKEN_EXPIRES_KEY, tokens.accessTokenExpiresAtUtc);
    storage.setItem(REFRESH_TOKEN_KEY, tokens.refreshToken);
    storage.setItem(REFRESH_TOKEN_EXPIRES_KEY, tokens.refreshTokenExpiresAtUtc);
  }

  private readFrom(storage: Storage): AuthTokens | null {
    const accessToken = storage.getItem(ACCESS_TOKEN_KEY);
    const accessTokenExpiresAtUtc = storage.getItem(ACCESS_TOKEN_EXPIRES_KEY);
    const refreshToken = storage.getItem(REFRESH_TOKEN_KEY);
    const refreshTokenExpiresAtUtc = storage.getItem(REFRESH_TOKEN_EXPIRES_KEY);

    if (!accessToken || !accessTokenExpiresAtUtc || !refreshToken || !refreshTokenExpiresAtUtc) {
      return null;
    }

    return { accessToken, accessTokenExpiresAtUtc, refreshToken, refreshTokenExpiresAtUtc };
  }

  private clearStorage(storage: Storage): void {
    storage.removeItem(ACCESS_TOKEN_KEY);
    storage.removeItem(ACCESS_TOKEN_EXPIRES_KEY);
    storage.removeItem(REFRESH_TOKEN_KEY);
    storage.removeItem(REFRESH_TOKEN_EXPIRES_KEY);
  }
}
