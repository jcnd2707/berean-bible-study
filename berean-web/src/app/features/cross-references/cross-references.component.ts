import { Component, inject, signal, effect } from '@angular/core';
import { CommonModule } from '@angular/common';
import { CrossReferencesService } from '../../core/services/cross-references.service';
import { NavigationStateService } from '../../core/services/navigation-state.service';
import { CrossReference } from '../../core/models';
import { catchError } from 'rxjs/operators';
import { EMPTY } from 'rxjs';

@Component({
  selector: 'app-cross-references',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './cross-references.component.html',
  styleUrl: './cross-references.component.scss',
})
export class CrossReferencesComponent {
  private readonly xrefService = inject(CrossReferencesService);
  readonly nav                  = inject(NavigationStateService);

  readonly references = signal<CrossReference[]>([]);
  readonly fromRef    = signal<string>('');
  readonly loading    = signal(false);
  readonly error      = signal<string | null>(null);

  private readonly _effect = effect(() => {
    const loc = this.nav.location();
    if (!loc?.verse) {
      this.references.set([]);
      this.fromRef.set('');
      return;
    }
    this.load(loc.book, loc.chapter, loc.verse);
  });

  private load(book: string, chapter: number, verse: number): void {
    this.loading.set(true);
    this.error.set(null);

    this.xrefService.get(book, chapter, verse).pipe(
      catchError(() => {
        this.error.set('No cross-references found.');
        this.loading.set(false);
        return EMPTY;
      })
    ).subscribe(resp => {
      this.references.set(resp.references);
      this.fromRef.set(resp.reference);
      this.loading.set(false);
    });
  }

  navigateTo(ref: CrossReference): void {
    const loc = this.nav.location();
    if (!loc) return;
    const bookAbbr = this.nav.bookAbbrFromNumber(ref.toBook);
    if (!bookAbbr) return;
    this.nav.navigate({
      moduleId: loc.moduleId,
      book: bookAbbr,
      chapter: ref.toChapter,
      verse: ref.toVerseStart,
    });
  }

  trackByRef(_: number, r: CrossReference): string {
    return r.toReference;
  }
}
