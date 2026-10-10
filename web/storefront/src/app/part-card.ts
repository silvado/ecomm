import { CurrencyPipe } from '@angular/common';
import { Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CONDITION_LABELS, partPath, photoSrcset, photoUrl, StorePartSummary } from './catalog';

@Component({
  selector: 'app-part-card',
  imports: [RouterLink, CurrencyPipe],
  template: `
    <a class="card" [routerLink]="path()">
      <div class="thumb">
        @if (part().coverPhotoId; as photo) {
          <img
            [src]="cover(photo)"
            [attr.srcset]="srcset(photo)"
            sizes="(max-width: 600px) 50vw, 240px"
            [alt]="part().title"
            width="300"
            height="300"
            loading="lazy"
            decoding="async"
          />
        } @else {
          <span class="no-photo" aria-hidden="true"></span>
        }
      </div>
      <span class="title">{{ part().title }}</span>
      <span class="meta">{{ conditionLabels[part().condition] }}</span>
      @if (part().available > 0) {
        <strong class="price">{{ part().price | currency: 'BRL' : 'symbol' : '1.2-2' }}</strong>
      } @else {
        <span class="unavailable">Indisponível</span>
      }
    </a>
  `,
})
export class PartCard {
  readonly part = input.required<StorePartSummary>();

  protected readonly conditionLabels = CONDITION_LABELS;
  protected readonly path = computed(() => partPath(this.part()));
  protected readonly cover = (photoId: string) => photoUrl(photoId, 300);
  protected readonly srcset = photoSrcset;
}
