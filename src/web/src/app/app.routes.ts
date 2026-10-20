import { Routes } from '@angular/router';
import { ImportReport } from './imports/import-report';
import { Imports } from './imports/imports';
import { Review } from './review/review';
import { SnapshotDetail } from './snapshots/snapshot-detail';
import { Snapshots } from './snapshots/snapshots';
import { VoteDetail } from './votes/vote-detail';
import { Votes } from './votes/votes';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'imports' },
  { path: 'imports', component: Imports },
  { path: 'imports/:id', component: ImportReport },
  { path: 'review', component: Review },
  { path: 'votes', component: Votes },
  { path: 'votes/:id', component: VoteDetail },
  { path: 'snapshots', component: Snapshots },
  { path: 'snapshots/:id', component: SnapshotDetail },
  { path: '**', redirectTo: 'imports' },
];
