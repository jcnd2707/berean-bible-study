import { Component } from '@angular/core';
import { BibleReaderComponent } from './features/bible-reader/bible-reader.component';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [BibleReaderComponent],
  template: `
    <div class="app-shell">
      <!-- Future: sidebar nav, panel layout, AI chat panel -->
      <main class="panel-main">
        <app-bible-reader />
      </main>
    </div>
  `,
  styles: [`
    .app-shell {
      display: flex;
      height: 100vh;
      overflow: hidden;
      background: #1a1815;
    }
    .panel-main {
      flex: 1;
      overflow: hidden;
      display: flex;
      flex-direction: column;
    }
  `]
})
export class AppComponent {}
