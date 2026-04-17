import { Component, OnInit, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { NotesService } from '../../core/services/notes.service';
import { NavigationStateService } from '../../core/services/navigation-state.service';
import { Note } from '../../core/models';

@Component({
  selector: 'app-notes-list',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './notes-list.component.html',
  styleUrl: './notes-list.component.scss',
})
export class NotesListComponent implements OnInit {
  private readonly notesService = inject(NotesService);
  readonly nav                   = inject(NavigationStateService);

  readonly notes       = signal<Note[]>([]);
  readonly filterText  = signal('');
  readonly loading     = signal(false);

  readonly filtered = computed(() => {
    const q = this.filterText().toLowerCase().trim();
    if (!q) return this.notes();
    return this.notes().filter(n =>
      n.reference.toLowerCase().includes(q) ||
      n.text.toLowerCase().includes(q)
    );
  });

  ngOnInit(): void {
    this.loading.set(true);
    this.notesService.getAll().subscribe({
      next: notes => { this.notes.set(notes); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }

  navigateTo(note: Note): void {
    // Parse reference e.g. "Gen.1.1" or "Gen.1"
    const parts = note.reference.split('.');
    if (parts.length < 2) return;
    const [book, chapterStr, verseStr] = parts;
    const chapter = parseInt(chapterStr, 10);
    const verse   = verseStr ? parseInt(verseStr, 10) : null;
    const loc = this.nav.location();
    this.nav.navigate({
      moduleId: loc?.moduleId ?? '',
      book,
      chapter,
      verse,
    });
    this.nav.closeNotesList();
  }

  formatDate(iso?: string): string {
    if (!iso) return '';
    try {
      return new Date(iso).toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
    } catch { return ''; }
  }

  snippet(text: string): string {
    return text.length > 120 ? text.slice(0, 120).trimEnd() + '…' : text;
  }
}
