import { Injectable, OnDestroy, signal } from "@angular/core";
import { classifyLayout, Layout } from "./layout-classification";

export type Orientation = "portrait" | "landscape";

@Injectable({ providedIn: "root" })
export class LayoutService implements OnDestroy {
  private readonly pointerQuery = window.matchMedia("(pointer: coarse)");

  readonly layout = signal<Layout>(
    classifyLayout(window.innerWidth, window.innerHeight),
  );
  readonly orientation = signal<Orientation>(this.readOrientation());
  readonly coarsePointer = signal<boolean>(this.pointerQuery.matches);

  private readonly onResize = () => this.update();
  private readonly onPointerChange = (e: MediaQueryListEvent) =>
    this.coarsePointer.set(e.matches);

  constructor() {
    window.addEventListener("resize", this.onResize);
    this.pointerQuery.addEventListener("change", this.onPointerChange);
  }

  ngOnDestroy(): void {
    window.removeEventListener("resize", this.onResize);
    this.pointerQuery.removeEventListener("change", this.onPointerChange);
  }

  private update(): void {
    this.layout.set(classifyLayout(window.innerWidth, window.innerHeight));
    this.orientation.set(this.readOrientation());
  }

  private readOrientation(): Orientation {
    return window.innerWidth >= window.innerHeight ? "landscape" : "portrait";
  }
}
