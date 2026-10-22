import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { USERS, USER_KEY, api, currentRole, currentUserName, getSession } from './api/client';
import { loadAuthMode, signIn, signOut } from './auth';
import { createPoller } from './shared';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {
  protected readonly users = USERS;
  protected readonly user = currentUserName();
  protected readonly role = currentRole();
  protected readonly pending = signal(0);

  /** 'loading' until the server says how it identifies users; 'Jwt' shows a sign-in form, 'Header' the user switcher. */
  protected readonly mode = signal<'loading' | 'Header' | 'Jwt'>('loading');
  protected readonly needsSignIn = computed(() => this.mode() === 'Jwt' && getSession() === null);

  protected readonly userName = signal('');
  protected readonly password = signal('');
  protected readonly signInError = signal<{ message: string; field?: 'userName' | 'password' } | null>(null);
  protected readonly signingIn = signal(false);

  constructor() {
    const poller = createPoller(
      async () => this.pending.set((await api.conflicts.list({ take: 1 })).total),
      () => true,
      8000,
    );
    inject(DestroyRef).onDestroy(() => poller.stop());

    void loadAuthMode().then((mode) => {
      this.mode.set(mode);
      // The badge on "Review": conflicts waiting for a decision. It is refreshed every few seconds, once the user is known.
      if (!this.needsSignIn()) void poller.start();
    });
  }

  protected async submitSignIn(event: Event) {
    event.preventDefault();
    this.signingIn.set(true);
    this.signInError.set(null);
    const result = await signIn(this.userName().trim(), this.password());
    this.signingIn.set(false);
    if (result.ok) location.reload();
    else this.signInError.set({ message: result.message, field: result.field });
  }

  protected signOut() {
    signOut();
  }

  protected switchUser(name: string) {
    try {
      localStorage.setItem(USER_KEY, name);
    } catch {
      /* the choice is then not remembered */
    }
    // Everything on screen is read again as the new user.
    location.reload();
  }
}
