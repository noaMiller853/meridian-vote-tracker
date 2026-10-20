import { Component, DestroyRef, inject, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { USERS, USER_KEY, api, currentUserName } from './api/client';
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
  protected readonly role = USERS.find((u) => u.name === this.user)?.role ?? '';
  protected readonly pending = signal(0);

  constructor() {
    // The badge on "Review": conflicts waiting for a decision. It is refreshed every few seconds.
    const poller = createPoller(
      async () => this.pending.set((await api.conflicts.list({ take: 1 })).total),
      () => true,
      8000,
    );
    void poller.start();
    inject(DestroyRef).onDestroy(() => poller.stop());
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
